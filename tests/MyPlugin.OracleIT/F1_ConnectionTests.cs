using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.OracleIT
{
    /// <summary>
    /// 기능 1. 접속 관리 → 연결.
    /// 접속 관리 창(저장: 직렬화·DPAPI 암호화) → DbHelperView 연결 흐름(연결 문자열 → DbSession.OpenAsync → 현재 스키마)을 실제 서버로 따라간다.
    /// </summary>
    [Collection(OracleCollection.Name)]
    public sealed class F1_ConnectionTests : OracleTestBase
    {
        public F1_ConnectionTests(OracleFixture db, ITestOutputHelper output) : base(db, output, "F1 접속") { }

        [OracleFact]
        public async Task F1_1_SavedProfile_DecryptsPassword_ConnectsAndReadsCurrentSchema()
        {
            // 접속 관리 창의 저장: 비밀번호 DPAPI 암호화 → JSON 직렬화(플러그인 설정에 저장되는 형태)
            var profile = Db.Profile();
            profile.ProtectedPassword = OracleConnectionStore.ProtectPassword(OracleFixture.Password);
            var json = OracleConnectionStore.Serialize(new[] { profile });
            Assert.DoesNotContain(OracleFixture.Password, json);

            // 다음 실행: 설정에서 읽고 검증 → 복호화 → 연결
            var loaded = OracleConnectionStore.Deserialize(json).Single();
            Assert.Empty(OracleConnectionStore.Validate(new[] { loaded }));
            Assert.Equal(profile.Id, loaded.Id);
            var password = OracleConnectionStore.UnprotectPassword(loaded.ProtectedPassword);
            Assert.Equal(OracleFixture.Password, password);

            var cs = OracleConnectionStore.BuildConnectionString(loaded, password, ShellLogic.ConnectTimeoutSeconds);
            using (var session = DbSession.CreateOracle(cs))
            {
                long openMs = 0, schemaMs = 0;
                await Timed(async () => { await session.OpenAsync(); return true; }, ms => openMs = ms);
                Assert.True(session.IsOpen);
                Assert.False(session.HasPendingChanges);

                var schemas = await Timed(() => session.QueryAsync(OracleMetadata.CurrentSchema(),
                    r => r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)), ms => schemaMs = ms);
                Assert.Equal(OracleFixture.User, schemas.Single());

                var version = await session.QueryAsync(Query("SELECT PRODUCT || VERSION FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%'"), r => r.GetString(0));
                Log("서버: " + version.Single());
                Log("연결 " + openMs + "ms, 현재 스키마 조회 " + schemaMs + "ms → " + schemas.Single());
                Log("저장 JSON 길이 " + json.Length + "자, 평문 비밀번호 미포함 · DPAPI 복호화 일치");

                await session.CloseAsync(false);
                Assert.False(session.IsOpen);
            }
        }

        [OracleFact]
        public async Task F1_2_PasswordWithSemicolonEqualsQuote_IsQuotedByBuilder()
        {
            var profile = Db.Profile("특수문자 비번");
            profile.UserId = OracleFixture.SpecialUser;
            var cs = OracleConnectionStore.BuildConnectionString(profile, OracleFixture.SpecialPassword, ShellLogic.ConnectTimeoutSeconds);
            using (var session = DbSession.CreateOracle(cs))
            {
                await session.OpenAsync();
                var schema = await session.QueryAsync(OracleMetadata.CurrentSchema(), r => r.GetString(0));
                Assert.Equal(OracleFixture.SpecialUser, schema.Single());
                Log("비밀번호 \"" + OracleFixture.SpecialPassword + "\"(; = ' 포함)로 연결 성공 → " + schema.Single());
            }
        }

        [OracleFact]
        public async Task F1_3_WrongPassword_FailsWithOra01017_Readably()
        {
            var cs = OracleConnectionStore.BuildConnectionString(Db.Profile(), "wrong-password", ShellLogic.ConnectTimeoutSeconds);
            using (var session = DbSession.CreateOracle(cs))
            {
                var started = DateTime.UtcNow;
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.OpenAsync());
                var described = DbSession.DescribeError(ex);
                Log("잘못된 비밀번호 → " + (DateTime.UtcNow - started).TotalMilliseconds.ToString("0") + "ms, 메시지: " + described);
                Assert.Contains("ORA-01017", described);
                Assert.False(session.IsOpen);
                Assert.False(DbSession.IsBrokenError(ex));
            }
        }

        // ODP.NET은 서비스명·포트 오류를 겉 ORA-50201로 감싸고 실제 원인은 안쪽에 둔다 — 사용자에게는 원인이 먼저 보여야 한다
        [OracleTheory]
        [InlineData("서비스명", null, 0, "no_such_service", "ORA-12514")]
        [InlineData("닫힌 포트", null, 1599, null, "ORA-12541")]
        [InlineData("없는 호스트", "no-such-host.invalid", 0, null, "ORA-12154")]
        public async Task F1_4_ConnectFailure_ShowsActualCause(string label, string host, int port, string service, string expectedCode)
        {
            var profile = Db.Profile();
            if (host != null) profile.Host = host;
            if (port != 0) profile.Port = port;
            if (service != null) profile.ServiceName = service;
            var cs = OracleConnectionStore.BuildConnectionString(profile, OracleFixture.Password, ShellLogic.ConnectTimeoutSeconds);
            using (var session = DbSession.CreateOracle(cs))
            {
                var started = DateTime.UtcNow;
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.OpenAsync());
                // 연결 흐름(DbHelperView·접속 테스트)이 쓰는 문장
                var described = ConnectionManagerLogic.DescribeConnectError(ex);
                Log(label + " → " + (DateTime.UtcNow - started).TotalMilliseconds.ToString("0") + "ms, 사용자에게 보이는 메시지: " + described);
                var chain = new System.Collections.Generic.List<string>();
                for (var e = ex; e != null; e = e.InnerException)
                    chain.Add(e.GetType().Name + ": " + e.Message.Split('\n')[0].Trim());
                Log("  예외 체인: " + string.Join(" ← ", chain));
                Assert.StartsWith(expectedCode + ":", described);
            }
        }

        [OracleFact]
        public async Task F1_6_InBandBreakSetLate_DoesNotBlockConnecting()
        {
            // 연결을 연 뒤에 취소 방식을 바꾸면 ODP.NET이 ORA-50099를 던진다 — 그때도 연결은 되어야 한다(실측에서 한 번 깨졌던 경로)
            var flag = typeof(DbSession).GetField("_breakConfigured", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(flag);
            flag.SetValue(null, 0);
            DbSession.UseInBandBreak();
            using (var session = await Db.OpenSessionAsync())
            {
                Assert.True(session.IsOpen);
                Log("연결을 연 뒤 취소 방식 설정을 다시 시도해도(ORA-50099 무시) 연결 정상");
            }
        }

        [OracleFact]
        public void F1_5_InvalidProfile_IsRejectedBeforeConnecting()
        {
            var profile = Db.Profile();
            profile.Host = " ";
            profile.Port = 70000;
            var ex = Assert.Throws<InvalidOperationException>(() => OracleConnectionStore.BuildConnectionString(profile, "", 10));
            Log("검증 오류: " + ex.Message.Replace(Environment.NewLine, " | "));
            Assert.Contains("호스트", ex.Message);
            Assert.Contains("포트", ex.Message);
            Assert.Contains("비밀번호", ex.Message);
        }
    }
}
