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
