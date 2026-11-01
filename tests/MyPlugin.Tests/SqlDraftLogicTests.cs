using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>SqlDraftLogic: 저장할 탭 판단, 직렬화, 잠금(DPAPI), 창별 파일 저장·넘겨받기·합치기·지우기, 평문 이전, 풀지 못한 파일 보관, 접속 제외.</summary>
    public class SqlDraftLogicTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "sql-drafts-test-" + Guid.NewGuid().ToString("N"));

        /// <summary>Windows면 실제 DPAPI, 아니면(Linux CI) 시험용 잠금 — 저장·넘겨받기 로직은 어디서나 같게 확인한다.</summary>
        private static readonly ISqlDraftProtector Protector = OperatingSystem.IsWindows() ? DpapiDraftProtector.Instance : new FakeProtector("me");

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        /// <summary>시험용 잠금: 주인 표시 + 바이트 뒤집기. 다른 주인의 것은 CryptographicException(다른 Windows 사용자 흉내).</summary>
        private sealed class FakeProtector : ISqlDraftProtector
        {
            private readonly byte[] _owner;

            public FakeProtector(string owner)
            {
                _owner = Encoding.UTF8.GetBytes(owner.PadRight(8));
            }

            public byte[] Protect(byte[] data)
            {
                return _owner.Concat(data.Select(b => (byte)~b)).ToArray();
            }

            public byte[] Unprotect(byte[] data)
            {
                if (data.Length < _owner.Length || !data.Take(_owner.Length).SequenceEqual(_owner))
                    throw new CryptographicException("주인이 다름");
                return data.Skip(_owner.Length).Select(b => (byte)~b).ToArray();
            }
        }

        private static SqlDraftSet Set(DateTime savedAtUtc, int active, params string[] texts)
        {
            return new SqlDraftSet
            {
                SavedAtUtc = savedAtUtc,
                Active = active,
                Tabs = texts.Select((t, i) => new SqlDraft { DbId = "db" + i, Text = t, Caret = t.Length }).ToList()
            };
        }

        private void Save(string owner, SqlDraftSet set)
        {
            SqlDraftLogic.Save(_folder, owner, set, Protector);
        }

        private SqlDraftLoad Load(params string[] live)
        {
            return SqlDraftLogic.LoadOrphans(_folder, live, Protector);
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle))
                    return true;
            }
            return false;
        }

        [Theory]
        [InlineData("SELECT 1 FROM DUAL", "", true)]
        [InlineData("", "", false)]
        [InlineData("  \r\n\t", "", false)]
        [InlineData("-- 안내\r\n", "-- 안내\n", false)]          // 만들 때 글 그대로(줄바꿈 형식만 다름)
        [InlineData("-- 안내\nSELECT 1 FROM DUAL", "-- 안내\n", true)]
        [InlineData("SELECT 1", null, true)]
        public void IsWorthSaving_SkipsBlankAndUntouchedTabs(string text, string initial, bool expected)
        {
            Assert.Equal(expected, SqlDraftLogic.IsWorthSaving(text, initial));
        }

        [Fact]
        public void SerializeDeserialize_RoundTripsKoreanAndNewlines()
        {
            var set = Set(new DateTime(2026, 10, 3, 1, 2, 3, DateTimeKind.Utc), 1, "SELECT '한글' FROM DUAL;\r\n-- 주석", "BEGIN\n  NULL;\nEND;\n/");

            var back = SqlDraftLogic.Deserialize(SqlDraftLogic.Serialize(set));

            Assert.Equal(2, back.Tabs.Count);
            Assert.Equal(set.Tabs[0].Text, back.Tabs[0].Text);
            Assert.Equal(set.Tabs[1].Text, back.Tabs[1].Text);
            Assert.Equal("db1", back.Tabs[1].DbId);
            Assert.Equal(1, back.Active);
            Assert.Equal(set.SavedAtUtc, back.SavedAtUtc);
        }

        [Fact]
        public void SerializeDeserialize_KeepsFileFields()
        {
            var set = new SqlDraftSet
            {
                Tabs = new List<SqlDraft>
                {
                    new SqlDraft { Text = "SELECT 1", FilePath = @"D:\sql\q.sql", FileEncoding = "Cp949", Newline = "\n", Dirty = true },
                    new SqlDraft { Text = "SELECT 2" }
                }
            };

            var back = SqlDraftLogic.Deserialize(SqlDraftLogic.Serialize(set));

            Assert.Equal(@"D:\sql\q.sql", back.Tabs[0].FilePath);
            Assert.Equal("Cp949", back.Tabs[0].FileEncoding);
            Assert.Equal("\n", back.Tabs[0].Newline);
            Assert.True(back.Tabs[0].Dirty);
            Assert.Null(back.Tabs[1].FilePath);
            Assert.False(back.Tabs[1].Dirty);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{ 깨진")]
        [InlineData("[1,2,3]")]
        public void Deserialize_BrokenJson_ReturnsNull(string json)
        {
            Assert.Null(SqlDraftLogic.Deserialize(json));
        }

        [Fact]
        public void Deserialize_DropsBlankTabsAndKeepsActivePointingToSameTab()
        {
            var json = "{\"tabs\":[{\"text\":\"\"},null,{\"text\":\"SELECT 2\"},{\"text\":\"SELECT 3\"}],\"active\":3}";

            var set = SqlDraftLogic.Deserialize(json);

            Assert.Equal(new[] { "SELECT 2", "SELECT 3" }, set.Tabs.Select(t => t.Text).ToArray());
            Assert.Equal(1, set.Active);
        }

        [Fact]
        public void Save_WritesOwnerFile_AndEmptySetDeletesIt()
        {
            Save("w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            var path = SqlDraftLogic.FileOf(_folder, "w1");
            Assert.True(File.Exists(path));
            Assert.EndsWith(".dat", path);
            Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));

            Save("w1", new SqlDraftSet());
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Save_OverwritesPreviousContent()
        {
            Save("w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            Save("w1", Set(DateTime.UtcNow, 0, "SELECT 2"));

            var loaded = Load().Orphans;

            Assert.Single(loaded);
            Assert.Equal("SELECT 2", loaded[0].Value.Tabs.Single().Text);
        }

        [Fact]
        public void Save_ProtectedFile_HasNoPlaintextSqlOrPath_AndRoundTrips()
        {
            const string sql = "SELECT 'secret-token-1234' AS 비밀 FROM DUAL";
            const string file = @"D:\private\주민번호.sql";
            var set = Set(DateTime.UtcNow, 0, sql);
            set.Tabs[0].FilePath = file;

            Save("w1", set);

            var bytes = File.ReadAllBytes(SqlDraftLogic.FileOf(_folder, "w1"));
            foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode })
            {
                Assert.False(Contains(bytes, encoding.GetBytes("secret-token-1234")));
                Assert.False(Contains(bytes, encoding.GetBytes("주민번호")));
                Assert.False(Contains(bytes, encoding.GetBytes("SELECT")));
            }
            var back = Load().Orphans.Single().Value.Tabs.Single();
            Assert.Equal(sql, back.Text);
            Assert.Equal(file, back.FilePath);
        }

        [Fact]
        public void Dpapi_RoundTrip_OnWindows()
        {
            if (!OperatingSystem.IsWindows())
                return;
            var bytes = SqlDraftLogic.Encode(Set(DateTime.UtcNow, 0, "SELECT '한글' FROM DUAL"), DpapiDraftProtector.Instance);
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("한글")));
            Assert.Equal("SELECT '한글' FROM DUAL", SqlDraftLogic.Decode(bytes, DpapiDraftProtector.Instance).Tabs.Single().Text);
        }

        [Fact]
        public void Save_ProtectFails_KeepsPreviousFile()
        {
            Save("w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            var path = SqlDraftLogic.FileOf(_folder, "w1");
            var before = File.ReadAllBytes(path);

            // 잠근 것을 풀 수 없는 잠금(키 바뀜 등) — 쓰기 전에 확인에서 멈춰 이전 파일이 남는다
            var broken = new BrokenProtector();
            Assert.ThrowsAny<Exception>(() => SqlDraftLogic.Save(_folder, "w1", Set(DateTime.UtcNow, 0, "SELECT 2"), broken));

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
            Assert.Equal("SELECT 1", Load().Orphans.Single().Value.Tabs.Single().Text);
        }

        private sealed class BrokenProtector : ISqlDraftProtector
        {
            public byte[] Protect(byte[] data)
            {
                return data.Select(b => (byte)~b).ToArray();
            }

            public byte[] Unprotect(byte[] data)
            {
                throw new CryptographicException("풀 수 없음");
            }
        }

        [Fact]
        public void LoadOrphans_SkipsLiveWindows_OldestFirst()
        {
            Save("newer", Set(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc), 0, "SELECT 'newer'"));
            Save("older", Set(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), 0, "SELECT 'older'"));
            Save("live", Set(DateTime.UtcNow, 0, "SELECT 'live'"));
            File.WriteAllText(Path.Combine(_folder, "other.txt"), "무시");

            var load = Load("live");

            Assert.Equal(new[] { "older", "newer" }, load.Orphans.Select(p => Path.GetFileNameWithoutExtension(p.Key)).ToArray());
            Assert.Empty(load.Broken);
            Assert.True(File.Exists(Path.Combine(_folder, "other.txt")));
        }

        [Fact]
        public void LoadOrphans_MissingFolder_IsEmpty()
        {
            var load = SqlDraftLogic.LoadOrphans(Path.Combine(_folder, "없음"), new string[0], Protector);
            Assert.Empty(load.Orphans);
            Assert.Empty(load.Broken);
        }

        [Fact]
        public void Migration_PlaintextJson_IsAdopted_ResavedProtected_AndPlaintextRemoved()
        {
            // 이전 버전이 남긴 평문 파일
            Directory.CreateDirectory(_folder);
            var legacy = Path.Combine(_folder, "oldwindow.json");
            File.WriteAllText(legacy, SqlDraftLogic.Serialize(Set(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 0, "SELECT 'plain-secret' FROM DUAL")), new UTF8Encoding(false));

            // 새 창의 넘겨받기: 읽기 → 자기 파일에 잠가 저장 → 원래 파일 지우기
            var load = Load("new");
            Assert.Empty(load.Broken);
            Assert.Equal(legacy, load.Orphans.Single().Key);
            Save("new", SqlDraftLogic.Merge(load.Orphans.Select(p => p.Value)));
            SqlDraftLogic.DeleteQuietly(load.Orphans.Select(p => p.Key));

            Assert.False(File.Exists(legacy));
            Assert.Equal(new[] { "new.dat" }, Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray());
            Assert.False(Contains(File.ReadAllBytes(SqlDraftLogic.FileOf(_folder, "new")), Encoding.UTF8.GetBytes("plain-secret")));
            Assert.Equal("SELECT 'plain-secret' FROM DUAL", Load().Orphans.Single().Value.Tabs.Single().Text);
        }

        [Fact]
        public void LoadOrphans_UndecryptableFile_IsMovedToBrokenFolder_NotDeleted()
        {
            // 다른 Windows 사용자가 잠근 파일(이 사용자로는 풀지 못함)
            var other = new FakeProtector("other");
            var bytes = SqlDraftLogic.Encode(Set(DateTime.UtcNow, 0, "SELECT 'other user'"), other);
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(Path.Combine(_folder, "foreign.dat"), bytes);

            var load = SqlDraftLogic.LoadOrphans(_folder, new string[0], new FakeProtector("me"));

            Assert.Empty(load.Orphans);
            var broken = load.Broken.Single();
            Assert.StartsWith("foreign-", Path.GetFileName(broken.Path));
            Assert.Contains("다른 Windows 사용자", broken.Reason);
            Assert.Equal(Path.Combine(_folder, SqlDraftLogic.BrokenFolderName), Path.GetDirectoryName(broken.Path));
            Assert.Equal(bytes, File.ReadAllBytes(broken.Path));       // 내용 그대로 보관
            Assert.False(File.Exists(Path.Combine(_folder, "foreign.dat")));
            // 보관한 파일은 다음에 다시 넘겨받지 않는다(같은 알림이 되풀이되지 않음)
            Assert.DoesNotContain(Load().Broken, b => b.Path.Contains("foreign"));
            Assert.Contains(broken.Path, SqlDraftLogic.BrokenMessage(broken));
        }

        [Theory]
        [InlineData("garbage.dat", "아무 바이트")]
        [InlineData("garbage.json", "{ 깨진")]
        public void LoadOrphans_CorruptFile_IsKeptAsBackup_WithNotice(string name, string content)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, name), content);
            Save("ok", Set(DateTime.UtcNow, 0, "SELECT 1"));

            var load = Load();

            Assert.Equal("SELECT 1", load.Orphans.Single().Value.Tabs.Single().Text);
            var broken = load.Broken.Single();
            Assert.True(File.Exists(broken.Path));
            Assert.Equal(content, File.ReadAllText(broken.Path));
            Assert.False(File.Exists(Path.Combine(_folder, name)));
            Assert.StartsWith("임시 저장한 SQL 파일 하나를 되살리지 못했습니다(", SqlDraftLogic.BrokenMessage(broken));
        }

        [Fact]
        public void LoadOrphans_TruncatedProtectedFile_IsBroken()
        {
            Save("w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            var path = SqlDraftLogic.FileOf(_folder, "w1");
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.Take(bytes.Length / 2).ToArray());

            var load = Load();

            Assert.Empty(load.Orphans);
            Assert.Single(load.Broken);
        }

        [Fact]
        public void WithoutExcluded_DropsExcludedConnectionTabs_AndKeepsActive()
        {
            var set = Set(DateTime.UtcNow, 2, "A", "B", "C");
            set.Tabs.Add(new SqlDraft { DbId = null, Text = "대상 없음" });

            var kept = SqlDraftLogic.WithoutExcluded(set, new[] { "db1" });

            Assert.Equal(new[] { "A", "C", "대상 없음" }, kept.Tabs.Select(t => t.Text).ToArray());
            Assert.Equal(1, kept.Active);
            Assert.Equal(-1, SqlDraftLogic.WithoutExcluded(Set(DateTime.UtcNow, 0, "A"), new[] { "db0" }).Active);
            Assert.Same(set, SqlDraftLogic.WithoutExcluded(set, new string[0]));
        }

        [Fact]
        public void ExcludedConnection_IsNotSaved()
        {
            var set = Set(DateTime.UtcNow, 0, "SELECT 'keep'", "SELECT 'excluded-secret'");

            Save("w1", SqlDraftLogic.WithoutExcluded(set, new[] { "db1" }));

            Assert.Equal(new[] { "SELECT 'keep'" }, Load().Orphans.Single().Value.Tabs.Select(t => t.Text).ToArray());
            // 모든 탭이 제외면 파일을 남기지 않는다
            Save("w1", SqlDraftLogic.WithoutExcluded(set, new[] { "db0", "db1" }));
            Assert.False(File.Exists(SqlDraftLogic.FileOf(_folder, "w1")));
        }

        [Fact]
        public void Clear_RemovesDraftsLegacyTempAndBroken_KeepsOtherFiles()
        {
            Save("w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            File.WriteAllText(Path.Combine(_folder, "old.json"), "{}");
            File.WriteAllText(Path.Combine(_folder, "w2.dat.tmp"), "x");
            File.WriteAllText(Path.Combine(_folder, "bad.dat"), "x");
            Load();      // bad.dat → broken 폴더
            File.WriteAllText(Path.Combine(_folder, "note.txt"), "남김");

            Assert.True(SqlDraftLogic.Clear(_folder) >= 4);

            Assert.Equal(new[] { "note.txt" }, Directory.GetFiles(_folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
            Assert.Equal(0, SqlDraftLogic.Clear(Path.Combine(_folder, "없음")));
        }

        [Fact]
        public void Merge_KeepsOrderAndUsesNewestActiveTab()
        {
            var older = Set(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 1, "A1", "A2");
            var newer = Set(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), 0, "B1");

            var merged = SqlDraftLogic.Merge(new[] { older, new SqlDraftSet(), newer });

            Assert.Equal(new[] { "A1", "A2", "B1" }, merged.Tabs.Select(t => t.Text).ToArray());
            Assert.Equal(2, merged.Active);
            Assert.Equal(newer.SavedAtUtc, merged.SavedAtUtc);
        }

        [Fact]
        public void Merge_NoActive_IsMinusOne()
        {
            Assert.Equal(-1, SqlDraftLogic.Merge(new[] { Set(DateTime.UtcNow, -1, "A") }).Active);
            Assert.Empty(SqlDraftLogic.Merge(null).Tabs);
        }

        [Fact]
        public void AdoptFlow_SaveOwnThenDeleteOrphans_LeavesOnlyOwnFile()
        {
            // 닫힌 창 두 개가 남긴 파일을 새 창이 넘겨받는 순서: 읽기 → 자기 파일에 저장 → 원래 파일 지우기
            Save("closed1", Set(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 0, "SELECT 1"));
            Save("closed2", Set(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), 0, "SELECT 2"));

            var orphans = Load("new").Orphans;
            var merged = SqlDraftLogic.Merge(orphans.Select(p => p.Value));
            Save("new", merged);
            SqlDraftLogic.DeleteQuietly(orphans.Select(p => p.Key));

            Assert.Equal(new[] { "new.dat" }, Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray());
            // 새 창이 닫히면 다음 창이 그대로 넘겨받는다
            var next = Load().Orphans;
            Assert.Equal(new[] { "SELECT 1", "SELECT 2" }, next.Single().Value.Tabs.Select(t => t.Text).ToArray());
        }

        [Fact]
        public void DeleteQuietly_IgnoresMissingFiles()
        {
            SqlDraftLogic.DeleteQuietly(new[] { Path.Combine(_folder, "없는파일.json") });
            SqlDraftLogic.DeleteQuietly(null);
        }

        [Fact]
        public void Messages()
        {
            Assert.Equal("임시 저장한 SQL 탭 3개를 되살렸습니다.", SqlDraftLogic.RestoredMessage(3));
            Assert.StartsWith("SQL을 임시 저장하지 못했습니다: ", SqlDraftLogic.SaveFailedMessage("디스크"));
            Assert.StartsWith("임시 저장 파일 2개를 지웠습니다.", SqlDraftLogic.ClearedMessage(2));
        }
    }
}
