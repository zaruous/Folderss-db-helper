using System;
using System.Collections.Generic;
using MyPlugin;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace MyPlugin.Tests
{
    public class OracleConnectionStoreTests
    {
        private static OracleConnectionProfile Valid(string name = "운영")
        {
            return new OracleConnectionProfile { Name = name, Host = "db.local", Port = 1521, ServiceName = "ORCL", UserId = "scott" };
        }

        [Fact]
        public void SerializeDeserialize_RoundTrip_KeepsFieldsAndStoresOnlyProtectedPassword()
        {
            var profile = Valid();
            profile.ProtectedPassword = "AQID";

            var json = OracleConnectionStore.Serialize(new[] { profile });
            var back = OracleConnectionStore.Deserialize(json);

            Assert.Single(back);
            Assert.Equal("운영", back[0].Name);
            Assert.Equal("db.local", back[0].Host);
            Assert.Equal(1521, back[0].Port);
            Assert.Equal("ORCL", back[0].ServiceName);
            Assert.Equal("scott", back[0].UserId);
            Assert.Equal("AQID", back[0].ProtectedPassword);
            Assert.DoesNotContain("\"password\"", json);
        }

        [Fact]
        public void Deserialize_OldFormatWithoutId_AssignsStableIdAndDefaults()
        {
            var back = OracleConnectionStore.Deserialize("[{\"name\":\"운영\",\"host\":\"h\",\"port\":1521,\"serviceName\":\"S\",\"userId\":\"u\"}]");

            var p = Assert.Single(back);
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.False(p.ReadOnly);
            Assert.Equal("", p.Color);
            var again = OracleConnectionStore.Deserialize(OracleConnectionStore.Serialize(back));
            Assert.Equal(p.Id, Assert.Single(again).Id);
        }

        [Fact]
        public void Deserialize_OldSqlServerProfile_KeepsPreviousOptionalTrustBehaviour()
        {
            // 암호화 설정이 없던 이전 버전의 SQL Server 접속: 갑자기 붙지 못하지 않게 이전 동작(Optional + 신뢰)으로 읽는다
            var back = OracleConnectionStore.Deserialize("[{\"name\":\"MS\",\"provider\":\"mssql\",\"host\":\"h\",\"port\":1433,\"serviceName\":\"db\",\"userId\":\"sa\"}]");

            var p = Assert.Single(back);
            Assert.Equal("optional", p.SqlEncrypt);
            Assert.True(p.SqlTrustServerCertificate);
            Assert.NotNull(SqlServerSecurity.Hint(p));
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(OracleConnectionStore.BuildConnectionString(p, "pw", 5));
            Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, b.Encrypt);
            Assert.True(b.TrustServerCertificate);

            // 저장하면 값을 명시하므로 다시 읽어도 같다
            var json = OracleConnectionStore.Serialize(back);
            Assert.Contains("\"sqlEncrypt\":\"optional\"", json);
            Assert.Contains("\"sqlTrustServerCertificate\":true", json);
            var again = Assert.Single(OracleConnectionStore.Deserialize(json));
            Assert.True(p.SameAs(again));
        }

        [Fact]
        public void Serialize_NewSqlServerProfile_StoresSecureDefaultsExplicitly()
        {
            // 새 접속(값 없음)을 저장하면 필수 + 검증을 명시한다 — 빈 값으로 두면 다음에 이전 형식으로 읽힌다
            var p = Valid("MS");
            p.Provider = "mssql";
            var json = OracleConnectionStore.Serialize(new[] { p });
            Assert.Null(p.SqlEncrypt); // 넘긴 객체는 그대로
            Assert.Contains("\"sqlEncrypt\":\"mandatory\"", json);
            Assert.Contains("\"sqlTrustServerCertificate\":false", json);
            Assert.DoesNotContain("sqlHostNameInCertificate", json);

            var back = Assert.Single(OracleConnectionStore.Deserialize(json));
            Assert.Equal("mandatory", back.SqlEncrypt);
            Assert.False(back.SqlTrustServerCertificate);
            Assert.Null(SqlServerSecurity.Hint(back));
        }

        [Fact]
        public void SerializeDeserialize_SqlServerSecurity_RoundTrip_AndOtherProvidersOmitFields()
        {
            var ms = Valid("MS");
            ms.Provider = "mssql";
            ms.SqlEncrypt = "strict";
            ms.SqlHostNameInCertificate = " sql.corp.local ";
            var ora = Valid("ORA");
            ora.SqlEncrypt = "optional"; // 공급자를 바꾼 흔적 — 저장하지 않는다
            ora.SqlTrustServerCertificate = true;

            var json = OracleConnectionStore.Serialize(new[] { ms, ora });
            var back = OracleConnectionStore.Deserialize(json);

            Assert.Equal("strict", back[0].SqlEncrypt);
            Assert.False(back[0].SqlTrustServerCertificate);
            Assert.Equal("sql.corp.local", back[0].SqlHostNameInCertificate);
            Assert.Null(back[1].SqlEncrypt);
            Assert.Null(back[1].SqlTrustServerCertificate);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "sqlEncrypt"));

            // 모르는 값(다음 버전 등)은 약해지지 않게 필수로
            var unknown = Assert.Single(OracleConnectionStore.Deserialize("[{\"name\":\"MS\",\"provider\":\"mssql\",\"host\":\"h\",\"port\":1433,\"serviceName\":\"db\",\"userId\":\"sa\",\"sqlEncrypt\":\"future\"}]"));
            Assert.Equal("mandatory", unknown.SqlEncrypt);
            Assert.Null(unknown.SqlTrustServerCertificate);
        }

        [Fact]
        public void CopyAndSameAs_IncludeSqlServerSecurity()
        {
            var a = Valid();
            a.Provider = "mssql";
            a.SqlEncrypt = "mandatory";
            a.SqlTrustServerCertificate = true;
            a.SqlHostNameInCertificate = "h";
            var b = a.Copy();
            Assert.True(a.SameAs(b));
            b.SqlTrustServerCertificate = false;
            Assert.False(a.SameAs(b));
            b = a.Copy(); b.SqlEncrypt = "strict";
            Assert.False(a.SameAs(b));
            b = a.Copy(); b.SqlHostNameInCertificate = "x";
            Assert.False(a.SameAs(b));
        }

        [Fact]
        public void SerializeDeserialize_KeepsReadOnlyAndColor_UnknownColorBecomesNone()
        {
            var a = Valid("A"); a.ReadOnly = true; a.Color = "red";
            var b = Valid("B"); b.Color = "purple";

            var back = OracleConnectionStore.Deserialize(OracleConnectionStore.Serialize(new[] { a, b }));

            Assert.True(back[0].ReadOnly);
            Assert.Equal("red", back[0].Color);
            Assert.Equal("", back[1].Color);
        }

        [Fact]
        public void Validate_DuplicateIds_Reports()
        {
            var a = Valid("A"); var b = Valid("B");
            a.Id = b.Id = "same";
            Assert.Contains(OracleConnectionStore.Validate(new[] { a, b }), e => e.Contains("ID"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Deserialize_Empty_ReturnsEmptyList(string json)
        {
            Assert.Empty(OracleConnectionStore.Deserialize(json));
        }

        [Fact]
        public void Deserialize_Corrupt_Throws()
        {
            Assert.ThrowsAny<Exception>(() => OracleConnectionStore.Deserialize("{ not json"));
        }

        [Fact]
        public void Validate_ValidProfile_NoErrors()
        {
            Assert.Empty(OracleConnectionStore.Validate(new[] { Valid() }));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Validate_BadPort_Reports(int port)
        {
            var p = Valid();
            p.Port = port;
            Assert.Contains(OracleConnectionStore.Validate(new[] { p }), e => e.Contains("포트"));
        }

        [Fact]
        public void Validate_MissingFields_ReportsEach()
        {
            var errors = OracleConnectionStore.Validate(new[] { new OracleConnectionProfile { Name = "x", Port = 1521 } });
            Assert.Contains(errors, e => e.Contains("호스트"));
            Assert.Contains(errors, e => e.Contains("서비스명"));
            Assert.Contains(errors, e => e.Contains("사용자"));
        }

        [Fact]
        public void Validate_DuplicateNamesIgnoringCase_Reports()
        {
            var errors = OracleConnectionStore.Validate(new List<OracleConnectionProfile> { Valid("Prod"), Valid("prod ") });
            Assert.Contains(errors, e => e.Contains("같은 이름"));
        }

        [Fact]
        public void BuildConnectionString_PasswordWithSeparators_IsNotInterpretedAsOtherKeys()
        {
            const string password = "p;Pooling=true;User Id=sys\"'";

            var cs = OracleConnectionStore.BuildConnectionString(Valid(), password, 10);
            var parsed = new OracleConnectionStringBuilder(cs);

            Assert.Equal(password, parsed.Password);
            Assert.Equal("scott", parsed.UserID);
            Assert.False(parsed.Pooling);
            Assert.Equal("db.local:1521/ORCL", parsed.DataSource);
            Assert.Equal(10, parsed.ConnectionTimeout);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void BuildConnectionString_NoPassword_Throws(string password)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => OracleConnectionStore.BuildConnectionString(Valid(), password, 10));
            Assert.Contains("비밀번호", ex.Message);
        }
    }
}
