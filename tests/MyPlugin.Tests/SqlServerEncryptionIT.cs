using System;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// SQL Server 암호화·인증서 검증 설정을 실제 서버에서 확인한다(없으면 건너뜀). 서버는 자체 서명 인증서여야 한다(도커 기본).
    /// 환경 변수: SCHEMA_IT_MSSQL (예: localhost:1433/tempdb), SCHEMA_IT_MSSQL_USER(sa), SCHEMA_IT_MSSQL_PW — TableSchemaIT와 같다.
    /// </summary>
    public class SqlServerEncryptionIT
    {
        private const string Prefix = "SCHEMA_IT_MSSQL";

        sealed class MssqlFact : FactAttribute
        {
            public MssqlFact()
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix)))
                    Skip = Prefix + "가 없어 실제 DB 시험을 건너뜁니다.";
            }
        }

        static OracleConnectionProfile Profile(string encrypt, bool trust, string hostName = null)
        {
            var dsn = Environment.GetEnvironmentVariable(Prefix).Trim();
            var hostPort = dsn.Split('/')[0];
            return new OracleConnectionProfile
            {
                Id = "ms", Name = "MS 시험", Provider = "mssql",
                Host = hostPort.Split(':')[0], Port = int.Parse(hostPort.Split(':')[1]), ServiceName = dsn.Split('/')[1],
                UserId = Environment.GetEnvironmentVariable(Prefix + "_USER") ?? "sa",
                SqlEncrypt = encrypt, SqlTrustServerCertificate = trust, SqlHostNameInCertificate = hostName
            };
        }

        static string Password { get { return Environment.GetEnvironmentVariable(Prefix + "_PW") ?? ""; } }

        /// <summary>접속 테스트([접속 테스트] 버튼)와 같은 순서로 열어 보고, 실패면 화면에 보일 문장.</summary>
        static string TryOpen(OracleConnectionProfile profile)
        {
            try
            {
                var provider = DbProviders.For(profile);
                using (var connection = provider.CreateConnection(OracleConnectionStore.BuildConnectionString(profile, Password, 10)))
                    connection.Open();
                return null;
            }
            catch (Exception ex)
            {
                return ConnectionManagerLogic.DescribeConnectError(ex, Password);
            }
        }

        [MssqlFact]
        public void Mandatory_Validate_FailsOnSelfSignedCertificate_WithHint()
        {
            var error = TryOpen(Profile("mandatory", false));
            Assert.Equal(SqlServerSecurity.UntrustedCertificate, error);
            Assert.DoesNotContain(Password, error);
        }

        [MssqlFact]
        public void Mandatory_TrustServerCertificate_Succeeds()
        {
            Assert.Null(TryOpen(Profile("mandatory", true)));
        }

        [MssqlFact]
        public void Optional_Trust_LegacyBehaviour_Succeeds()
        {
            Assert.Null(TryOpen(Profile("optional", true)));
        }

        [MssqlFact]
        public void Strict_FailsOnSelfSignedCertificate()
        {
            // 엄격은 신뢰 설정을 무시하고 항상 검증한다
            var error = TryOpen(Profile("strict", true));
            Assert.NotNull(error);
            Assert.DoesNotContain(Password, error);
        }
    }
}
