using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>TreeLoaderLogic: 객체 페이지 자르기, 묶음별 개수 합치기, 검색 결과 자르기, 바로 실행할 불러오기 고르기.</summary>
    public class TreeLoaderLogicTests
    {
        private static List<DbObjectInfo> Objects(int count)
        {
            return Enumerable.Range(1, count).Select(i => new DbObjectInfo { Owner = "SCOTT", Name = "T" + i, Type = "TABLE" }).ToList();
        }

        private static List<SearchHit> Hits(int count)
        {
            return Enumerable.Range(1, count).Select(i => new SearchHit { Owner = "SCOTT", ObjectType = "TABLE", ObjectName = "T" + i }).ToList();
        }

        private static List<SchemaInfo> Schemas(int count)
        {
            return Enumerable.Range(1, count).Select(i => new SchemaInfo { Name = "S" + i }).ToList();
        }

        // ---------- Page ----------

        [Fact]
        public void Page_LimitPlusOneRows_TrimmedToLimitWithHasMore()
        {
            var page = TreeLoaderLogic.Page(Objects(1001), 1000);

            Assert.True(page.HasMore);
            Assert.Equal(1000, page.Items.Count);
            Assert.Equal("T1000", page.Items.Last().Name);
            Assert.Equal(1000, page.Limit);
        }

        [Theory]
        [InlineData(0, 1000)]
        [InlineData(999, 1000)]
        [InlineData(1000, 1000)]
        public void Page_AtMostLimitRows_AllKeptNoMore(int count, int limit)
        {
            var page = TreeLoaderLogic.Page(Objects(count), limit);

            Assert.False(page.HasMore);
            Assert.Equal(count, page.Items.Count);
            Assert.Equal(limit, page.Limit);
        }

        [Fact]
        public void Page_ZeroLimit_NoLimitAllKept()
        {
            var page = TreeLoaderLogic.Page(Objects(30000), 0);

            Assert.False(page.HasMore);
            Assert.Equal(30000, page.Items.Count);
            Assert.Equal(0, page.Limit);
        }

        [Fact]
        public void Page_Null_EmptyPage()
        {
            var page = TreeLoaderLogic.Page(null, 1000);

            Assert.Empty(page.Items);
            Assert.False(page.HasMore);
        }

        [Fact]
        public void Page_MoreAfterLoadMore_UsesNextLimitFromBuilder()
        {
            // "더 보기"는 TreeRowsBuilder.NextLimit의 limit으로 다시 부른다: 1000 → 5000 → 25000 → 0(전부)
            var page = TreeLoaderLogic.Page(Objects(5001), TreeRowsBuilder.NextLimit(1000));

            Assert.Equal(5000, page.Items.Count);
            Assert.True(page.HasMore);
            Assert.Equal(5000, page.Limit);
        }

        // ---------- GroupCounts ----------

        [Fact]
        public void GroupCounts_CodeTypesMergedAndAllGroupsPresent()
        {
            var counts = TreeLoaderLogic.GroupCounts(new[]
            {
                new KeyValuePair<string, long>("TABLE", 14),
                new KeyValuePair<string, long>("PROCEDURE", 2),
                new KeyValuePair<string, long>("FUNCTION", 3),
                new KeyValuePair<string, long>("PACKAGE", 1)
            });

            Assert.Equal(4, counts.Count);
            Assert.Equal(14, counts[TreeGroups.Table]);
            Assert.Equal(0, counts[TreeGroups.View]);
            Assert.Equal(0, counts[TreeGroups.Sequence]);
            Assert.Equal(6, counts[TreeGroups.Code]);
        }

        [Fact]
        public void GroupCounts_UnknownTypesNullAndNonPositiveIgnored()
        {
            var counts = TreeLoaderLogic.GroupCounts(new[]
            {
                new KeyValuePair<string, long>("PACKAGE BODY", 5),
                new KeyValuePair<string, long>("INDEX", 9),
                new KeyValuePair<string, long>(null, 3),
                new KeyValuePair<string, long>("VIEW", 0),
                new KeyValuePair<string, long>("SEQUENCE", -2),
                new KeyValuePair<string, long>("VIEW", 4)
            });

            Assert.Equal(new[] { 0, 4, 0, 0 }, TreeGroups.All.Select(g => counts[g]).ToArray());
        }

        [Fact]
        public void GroupCounts_EmptyOrNull_AllZero()
        {
            Assert.All(TreeLoaderLogic.GroupCounts(null).Values, v => Assert.Equal(0, v));
            Assert.Equal(TreeGroups.All, TreeLoaderLogic.GroupCounts(new KeyValuePair<string, long>[0]).Keys.ToArray());
        }

        [Fact]
        public void GroupCounts_HugeCounts_ClampedToIntMax()
        {
            var counts = TreeLoaderLogic.GroupCounts(new[]
            {
                new KeyValuePair<string, long>("FUNCTION", int.MaxValue),
                new KeyValuePair<string, long>("PACKAGE", 10)
            });

            Assert.Equal(int.MaxValue, counts[TreeGroups.Code]);
        }

        [Fact]
        public void GroupCounts_EmptySchema_BuilderShowsNoAccessNote()
        {
            var db = new DbTreeData { DbId = "d1", Name = "개발", Connected = true, MySchema = "SCOTT", Schemas = new List<SchemaInfo> { new SchemaInfo { Name = "APP_AUDIT" } } };
            var schemaKey = TreeKeys.Schema("d1", "APP_AUDIT");
            db.GroupCounts[schemaKey] = TreeLoaderLogic.GroupCounts(new KeyValuePair<string, long>[0]);
            var state = new TreeState();
            state.Expanded[TreeKeys.Db("d1")] = true;
            state.Expanded[schemaKey] = true;

            var rows = TreeRowsBuilder.Build(new[] { db }, state, false);

            Assert.Equal("볼 수 있는 객체가 없습니다 (권한 필요)", rows.Last().Text);
        }

        [Fact]
        public void ReadGroupCount_DecimalCountAndNulls()
        {
            var table = new DataTable();
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("CNT", typeof(decimal));
            table.Rows.Add("TABLE", 12m);
            table.Rows.Add(DBNull.Value, DBNull.Value);

            var rows = new List<KeyValuePair<string, long>>();
            using (var reader = table.CreateDataReader())
            {
                while (reader.Read())
                    rows.Add(TreeLoaderLogic.ReadGroupCount(reader));
            }

            Assert.Equal(new KeyValuePair<string, long>("TABLE", 12), rows[0]);
            Assert.Equal(new KeyValuePair<string, long>(null, 0), rows[1]);
        }

        // ---------- SearchResult ----------

        [Fact]
        public void SearchResult_OverLimit_TrimmedAndTruncated()
        {
            var result = TreeLoaderLogic.SearchResult(Schemas(3), Hits(501), Hits(10), 500, null);

            Assert.Equal(3, result.Schemas.Count);
            Assert.Equal(500, result.Objects.Count);
            Assert.Equal(10, result.Columns.Count);
            Assert.True(result.Truncated);
            Assert.Null(result.Error);
        }

        [Fact]
        public void SearchResult_ExactlyLimit_NotTruncated()
        {
            var result = TreeLoaderLogic.SearchResult(Schemas(500), Hits(500), Hits(500), 500, null);

            Assert.False(result.Truncated);
            Assert.Equal(500, result.Schemas.Count);
        }

        [Fact]
        public void SearchResult_EachListTrimmedSeparately()
        {
            var result = TreeLoaderLogic.SearchResult(Schemas(501), Hits(2), null, 500, null);

            Assert.True(result.Truncated);
            Assert.Equal(500, result.Schemas.Count);
            Assert.Equal(2, result.Objects.Count);
            Assert.Empty(result.Columns);
        }

        [Fact]
        public void SearchResult_ErrorKeptWithPartialResults_BlankErrorIsNull()
        {
            var failed = TreeLoaderLogic.SearchResult(Schemas(1), null, null, 500, "ORA-01031: insufficient privileges");
            var blank = TreeLoaderLogic.SearchResult(null, null, null, 500, "  ");

            Assert.Equal("ORA-01031: insufficient privileges", failed.Error);
            Assert.Single(failed.Schemas);
            Assert.Null(blank.Error);
            Assert.False(blank.Truncated);
        }

        [Fact]
        public void SearchResult_TruncatedResult_BuilderAddsTruncatedNote()
        {
            var db = new DbTreeData { DbId = "d1", Name = "개발", Connected = true };
            var result = TreeLoaderLogic.SearchResult(null, Hits(OracleMetadata.SearchLimit + 1), null, OracleMetadata.SearchLimit, null);
            var results = new Dictionary<string, TreeSearchResult> { { "d1", result } };

            var rows = TreeRowsBuilder.BuildSearch(new[] { db }, results, "T", new TreeState(), false);

            Assert.Equal("결과가 많아 일부만 표시합니다 (DB마다 최대 500개)", rows.Last().Text);
            Assert.Equal(OracleMetadata.SearchLimit, rows.Count(r => r.IsHit && r.Kind == TreeRowKind.Object));
        }

        // ---------- PendingLoads ----------

        [Fact]
        public void PendingLoads_SkipsLoadMoreLoadingUnknownDbAndDuplicates()
        {
            var db = new DbTreeData { DbId = "d1" };
            db.Loading.Add("busy");
            var data = new Dictionary<string, DbTreeData> { { "d1", db } };
            var rows = new List<TreeRow>
            {
                new TreeRow { Key = "n1", Kind = TreeRowKind.Note, Load = new TreeLoadRequest { Key = "k1", DbId = "d1", Kind = TreeLoadKind.Schemas } },
                new TreeRow { Key = "n2", Kind = TreeRowKind.Note, IsLoadMore = true, Load = new TreeLoadRequest { Key = "k2", DbId = "d1", Kind = TreeLoadKind.Objects } },
                new TreeRow { Key = "n3", Kind = TreeRowKind.Note, Load = new TreeLoadRequest { Key = "busy", DbId = "d1" } },
                new TreeRow { Key = "n4", Kind = TreeRowKind.Note, Load = new TreeLoadRequest { Key = "k4", DbId = "gone" } },
                new TreeRow { Key = "n5", Kind = TreeRowKind.Note, Load = new TreeLoadRequest { Key = "k1", DbId = "d1" } },
                new TreeRow { Key = "t", Kind = TreeRowKind.Object },
                null
            };

            var loads = TreeLoaderLogic.PendingLoads(rows, id => data.TryGetValue(id, out var found) ? found : null);

            Assert.Equal(new[] { "k1" }, loads.Select(l => l.Key).ToArray());
        }

        [Fact]
        public void PendingLoads_FromBuilderRows_SchemasLoadOfExpandedDb()
        {
            var db = new DbTreeData { DbId = "d1", Name = "개발", Connected = true };
            var state = new TreeState();
            state.Expanded[TreeKeys.Db("d1")] = true;
            var rows = TreeRowsBuilder.Build(new[] { db }, state, false);

            var loads = TreeLoaderLogic.PendingLoads(rows, id => id == "d1" ? db : null);

            Assert.Single(loads);
            Assert.Equal(TreeLoadKind.Schemas, loads[0].Kind);
            Assert.Equal(TreeKeys.Db("d1"), loads[0].Key);

            db.Loading.Add(loads[0].Key);
            Assert.Empty(TreeLoaderLogic.PendingLoads(rows, id => id == "d1" ? db : null));
        }
    }
}
