using System;
using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>접속 관리 대화상자의 순수 로직: 변경 판단, 이름 짓기, 비밀번호 안내, 검증 표시, 저장 목록, 연결 수 세기.</summary>
    public class ConnectionManagerLogicTests
    {
        private static OracleConnectionProfile Valid(string name = "개발", string id = null)
        {
            return new OracleConnectionProfile
            {
                Id = id ?? "id-" + name,
                Name = name,
                Host = "dev-db",
                Port = 1521,
                ServiceName = "ORCLPDB1",
                UserId = "scott",
                Color = ""
            };
        }

        private static List<ConnectionDraft> Drafts(IEnumerable<OracleConnectionProfile> originals)
        {
            return originals.Select(p => new ConnectionDraft(ConnectionManagerLogic.Clone(p))).ToList();
        }

        // ---------- 읽기·저장 형식 (ConnectionRepository가 쓰는 부분) ----------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void ParseStored_Empty_ReturnsEmptyList(string json)
        {
            Assert.Empty(ConnectionManagerLogic.ParseStored(json));
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("[null]")]
        [InlineData("{\"name\":\"객체 하나\"}")]
        public void ParseStored_Corrupt_ThrowsDocumentedMessage(string json)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ConnectionManagerLogic.ParseStored(json));
            Assert.StartsWith("저장된 접속 정보를 읽지 못했습니다: ", ex.Message);
            Assert.NotNull(ex.InnerException);
        }

        [Fact]
        public void ParseStored_NormalizesNullFields()
        {
            var p = Assert.Single(ConnectionManagerLogic.ParseStored("[{\"id\":\"a\",\"name\":null,\"host\":\"h\",\"color\":null,\"protectedPassword\":\"\"}]"));
            Assert.Equal("a", p.Id);
            Assert.Equal("", p.Name);
            Assert.Equal("", p.ServiceName);
            Assert.Equal("", p.Color);
            Assert.Null(p.ProtectedPassword);
            Assert.Equal(1521, p.Port);
        }

        [Fact]
        public void SerializeForSave_Invalid_ThrowsErrorsJoinedByNewLine()
        {
            var bad = new OracleConnectionProfile { Id = "x", Name = "A", Port = 0 };
            var ex = Assert.Throws<InvalidOperationException>(() => ConnectionManagerLogic.SerializeForSave(new[] { bad }));
            var lines = ex.Message.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            Assert.Equal(OracleConnectionStore.Validate(new[] { bad }), lines);
            Assert.True(lines.Length > 1);
        }

        [Fact]
        public void SerializeForSave_NullItem_Throws()
        {
            Assert.Throws<ArgumentException>(() => ConnectionManagerLogic.SerializeForSave(new OracleConnectionProfile[] { Valid(), null }));
        }

        [Fact]
        public void SerializeForSave_Valid_RoundTripsThroughParseStored()
        {
            var a = Valid("개발", "a");
            a.ProtectedPassword = "AQID";
            var b = Valid("운영", "b");
            b.ReadOnly = true;
            b.Color = "red";

            var back = ConnectionManagerLogic.ParseStored(ConnectionManagerLogic.SerializeForSave(new[] { a, b }));

            Assert.Equal(2, back.Count);
            Assert.True(ConnectionManagerLogic.SameProfile(a, back[0]));
            Assert.True(ConnectionManagerLogic.SameProfile(b, back[1]));
        }

        [Fact]
        public void StoredPassword_NoneIsNull_NotBase64IsDocumentedError()
        {
            Assert.Null(ConnectionManagerLogic.StoredPassword(null));
            Assert.Null(ConnectionManagerLogic.StoredPassword(Valid()));
            var blank = Valid();
            blank.ProtectedPassword = "  ";
            Assert.Null(ConnectionManagerLogic.StoredPassword(blank));

            // Base64가 아니면 DPAPI까지 가지 않고 FormatException이 난다(이 경로는 Linux에서도 확인할 수 있다).
            var broken = Valid();
            broken.ProtectedPassword = "이건 base64가 아님!";
            var ex = Assert.Throws<InvalidOperationException>(() => ConnectionManagerLogic.StoredPassword(broken));
            Assert.StartsWith("저장된 비밀번호를 풀 수 없습니다", ex.Message);
            Assert.IsType<FormatException>(ex.InnerException);
        }

        // ---------- 정리·복사·비교 ----------

        [Fact]
        public void Normalize_NullStringsBecomeEmpty_UnknownColorAndBlankPasswordCleared()
        {
            var p = new OracleConnectionProfile { Id = "a", Name = null, Host = null, ServiceName = null, UserId = null, Color = null, ProtectedPassword = "  " };

            var same = ConnectionManagerLogic.Normalize(p);

            Assert.Same(p, same);
            Assert.Equal("", p.Name);
            Assert.Equal("", p.Host);
            Assert.Equal("", p.ServiceName);
            Assert.Equal("", p.UserId);
            Assert.Equal("", p.Color);
            Assert.Null(p.ProtectedPassword);
            p.Color = "purple";
            Assert.Equal("", ConnectionManagerLogic.Normalize(p).Color);
            p.Color = "red";
            Assert.Equal("red", ConnectionManagerLogic.Normalize(p).Color);
        }

        [Fact]
        public void Clone_CopiesEveryStoredField_AsNewObject()
        {
            var p = Valid();
            p.ProtectedPassword = "AQID";
            p.ReadOnly = true;
            p.Color = "yellow";
            p.Port = 1522;

            var copy = ConnectionManagerLogic.Clone(p);

            Assert.NotSame(p, copy);
            Assert.True(ConnectionManagerLogic.SameProfile(p, copy));
            Assert.Equal("AQID", copy.ProtectedPassword);
            Assert.True(copy.ReadOnly);
            Assert.Equal("yellow", copy.Color);
            Assert.Equal(1522, copy.Port);
            copy.Host = "other";
            Assert.Equal("dev-db", p.Host);
        }

        [Fact]
        public void SameProfile_TreatsNullAndEmptyAlike_ButDetectsEachField()
        {
            var a = Valid();
            var b = Valid();
            a.Color = null;
            b.Color = "";
            a.ProtectedPassword = null;
            b.ProtectedPassword = "";
            Assert.True(ConnectionManagerLogic.SameProfile(a, b));

            var changes = new List<Action<OracleConnectionProfile>>
            {
                x => x.Id = "other",
                x => x.Name = "운영",
                x => x.Host = "prod-db",
                x => x.Port = 1522,
                x => x.ServiceName = "PROD",
                x => x.UserId = "hr",
                x => x.ProtectedPassword = "AQID",
                x => x.ReadOnly = true,
                x => x.Color = "red"
            };
            foreach (var change in changes)
            {
                var c = Valid();
                change(c);
                Assert.False(ConnectionManagerLogic.SameProfile(Valid(), c));
            }
            Assert.False(ConnectionManagerLogic.SameProfile(Valid(), null));
            Assert.True(ConnectionManagerLogic.SameProfile(null, null));
        }

        [Fact]
        public void SameProfile_IsCaseSensitive()
        {
            var b = Valid();
            b.UserId = "SCOTT";
            Assert.False(ConnectionManagerLogic.SameProfile(Valid(), b));
        }

        // ---------- 변경 판단 ----------

        [Fact]
        public void IsDirty_UnchangedCopy_False()
        {
            var originals = new List<OracleConnectionProfile> { Valid("개발"), Valid("운영") };
            Assert.False(ConnectionManagerLogic.IsDirty(originals, Drafts(originals)));
            Assert.False(ConnectionManagerLogic.IsDirty(new List<OracleConnectionProfile>(), new List<ConnectionDraft>()));
        }

        [Fact]
        public void IsDirty_EditAddDeleteReorderPassword_True()
        {
            var originals = new List<OracleConnectionProfile> { Valid("개발"), Valid("운영") };

            var edited = Drafts(originals);
            edited[1].Profile.Host = "x";
            Assert.True(ConnectionManagerLogic.IsDirty(originals, edited));

            var added = Drafts(originals);
            added.Add(new ConnectionDraft(Valid("검증")));
            Assert.True(ConnectionManagerLogic.IsDirty(originals, added));

            var deleted = Drafts(originals);
            deleted.RemoveAt(0);
            Assert.True(ConnectionManagerLogic.IsDirty(originals, deleted));

            var reordered = Drafts(originals);
            reordered.Reverse();
            Assert.True(ConnectionManagerLogic.IsDirty(originals, reordered));

            var typed = Drafts(originals);
            typed[0].NewPassword = "tiger";
            Assert.True(ConnectionManagerLogic.IsDirty(originals, typed));

            var cleared = Drafts(originals);
            cleared[0].ClearPassword = true;
            Assert.True(ConnectionManagerLogic.IsDirty(originals, cleared));
        }

        [Fact]
        public void IsDirty_EditThenRevert_False()
        {
            var originals = new List<OracleConnectionProfile> { Valid() };
            var drafts = Drafts(originals);
            drafts[0].Profile.Host = "x";
            drafts[0].NewPassword = "a";
            drafts[0].Profile.Host = "dev-db";
            drafts[0].NewPassword = "";
            Assert.False(ConnectionManagerLogic.IsDirty(originals, drafts));
        }

        [Fact]
        public void IsChanged_MarksNewEditedAndPasswordChangedOnly()
        {
            var originals = new List<OracleConnectionProfile> { Valid("개발"), Valid("운영") };
            var drafts = Drafts(originals);
            Assert.False(ConnectionManagerLogic.IsChanged(drafts[0], originals));

            drafts[1].Profile.ReadOnly = true;
            Assert.True(ConnectionManagerLogic.IsChanged(drafts[1], originals));
            Assert.False(ConnectionManagerLogic.IsChanged(drafts[0], originals));

            drafts[0].NewPassword = "tiger";
            Assert.True(ConnectionManagerLogic.IsChanged(drafts[0], originals));

            var fresh = new ConnectionDraft(Valid("새 접속", "new"));
            Assert.True(ConnectionManagerLogic.IsChanged(fresh, originals));
        }

        [Fact]
        public void IsChanged_FindsOriginalById_NotByPosition()
        {
            var originals = new List<OracleConnectionProfile> { Valid("개발"), Valid("운영") };
            var drafts = Drafts(originals);
            drafts.Reverse();
            Assert.False(ConnectionManagerLogic.IsChanged(drafts[0], originals));
            Assert.False(ConnectionManagerLogic.IsChanged(drafts[1], originals));
        }

        // ---------- 이름 짓기 ----------

        [Fact]
        public void NameForNew_FirstFreeNumber_IgnoringCaseAndSpaces()
        {
            Assert.Equal("새 접속", ConnectionManagerLogic.NameForNew(new string[0]));
            Assert.Equal("새 접속 2", ConnectionManagerLogic.NameForNew(new[] { " 새 접속 " }));
            Assert.Equal("새 접속 3", ConnectionManagerLogic.NameForNew(new[] { "새 접속", "새 접속 2", null }));
            Assert.Equal("새 접속 2", ConnectionManagerLogic.NameForNew(new[] { "새 접속", "새 접속 3" }));
        }

        [Fact]
        public void NameForCopy_AddsCopySuffix_ThenNumbers()
        {
            Assert.Equal("운영 복사", ConnectionManagerLogic.NameForCopy("운영", new[] { "운영" }));
            Assert.Equal("운영 복사 2", ConnectionManagerLogic.NameForCopy("운영", new[] { "운영", "운영 복사" }));
            Assert.Equal("Prod 복사 3", ConnectionManagerLogic.NameForCopy(" Prod ", new[] { "Prod", "PROD 복사", "prod 복사 2" }));
            Assert.Equal("새 접속 복사", ConnectionManagerLogic.NameForCopy("  ", new[] { "" }));
        }

        [Fact]
        public void NewNames_PassValidation()
        {
            var profiles = new List<OracleConnectionProfile> { Valid("새 접속", "a") };
            var added = Valid(ConnectionManagerLogic.NameForNew(profiles.Select(p => p.Name)), "b");
            profiles.Add(added);
            var copy = Valid(ConnectionManagerLogic.NameForCopy(added.Name, profiles.Select(p => p.Name)), "c");
            profiles.Add(copy);
            Assert.Empty(OracleConnectionStore.Validate(profiles));
        }

        // ---------- 비밀번호 안내 ----------

        [Fact]
        public void PasswordTexts_FollowState()
        {
            var stored = Valid();
            stored.ProtectedPassword = "AQID";
            var draft = new ConnectionDraft(stored);
            Assert.True(draft.HasStoredPassword);
            Assert.False(draft.PasswordChanged);
            Assert.Contains("저장된 비밀번호가 있습니다", ConnectionManagerLogic.PasswordState(draft));
            Assert.Equal("저장됨 — 바꾸려면 입력", ConnectionManagerLogic.PasswordPlaceholder(draft));

            draft.NewPassword = "tiger";
            Assert.Equal("저장하면 새 비밀번호를 암호화해 저장합니다.", ConnectionManagerLogic.PasswordState(draft));
            Assert.True(draft.PasswordChanged);

            draft.NewPassword = null;
            Assert.Equal("", draft.NewPassword);
            draft.ClearPassword = true;
            Assert.False(draft.HasStoredPassword);
            Assert.Equal("저장하면 비밀번호를 지웁니다. 연결할 때마다 입력합니다.", ConnectionManagerLogic.PasswordState(draft));
            Assert.Equal("저장하지 않으려면 비워 두기", ConnectionManagerLogic.PasswordPlaceholder(draft));

            var none = new ConnectionDraft(Valid());
            Assert.False(none.HasStoredPassword);
            Assert.Equal("저장된 비밀번호가 없습니다. 연결할 때 입력합니다.", ConnectionManagerLogic.PasswordState(none));
            Assert.Equal("저장하지 않으려면 비워 두기", ConnectionManagerLogic.PasswordPlaceholder(none));
        }

        // ---------- 검증 표시 ----------

        [Fact]
        public void InvalidIndexes_MarksMissingValuesBadPortAndDuplicates()
        {
            var noHost = Valid("A", "1");
            noHost.Host = " ";
            var badPort = Valid("B", "2");
            badPort.Port = 0;
            var ok = Valid("C", "3");
            var dupName1 = Valid("Prod", "4");
            var dupName2 = Valid(" prod ", "5");
            var dupId1 = Valid("E", "same");
            var dupId2 = Valid("F", "same");
            var noName = Valid("", "8");
            var list = new List<OracleConnectionProfile> { noHost, badPort, ok, dupName1, dupName2, dupId1, dupId2, noName };

            var invalid = ConnectionManagerLogic.InvalidIndexes(list);

            Assert.Equal(new[] { 0, 1, 3, 4, 5, 6, 7 }, invalid.OrderBy(i => i));
        }

        [Fact]
        public void InvalidIndexes_AgreesWithValidate_OnRandomLists()
        {
            var random = new Random(20261002);
            var names = new[] { "", " ", "개발", "운영", "운영 ", "DEV", "dev", null };
            var hosts = new[] { "", "h", null, " db " };
            var ports = new[] { 0, 1, 1521, 65535, 65536, -1 };
            var ids = new[] { "a", "b", "c", "a", "", null };
            for (var round = 0; round < 500; round++)
            {
                var list = new List<OracleConnectionProfile>();
                var count = random.Next(0, 5);
                for (var i = 0; i < count; i++)
                {
                    list.Add(new OracleConnectionProfile
                    {
                        Id = ids[random.Next(ids.Length)],
                        Name = names[random.Next(names.Length)],
                        Host = hosts[random.Next(hosts.Length)],
                        Port = ports[random.Next(ports.Length)],
                        ServiceName = random.Next(4) == 0 ? "" : "S",
                        UserId = random.Next(4) == 0 ? null : "u"
                    });
                }
                var hasErrors = OracleConnectionStore.Validate(list).Count > 0;
                Assert.Equal(hasErrors, ConnectionManagerLogic.InvalidIndexes(list).Count > 0);
            }
        }

        // ---------- 접속 테스트 ----------

        [Fact]
        public void TestInputError_ListsMissingFieldsAndPassword()
        {
            Assert.Null(ConnectionManagerLogic.TestInputError(Valid(), true));

            var empty = new OracleConnectionProfile { Port = 0 };
            Assert.Equal("호스트·포트·서비스명·사용자을(를) 입력하세요. 비밀번호를 입력하세요(테스트에만 쓰고, 저장 여부는 따로 정함).",
                ConnectionManagerLogic.TestInputError(empty, false));

            var noService = Valid();
            noService.ServiceName = "  ";
            Assert.Equal("서비스명을(를) 입력하세요.", ConnectionManagerLogic.TestInputError(noService, true));

            Assert.Equal("비밀번호를 입력하세요(테스트에만 쓰고, 저장 여부는 따로 정함).", ConnectionManagerLogic.TestInputError(Valid(), false));
        }

        [Fact]
        public void TestInputError_IgnoresName()
        {
            var unnamed = Valid("");
            Assert.Null(ConnectionManagerLogic.TestInputError(unnamed, true));
        }

        [Fact]
        public void TestSuccess_FormatsVersionAndSeconds()
        {
            Assert.Equal("접속 성공 · Oracle 23.4.0.24.05 · 0.2초", ConnectionManagerLogic.TestSuccess(" 23.4.0.24.05 ", TimeSpan.FromMilliseconds(230)));
            Assert.Equal("접속 성공 · 1.5초", ConnectionManagerLogic.TestSuccess(null, TimeSpan.FromMilliseconds(1500)));
            Assert.Equal("최대 10초, 풀링 끔", ConnectionManagerLogic.TestIdleText);
        }

        [Theory]
        [InlineData("ORA-12545: Network Transport: Unable to resolve connect hostname\nhttps://docs.oracle.com/error-help/db/ora-12545/", "ORA-12545: Network Transport: Unable to resolve connect hostname")]
        [InlineData("\n  \nORA-01017: invalid credential or not authorized; logon denied\r\n", "ORA-01017: invalid credential or not authorized; logon denied")]
        [InlineData("ORA-06550: line 1, column 7:\nPLS-00201: identifier 'X' must be declared\nORA-06550: line 1, column 7:\nPL/SQL: Statement ignored", "ORA-06550: line 1, column 7: PLS-00201: identifier 'X' must be declared")]
        [InlineData("a:\nb:\nc:\nd", "a: b: c:")]
        [InlineData("http://only.example/help", "")]
        [InlineData(null, "")]
        public void FirstLine_SkipsBlankAndHelpLines(string message, string expected)
        {
            Assert.Equal(expected, ConnectionManagerLogic.FirstLine(message));
        }

        [Fact]
        public void DescribeConnectError_BrokenNumberKeepsListenerMessage()
        {
            // 연결 끊김 번호라도 연결하기 전의 오류는 "다시 연결하세요"가 아니라 원래 이유가 맞다
            var refused = NewOracleException(12537, "ORA-12537: Network Session: End of file");
            var denied = NewOracleException(1017, "ORA-01017: invalid credential or not authorized; logon denied");

            Assert.Equal("ORA-12537: Network Session: End of file", ConnectionManagerLogic.DescribeConnectError(refused));
            Assert.StartsWith("DB 연결이 끊겼습니다", DbSession.DescribeError(refused));
            Assert.Equal(DbSession.DescribeError(denied), ConnectionManagerLogic.DescribeConnectError(denied));
        }

        [Fact]
        public void DescribeConnectError_GenericConnectFailure_ShowsListenerCause()
        {
            // 서비스명이 틀리면 ODP.NET은 겉에 ORA-50201만 둔다 — 접속 테스트·연결 메시지에는 실제 원인이 보여야 한다
            var error = new Exception("ORA-50201: Oracle Communication: Failed to connect to server or failed to parse connect string",
                new Exception("ORA-12514: TNS:listener does not currently know of service requested in connect descriptor"));

            Assert.Equal("ORA-12514: TNS:listener does not currently know of service requested in connect descriptor (ORA-50201)",
                ConnectionManagerLogic.DescribeConnectError(error));
        }

        /// <summary>ODP.NET의 OracleException은 공개 생성자가 없어 내부 생성자로 만든다(DbSessionTests와 같음).</summary>
        private static Oracle.ManagedDataAccess.Client.OracleException NewOracleException(int number, string message)
        {
            var type = typeof(Oracle.ManagedDataAccess.Client.OracleException);
            var constructor = type.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(string), typeof(string), typeof(string), typeof(int) }, null);
            Assert.True(constructor != null, "OracleException 내부 생성자를 찾지 못했습니다.");
            return (Oracle.ManagedDataAccess.Client.OracleException)constructor.Invoke(new object[] { number, "test", "test", message, -1 });
        }

        // ---------- 입력 칸·표시 ----------

        [Theory]
        [InlineData("1521", 1521)]
        [InlineData(" 1522 ", 1522)]
        [InlineData("0001521", 1521)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("abc", 0)]
        [InlineData("15 21", 0)]
        [InlineData("-1", 0)]
        [InlineData("+1521", 0)]
        [InlineData("1e3", 0)]
        [InlineData("99999999999", 0)]
        [InlineData("65536", 65536)]
        public void ParsePort_DigitsOnly(string text, int expected)
        {
            Assert.Equal(expected, ConnectionManagerLogic.ParsePort(text));
        }

        [Fact]
        public void Address_And_DisplayName()
        {
            var p = Valid();
            p.UserId = " scott ";
            Assert.Equal("scott@dev-db:1521/ORCLPDB1", ConnectionManagerLogic.Address(p));
            Assert.Equal("@:1521/", ConnectionManagerLogic.Address(new OracleConnectionProfile()));
            Assert.Equal("", ConnectionManagerLogic.Address(null));

            Assert.Equal("개발", ConnectionManagerLogic.DisplayName(Valid(" 개발 ")));
            Assert.Equal("(이름 없음)", ConnectionManagerLogic.DisplayName(Valid(" ")));
            Assert.Equal("(이름 없음)", ConnectionManagerLogic.DisplayName(null));
        }

        // ---------- 저장 목록 ----------

        [Fact]
        public void BuildProfilesToSave_EncryptsNewClearsOrKeepsPasswords()
        {
            var keep = Valid("A", "a");
            keep.ProtectedPassword = "KEEP";
            var change = Valid("B", "b");
            change.ProtectedPassword = "OLD";
            var clear = Valid("C", "c");
            clear.ProtectedPassword = "OLD";
            var originals = new List<OracleConnectionProfile> { keep, change, clear, Valid("D", "d") };
            var drafts = Drafts(originals);
            drafts[1].NewPassword = "tiger";
            drafts[2].ClearPassword = true;
            var protectedInputs = new List<string>();

            var saved = ConnectionManagerLogic.BuildProfilesToSave(drafts, pw => { protectedInputs.Add(pw); return "P(" + pw + ")"; });

            Assert.Equal(new[] { "tiger" }, protectedInputs);
            Assert.Equal("KEEP", saved[0].ProtectedPassword);
            Assert.Equal("P(tiger)", saved[1].ProtectedPassword);
            Assert.Null(saved[2].ProtectedPassword);
            Assert.Null(saved[3].ProtectedPassword);
            Assert.Equal(new[] { "a", "b", "c", "d" }, saved.Select(p => p.Id));
            Assert.All(saved.Zip(drafts, (s, d) => Tuple.Create(s, d)), pair => Assert.NotSame(pair.Item2.Profile, pair.Item1));
            Assert.Equal("OLD", drafts[1].Profile.ProtectedPassword);
        }

        [Fact]
        public void BuildProfilesToSave_NewPasswordWinsOverClear_AndTrimsText()
        {
            var p = Valid(" 운영 ");
            p.Host = " prod-db ";
            p.ServiceName = " PROD ";
            p.UserId = " hr ";
            p.ProtectedPassword = "OLD";
            var draft = new ConnectionDraft(p) { ClearPassword = true, NewPassword = "x" };

            var saved = ConnectionManagerLogic.BuildProfilesToSave(new[] { draft }, pw => "P").Single();

            Assert.Equal("P", saved.ProtectedPassword);
            Assert.Equal("운영", saved.Name);
            Assert.Equal("prod-db", saved.Host);
            Assert.Equal("PROD", saved.ServiceName);
            Assert.Equal("hr", saved.UserId);
        }

        [Fact]
        public void BuildProfilesToSave_RoundTripsThroughStore()
        {
            var p = Valid();
            p.ReadOnly = true;
            p.Color = "green";
            var saved = ConnectionManagerLogic.BuildProfilesToSave(Drafts(new[] { p }), pw => pw);

            var back = OracleConnectionStore.Deserialize(OracleConnectionStore.Serialize(saved)).Select(ConnectionManagerLogic.Normalize).ToList();

            Assert.True(ConnectionManagerLogic.SameProfile(saved[0], back[0]));
            Assert.False(ConnectionManagerLogic.IsDirty(back, Drafts(saved)));
        }

        [Fact]
        public void RemovedProfiles_ReturnsOriginalsMissingFromDrafts()
        {
            var originals = new List<OracleConnectionProfile> { Valid("A", "a"), Valid("B", "b"), Valid("C", "c") };
            var drafts = Drafts(originals);
            drafts.RemoveAt(1);
            drafts.Add(new ConnectionDraft(Valid("D", "d")));

            var removed = ConnectionManagerLogic.RemovedProfiles(originals, drafts);

            Assert.Equal(new[] { "b" }, removed.Select(p => p.Id));
            Assert.Empty(ConnectionManagerLogic.RemovedProfiles(originals, Drafts(originals)));
        }

        // ---------- 연결 수 ----------

        [Fact]
        public void UseCounts_CountPerId()
        {
            var counts = new ConnectionUseCounts();
            Assert.False(counts.Contains("a"));

            counts.Add("a");
            counts.Add("a");
            counts.Add("b");
            counts.Remove("a");
            Assert.True(counts.Contains("a"));
            Assert.True(counts.Contains("b"));

            counts.Remove("a");
            Assert.False(counts.Contains("a"));
            counts.Remove("a");
            counts.Add("a");
            Assert.True(counts.Contains("a"));
        }

        [Fact]
        public void UseCounts_IgnoreEmptyIds()
        {
            var counts = new ConnectionUseCounts();
            counts.Add(null);
            counts.Add("");
            counts.Remove(null);
            Assert.False(counts.Contains(null));
            Assert.False(counts.Contains(""));
        }

        [Fact]
        public void UseCounts_AreThreadSafe()
        {
            var counts = new ConnectionUseCounts();
            System.Threading.Tasks.Parallel.For(0, 2000, i => counts.Add("x"));
            System.Threading.Tasks.Parallel.For(0, 1999, i => counts.Remove("x"));
            Assert.True(counts.Contains("x"));
            counts.Remove("x");
            Assert.False(counts.Contains("x"));
        }
    }
}
