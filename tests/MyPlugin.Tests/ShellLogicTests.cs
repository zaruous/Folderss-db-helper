using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>ShellLogic: 툴바 문장(고른 DB·모드 표시), 연결·끊기 메시지, 가져올 행, 접속 목록 다시 읽기.</summary>
    public class ShellLogicTests
    {
        private static OracleConnectionProfile Profile(string id = "dev", string name = "개발", bool readOnly = false)
        {
            return new OracleConnectionProfile
            {
                Id = id, Name = name, Host = "dev-db", Port = 1521, ServiceName = "ORCLPDB1", UserId = "scott", ReadOnly = readOnly
            };
        }

        [Fact]
        public void Address_UserHostPortService_Trimmed()
        {
            var profile = Profile();
            profile.Host = " dev-db ";

            Assert.Equal("scott@dev-db:1521/ORCLPDB1", ShellLogic.Address(profile));
            Assert.Equal("", ShellLogic.Address(null));
        }

        [Fact]
        public void SelectedDbText_NameAndAddressOrHint()
        {
            Assert.Equal("개발 · scott@dev-db:1521/ORCLPDB1", ShellLogic.SelectedDbText(Profile()));
            Assert.Equal("트리에서 DB를 고르세요", ShellLogic.SelectedDbText(null));
        }

        [Fact]
        public void ModeText_EveryState()
        {
            bool danger;

            Assert.Equal("대상 DB 없음", ShellLogic.ModeText(null, null, out danger));
            Assert.False(danger);
            Assert.Equal("개발 · 자동 커밋 끔", ShellLogic.ModeText(Profile(), null, out danger));
            Assert.False(danger);
            Assert.Equal("운영 · 읽기 전용", ShellLogic.ModeText(Profile("prod", "운영", readOnly: true), null, out danger));
            Assert.False(danger);
            Assert.Equal("개발 · 커밋 대기 3행", ShellLogic.ModeText(Profile(), "커밋 대기 3행", out danger));
            Assert.True(danger);
        }

        [Fact]
        public void ModeText_ReadOnlyWithForUpdateLocks_PendingWins()
        {
            bool danger;

            var text = ShellLogic.ModeText(Profile("prod", "운영", readOnly: true), "커밋 대기 0행", out danger);

            Assert.Equal("운영 · 커밋 대기 0행", text);
            Assert.True(danger);
        }

        [Fact]
        public void ConnectedMessage_AddressAndReadOnly()
        {
            Assert.Equal("연결됨: scott@dev-db:1521/ORCLPDB1", ShellLogic.ConnectedMessage(Profile()));
            Assert.Equal("연결됨: scott@dev-db:1521/ORCLPDB1 · 읽기 전용", ShellLogic.ConnectedMessage(Profile(readOnly: true)));
        }

        [Fact]
        public void ReconnectAndBrokenMessages_MentionLostPendingOnlyWhenThereWasSome()
        {
            Assert.Equal("DB 연결이 끊겨 다시 연결합니다.", ShellLogic.ReconnectMessage(null));
            Assert.Equal("DB 연결이 끊겨 다시 연결합니다. 커밋하지 않은 변경(커밋 대기 2행)은 서버에서 이미 사라졌습니다.", ShellLogic.ReconnectMessage("커밋 대기 2행"));
            Assert.Contains("커밋 대기(행 수 모름)", ShellLogic.BrokenDisconnectMessage("커밋 대기(행 수 모름)"));
            Assert.DoesNotContain("변경", ShellLogic.BrokenDisconnectMessage(null));
        }

        [Fact]
        public void BusyAndCloseFailedMessages()
        {
            Assert.Equal("같은 DB에서 다른 실행이 진행 중입니다. 끝난 뒤 다시 커밋하세요 (DB마다 세션 1개).", ShellLogic.BusyMessage("커밋"));
            Assert.StartsWith("커밋하지 못해 변경이 저장되지 않았습니다: ORA-02091", ShellLogic.CloseFailedMessage(true, "ORA-02091"));
            Assert.StartsWith("롤백하지 못했습니다: ORA-03113", ShellLogic.CloseFailedMessage(false, "ORA-03113"));
        }

        [Theory]
        [InlineData(1000, 1000)]
        [InlineData("5000", 5000)]
        [InlineData("abc", 200)]
        [InlineData(null, 200)]
        [InlineData(0, 200)]
        [InlineData(-5, 200)]
        public void ParseFetchCount_KnownOrDefault(object value, int expected)
        {
            Assert.Equal(expected, ShellLogic.ParseFetchCount(value));
        }

        [Fact]
        public void FetchCounts_DefaultFirst()
        {
            Assert.Equal(new[] { 200, 1000, 5000 }, ShellLogic.FetchCounts);
            Assert.Equal(ShellLogic.DefaultFetchCount, ShellLogic.FetchCounts[0]);
        }

        // ---------- MergeProfiles ----------

        [Fact]
        public void MergeProfiles_LoadedOrderKept_RemovedLiveProfileAppendedAsOrphan()
        {
            var dev = Profile("dev", "개발");
            var prod = Profile("prod", "운영");
            var test = Profile("test", "검증");
            var renamedDev = Profile("dev", "개발 DB");
            var orphans = new HashSet<string>();

            var merged = ShellLogic.MergeProfiles(new[] { renamedDev, test }, new[] { dev, prod }, id => id == "prod" || id == "dev", orphans);

            Assert.Equal(new[] { "dev", "test", "prod" }, merged.Select(p => p.Id).ToArray());
            Assert.Same(renamedDev, merged[0]);
            Assert.Same(prod, merged[2]);
            Assert.Equal(new[] { "prod" }, orphans.ToArray());
        }

        [Fact]
        public void MergeProfiles_RemovedDisconnectedProfileDropped_OrphansRebuilt()
        {
            var orphans = new HashSet<string> { "old" };

            var merged = ShellLogic.MergeProfiles(new[] { Profile("dev") }, new[] { Profile("dev"), Profile("gone") }, id => false, orphans);

            Assert.Equal(new[] { "dev" }, merged.Select(p => p.Id).ToArray());
            Assert.Empty(orphans);
        }

        [Fact]
        public void MergeProfiles_OrphanBackInSavedList_NoLongerOrphan()
        {
            var orphans = new HashSet<string> { "prod" };

            var merged = ShellLogic.MergeProfiles(new[] { Profile("prod") }, new[] { Profile("prod") }, id => true, orphans);

            Assert.Single(merged);
            Assert.Empty(orphans);
        }

        [Fact]
        public void MergeProfiles_NullsBlankIdsAndDuplicatesSkipped()
        {
            var first = Profile("dev", "첫째");
            var loaded = new List<OracleConnectionProfile> { null, Profile("", "빈 Id"), first, Profile("dev", "둘째") };

            var merged = ShellLogic.MergeProfiles(loaded, null, null, null);

            Assert.Single(merged);
            Assert.Same(first, merged[0]);
            Assert.Empty(ShellLogic.MergeProfiles(null, null, null, null));
        }
    }
}
