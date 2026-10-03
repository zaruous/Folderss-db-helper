using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>SqlDraftLogic: 저장할 탭 판단, 직렬화, 창별 파일 저장·넘겨받기·합치기·지우기.</summary>
    public class SqlDraftLogicTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "sql-drafts-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
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
            SqlDraftLogic.Save(_folder, "w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            var path = SqlDraftLogic.FileOf(_folder, "w1");
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));

            SqlDraftLogic.Save(_folder, "w1", new SqlDraftSet());
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Save_OverwritesPreviousContent()
        {
            SqlDraftLogic.Save(_folder, "w1", Set(DateTime.UtcNow, 0, "SELECT 1"));
            SqlDraftLogic.Save(_folder, "w1", Set(DateTime.UtcNow, 0, "SELECT 2"));

            var loaded = SqlDraftLogic.LoadOrphans(_folder, new string[0]);

            Assert.Single(loaded);
            Assert.Equal("SELECT 2", loaded[0].Value.Tabs.Single().Text);
        }

        [Fact]
        public void LoadOrphans_SkipsLiveWindowsAndBrokenFiles_OldestFirst()
        {
            SqlDraftLogic.Save(_folder, "newer", Set(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc), 0, "SELECT 'newer'"));
            SqlDraftLogic.Save(_folder, "older", Set(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), 0, "SELECT 'older'"));
            SqlDraftLogic.Save(_folder, "live", Set(DateTime.UtcNow, 0, "SELECT 'live'"));
            File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ 깨진");
            File.WriteAllText(Path.Combine(_folder, "other.txt"), "무시");

            var orphans = SqlDraftLogic.LoadOrphans(_folder, new[] { "live" });

            Assert.Equal(new[] { "older", "newer" }, orphans.Select(p => Path.GetFileNameWithoutExtension(p.Key)).ToArray());
            // 읽지 못한 파일은 지우지 않고 남겨 둔다
            Assert.True(File.Exists(Path.Combine(_folder, "broken.json")));
        }

        [Fact]
        public void LoadOrphans_MissingFolder_IsEmpty()
        {
            Assert.Empty(SqlDraftLogic.LoadOrphans(Path.Combine(_folder, "없음"), new string[0]));
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
            SqlDraftLogic.Save(_folder, "closed1", Set(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), 0, "SELECT 1"));
            SqlDraftLogic.Save(_folder, "closed2", Set(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), 0, "SELECT 2"));

            var orphans = SqlDraftLogic.LoadOrphans(_folder, new[] { "new" });
            var merged = SqlDraftLogic.Merge(orphans.Select(p => p.Value));
            SqlDraftLogic.Save(_folder, "new", merged);
            SqlDraftLogic.DeleteQuietly(orphans.Select(p => p.Key));

            Assert.Equal(new[] { "new.json" }, Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray());
            // 새 창이 닫히면 다음 창이 그대로 넘겨받는다
            var next = SqlDraftLogic.LoadOrphans(_folder, new string[0]);
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
        }
    }
}
