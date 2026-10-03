using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>TreePanelLogic: 행 비교·목록 맞추기, 검색 막대·바닥줄 문장, 일치 이동, 펼침 상태(검색 지우기·끊기).</summary>
    public class TreePanelLogicTests
    {
        private static TreeRow Row(string key, string text = "EMP", TreeRowKind kind = TreeRowKind.Object)
        {
            return new TreeRow { Key = key, Text = text, Kind = kind, Depth = 3 };
        }

        private static DbTreeData Db(string id = "d1", bool connected = true)
        {
            return new DbTreeData { DbId = id, Name = "개발", Detail = "scott@dev-db:1521/ORCLPDB1", Connected = connected, MySchema = "SCOTT" };
        }

        private static SearchHit ObjectHit(string owner, string type, string name)
        {
            return new SearchHit { Owner = owner, ObjectType = type, ObjectName = name };
        }

        // ---------- Signature ----------

        [Fact]
        public void Signature_SameContent_Equal()
        {
            var a = Row("k");
            var b = Row("k");

            Assert.Equal(TreePanelLogic.Signature(a, new RowBadges()), TreePanelLogic.Signature(b, new RowBadges()));
        }

        [Fact]
        public void Signature_ChangesWithRowStateHighlightsAndBadges()
        {
            var baseline = TreePanelLogic.Signature(Row("k"), new RowBadges());

            var expanded = Row("k");
            expanded.Expandable = true;
            expanded.Expanded = true;
            var highlighted = Row("k");
            highlighted.Highlights.Add(new TextSpan(0, 2));
            var counted = Row("k");
            counted.Count = 3;

            Assert.NotEqual(baseline, TreePanelLogic.Signature(expanded, new RowBadges()));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(highlighted, new RowBadges()));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(counted, new RowBadges()));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(Row("k"), new RowBadges { Pending = "커밋 대기 3행" }));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(Row("k"), new RowBadges { Running = true }));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(Row("k"), new RowBadges { ProfileChanged = true }));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(Row("k"), new RowBadges { Current = true }));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(Row("k"), new RowBadges { Color = "red" }));
            Assert.NotEqual(baseline, TreePanelLogic.Signature(Row("k", "DEPT"), new RowBadges()));
        }

        [Fact]
        public void Signature_NullRow_Empty()
        {
            Assert.Equal("", TreePanelLogic.Signature(null, new RowBadges()));
        }

        // ---------- Align ----------

        [Theory]
        // 같은 목록: 모두 앞부분
        [InlineData("a b c", "a b c", 3, 0)]
        // 가운데에 하위 행이 들어감(펼치기)
        [InlineData("a b e", "a b c d e", 2, 1)]
        // 가운데 하위 행이 빠짐(접기)
        [InlineData("a b c d e", "a b e", 2, 1)]
        // 모두 다름
        [InlineData("a b", "c d", 0, 0)]
        // 끝에 붙음
        [InlineData("a b", "a b c", 2, 0)]
        // 앞에 붙음
        [InlineData("b c", "a b c", 0, 2)]
        // 반복되는 키: 앞부분이 먼저, 뒷부분은 남은 것에서만
        [InlineData("a a", "a a a", 2, 0)]
        [InlineData("", "a", 0, 0)]
        [InlineData("a", "", 0, 0)]
        public void Align_PrefixAndSuffixDoNotOverlap(string oldKeys, string newKeys, int prefix, int suffix)
        {
            int p, s;
            TreePanelLogic.Align(Split(oldKeys), Split(newKeys), out p, out s);

            Assert.Equal(prefix, p);
            Assert.Equal(suffix, s);
        }

        [Fact]
        public void Align_Null_Zero()
        {
            int p, s;
            TreePanelLogic.Align(null, null, out p, out s);

            Assert.Equal(0, p);
            Assert.Equal(0, s);
        }

        [Fact]
        public void Align_ExpandWithBuilder_OnlyChildrenAreNew()
        {
            var db = Db();
            db.Schemas = new List<SchemaInfo> { new SchemaInfo { Name = "SCOTT" }, new SchemaInfo { Name = "HR" } };
            var state = new TreeState();
            state.Expanded[TreeKeys.Db("d1")] = true;
            var before = TreeRowsBuilder.Build(new[] { db }, state, false).Select(r => r.Key).ToList();
            state.Expanded[TreeKeys.Schema("d1", "SCOTT")] = true;
            var after = TreeRowsBuilder.Build(new[] { db }, state, false).Select(r => r.Key).ToList();

            int p, s;
            TreePanelLogic.Align(before, after, out p, out s);

            // DB, SCOTT은 그대로(앞), HR도 그대로(뒤), SCOTT 아래 "불러오는 중…" 한 줄만 새로 들어간다
            Assert.Equal(2, p);
            Assert.Equal(1, s);
            Assert.Equal(1, after.Count - p - s);
        }

        private static string[] Split(string keys)
        {
            return keys.Length == 0 ? new string[0] : keys.Split(' ');
        }

        // ---------- GroupMeta ----------

        [Theory]
        [InlineData(null, 12, "12")]
        [InlineData(3, 12, "3 / 12")]
        [InlineData(12, 12, "12")]
        [InlineData(3, null, "3")]
        [InlineData(null, null, "")]
        [InlineData(null, 1234, "1,234")]
        public void GroupMeta_MatchOverTotal(int? match, int? count, string expected)
        {
            var row = new TreeRow { Key = "g", Kind = TreeRowKind.Group, MatchCount = match, Count = count };

            Assert.Equal(expected, TreePanelLogic.GroupMeta(row));
        }

        [Fact]
        public void GroupMeta_NotAGroup_Empty()
        {
            Assert.Equal("", TreePanelLogic.GroupMeta(new TreeRow { Kind = TreeRowKind.Object, Count = 3 }));
            Assert.Equal("", TreePanelLogic.GroupMeta(null));
        }

        // ---------- 검색 막대 ----------

        private static List<TreeRow> SearchRows(bool columns)
        {
            var db = Db();
            var result = new TreeSearchResult();
            result.Schemas.Add(new SchemaInfo { Name = "EMP_ADMIN" });
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            result.Objects.Add(ObjectHit("SCOTT", "VIEW", "V_EMP"));
            if (columns)
                result.Columns.Add(new SearchHit { Owner = "SCOTT", ObjectType = "TABLE", ObjectName = "DEPT", ColumnName = "EMP_COUNT", ColumnType = "NUMBER" });
            var results = new Dictionary<string, TreeSearchResult> { { "d1", result } };
            return TreeRowsBuilder.BuildSearch(new[] { db }, results, "emp", new TreeState(), false);
        }

        [Fact]
        public void SearchSummary_SchemasAndObjects_NoDbPartWithoutDbHit()
        {
            var rows = SearchRows(false);

            Assert.Equal("일치 3개 (스키마 1 · 객체 2)", TreePanelLogic.SearchSummary(rows, false));
        }

        [Fact]
        public void SearchSummary_ColumnsScope_ColumnPart()
        {
            var rows = SearchRows(true);

            Assert.Equal("일치 4개 (스키마 1 · 객체 2 · 열 1)", TreePanelLogic.SearchSummary(rows, true));
        }

        [Fact]
        public void SearchSummary_DbNameHit_DbPartFirst()
        {
            var rows = new List<TreeRow>
            {
                new TreeRow { Key = "db", Kind = TreeRowKind.Database, IsHit = true },
                new TreeRow { Key = "o", Kind = TreeRowKind.Object, IsHit = true },
                new TreeRow { Key = "g", Kind = TreeRowKind.Group, IsDim = true }
            };

            Assert.Equal("일치 2개 (DB 1 · 스키마 0 · 객체 1)", TreePanelLogic.SearchSummary(rows, false));
        }

        [Fact]
        public void SearchSummary_NoHits_ZeroWithoutBreakdown()
        {
            Assert.Equal("일치 0개", TreePanelLogic.SearchSummary(new List<TreeRow>(), true));
            Assert.Equal("일치 0개", TreePanelLogic.SearchSummary(null, false));
        }

        [Fact]
        public void HitKeys_VisibleHitsInRowOrder()
        {
            var rows = SearchRows(false);

            var hits = TreePanelLogic.HitKeys(rows);

            // 내 스키마(SCOTT) 아래 객체 일치가 먼저, 이름이 일치한 다른 스키마가 그 뒤
            Assert.Equal(new[]
            {
                TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP"),
                TreeKeys.Object("d1", "SCOTT", "VIEW", "V_EMP"),
                TreeKeys.Schema("d1", "EMP_ADMIN")
            }, hits.ToArray());
        }

        [Theory]
        [InlineData(-1, 1, 3, 0)]
        [InlineData(-1, -1, 3, 2)]
        [InlineData(0, 1, 3, 1)]
        [InlineData(2, 1, 3, 0)]
        [InlineData(0, -1, 3, 2)]
        [InlineData(1, -1, 3, 0)]
        [InlineData(5, 1, 3, 0)]
        [InlineData(0, 1, 1, 0)]
        [InlineData(-1, 1, 0, -1)]
        public void Step_WrapsAroundAndStartsFromEnds(int index, int step, int count, int expected)
        {
            Assert.Equal(expected, TreePanelLogic.Step(index, step, count));
        }

        [Fact]
        public void Position_OneBasedOrEmpty()
        {
            Assert.Equal("2/5", TreePanelLogic.Position(1, 5));
            Assert.Equal("", TreePanelLogic.Position(-1, 5));
            Assert.Equal("", TreePanelLogic.Position(5, 5));
        }

        // ---------- 바닥줄·안내 ----------

        [Fact]
        public void NormalFooter_CountsAndHiddenSystem()
        {
            Assert.Equal("DB 2개 · 연결 1개", TreePanelLogic.NormalFooter(2, 1, false));
            Assert.Equal("DB 2개 · 연결 2개 · 내장 스키마 숨김", TreePanelLogic.NormalFooter(2, 2, true));
        }

        [Fact]
        public void SearchFooter_WaitingOrResult()
        {
            Assert.Equal("검색 중…", TreePanelLogic.SearchFooter(2, 1));
            Assert.Equal("검색 결과 · 연결된 DB 2개 · 종류마다 최대 500개", TreePanelLogic.SearchFooter(2, 0));
        }

        [Fact]
        public void NoMatchText_HintsForColumnsAndSystem()
        {
            Assert.Equal("'emp'와(과) 일치하는 항목이 없습니다 (검색 대상: 스키마·객체).\n열 이름이면 검색 대상을 '스키마·객체·열'로 바꾸세요.\n내장 스키마는 [내장]을 체크해야 검색됩니다.",
                TreePanelLogic.NoMatchText(" emp ", false, false));
            Assert.Equal("'emp'와(과) 일치하는 항목이 없습니다 (검색 대상: 스키마·객체·열).",
                TreePanelLogic.NoMatchText("emp", true, true));
        }

        [Fact]
        public void OfflineNote_OnlyWhenSomeDisconnected()
        {
            Assert.Equal("연결 안 된 DB 2개는 검색하지 않았습니다", TreePanelLogic.OfflineNote(2));
            Assert.Null(TreePanelLogic.OfflineNote(0));
            Assert.Null(TreeKeys.DbIdOf(TreePanelLogic.OfflineNoteKey));
        }

        [Fact]
        public void HidesSystemSchemas_OnlyLoadedConnectedDbsWithOtherMaintainedSchemas()
        {
            var plain = Db("d1");
            plain.Schemas = new List<SchemaInfo> { new SchemaInfo { Name = "SCOTT" } };
            var withSys = Db("d2");
            withSys.Schemas = new List<SchemaInfo> { new SchemaInfo { Name = "SCOTT" }, new SchemaInfo { Name = "SYS", OracleMaintained = true } };
            var mySystem = Db("d3");
            mySystem.MySchema = "SYSTEM";
            mySystem.Schemas = new List<SchemaInfo> { new SchemaInfo { Name = "SYSTEM", OracleMaintained = true } };
            var offline = Db("d4", connected: false);
            offline.Schemas = withSys.Schemas;

            Assert.False(TreePanelLogic.HidesSystemSchemas(new[] { plain, mySystem, offline, Db("d5") }, false));
            Assert.True(TreePanelLogic.HidesSystemSchemas(new[] { plain, withSys }, false));
            Assert.False(TreePanelLogic.HidesSystemSchemas(new[] { withSys }, true));
        }

        // ---------- 펼침 상태 ----------

        [Fact]
        public void ExpandAncestors_ColumnOfView_ExpandsPathThroughViewGroup()
        {
            var state = new TreeState();

            TreePanelLogic.ExpandAncestors(state, TreeKeys.Column("d1", "SCOTT", "V_EMP", "ENAME"), "VIEW");

            Assert.Equal(new[]
            {
                TreeKeys.Db("d1"),
                TreeKeys.Schema("d1", "SCOTT"),
                TreeKeys.Group("d1", "SCOTT", TreeGroups.View),
                TreeKeys.Object("d1", "SCOTT", "VIEW", "V_EMP")
            }, state.Expanded.Where(p => p.Value).Select(p => p.Key).ToArray());
        }

        [Fact]
        public void ExpandAncestors_OverridesCollapsedAndLeavesNodeItself()
        {
            var state = new TreeState();
            var table = TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP");
            state.Expanded[TreeKeys.Schema("d1", "SCOTT")] = false;

            TreePanelLogic.ExpandAncestors(state, table, "PACKAGE");
            TreePanelLogic.ExpandAncestors(state, null, null);

            Assert.True(state.Expanded[TreeKeys.Schema("d1", "SCOTT")]);
            Assert.True(state.Expanded[TreeKeys.Group("d1", "SCOTT", TreeGroups.Table)]);
            Assert.False(state.Expanded.ContainsKey(table));
        }

        [Fact]
        public void ExpandAncestors_SearchSelectionVisibleInNormalTreeAfterClear()
        {
            var db = Db();
            db.Schemas = new List<SchemaInfo> { new SchemaInfo { Name = "SCOTT" } };
            db.GroupCounts[TreeKeys.Schema("d1", "SCOTT")] = TreeLoaderLogic.GroupCounts(new[] { new KeyValuePair<string, long>("TABLE", 1) });
            db.Objects[TreeKeys.Group("d1", "SCOTT", TreeGroups.Table)] = TreeLoaderLogic.Page(new[] { new DbObjectInfo { Owner = "SCOTT", Name = "EMP", Type = "TABLE" } }, 1000);
            var selected = TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP");
            var state = new TreeState();

            TreePanelLogic.ExpandAncestors(state, selected, "TABLE");
            var rows = TreeRowsBuilder.Build(new[] { db }, state, false);

            Assert.Contains(rows, r => r.Key == selected);
        }

        [Fact]
        public void ForgetDb_RemovesOnlyThatDbsKeys()
        {
            var state = new TreeState();
            state.Expanded[TreeKeys.Db("d1")] = true;
            state.Expanded[TreeKeys.Schema("d1", "SCOTT")] = true;
            state.Expanded[TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP")] = false;
            state.Expanded[TreeKeys.Db("d2")] = true;
            state.Expanded[TreeKeys.Schema("d2", "HR")] = true;

            TreePanelLogic.ForgetDb(state, "d1");

            Assert.Equal(new[] { TreeKeys.Db("d2"), TreeKeys.Schema("d2", "HR") }, state.Expanded.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void ParentKey_ColumnUsesItsObjectTypeAndDbHasNone()
        {
            var column = new TreeRow { Key = TreeKeys.Column("d1", "SCOTT", "V_EMP", "ENAME"), Kind = TreeRowKind.Column, ObjectType = "VIEW" };
            var group = new TreeRow { Key = TreeKeys.Group("d1", "SCOTT", TreeGroups.Code), Kind = TreeRowKind.Group, ObjectType = TreeGroups.Code };
            var note = new TreeRow { Key = TreeKeys.Schema("d1", "SCOTT") + TreeKeys.Sep + "note", Kind = TreeRowKind.Note };

            Assert.Equal(TreeKeys.Object("d1", "SCOTT", "VIEW", "V_EMP"), TreePanelLogic.ParentKey(column));
            Assert.Equal(TreeKeys.Schema("d1", "SCOTT"), TreePanelLogic.ParentKey(group));
            Assert.Equal(TreeKeys.Schema("d1", "SCOTT"), TreePanelLogic.ParentKey(note));
            Assert.Null(TreePanelLogic.ParentKey(new TreeRow { Key = TreeKeys.Db("d1"), Kind = TreeRowKind.Database }));
            Assert.Null(TreePanelLogic.ParentKey(null));
        }

        // ---------- 오른쪽 메뉴 ----------

        [Fact]
        public void ConnectionMenu_NotConnected_OnlyConnect()
        {
            var state = TreePanelLogic.ConnectionMenu(false, false);

            Assert.True(state.CanConnect);
            Assert.False(state.CanReconnect);
            Assert.False(state.CanDisconnect);
            Assert.False(state.Connecting);
        }

        [Fact]
        public void ConnectionMenu_Connected_ReconnectAndDisconnect()
        {
            // 끊긴 세션도 세션이 있으므로 같다(다시 연결은 끊긴 세션을 버리고 연결)
            var state = TreePanelLogic.ConnectionMenu(true, false);

            Assert.False(state.CanConnect);
            Assert.True(state.CanReconnect);
            Assert.True(state.CanDisconnect);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConnectionMenu_Connecting_BlocksAll(bool hasSession)
        {
            var state = TreePanelLogic.ConnectionMenu(hasSession, true);

            Assert.True(state.Connecting);
            Assert.False(state.CanConnect || state.CanReconnect || state.CanDisconnect);
        }
    }
}
