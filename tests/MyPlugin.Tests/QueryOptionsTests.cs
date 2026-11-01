using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    // 전역 QueryOptions.QuickRows를 바꾸는 시험과 그 값을 읽는 시험은 같은 묶음 — 병렬로 돌면 "앞 250행"을 읽는다
    [Collection("QueryOptions")]
    public class QueryOptionsTests
    {
        [Fact]
        public void DefaultValuesWithinRange()
        {
            Assert.True(QueryOptions.DefaultRows >= 1);
            Assert.True(QueryOptions.DefaultRows <= QueryOptions.MaxRows);

            Assert.True(QueryOptions.DefaultFetchBytes >= QueryOptions.MinFetchBytes);
            Assert.True(QueryOptions.DefaultFetchBytes <= QueryOptions.MaxFetchBytes);

            Assert.Equal(QueryOptions.DefaultRows, QueryOptions.QuickRows);
            Assert.Equal(QueryOptions.DefaultFetchBytes, QueryOptions.FetchBytes);
        }

        [Fact]
        public void WorkspaceLogicDelegatesToQueryOptions()
        {
            var oldRows = QueryOptions.QuickRows;
            try
            {
                QueryOptions.QuickRows = 250;
                Assert.Equal(250, WorkspaceLogic.QuickQueryRows);
                var message = WorkspaceLogic.QuickQueryMessage("HR.EMP", "SQL 1", "DEPT = 10");
                Assert.Contains("앞 250행", message);
            }
            finally
            {
                QueryOptions.QuickRows = oldRows;
            }
        }
    }
}
