using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>TreeKeys·TreeGroups·TreeRowsBuilder: 캐시·펼침 상태 → 평면 트리 행(일반 모드·검색 모드).</summary>
    public class TreeModelTests
    {
        private const char S = TreeKeys.Sep;

        private static readonly string DbKey = TreeKeys.Db("d1");
        private static readonly string ScottKey = TreeKeys.Schema("d1", "SCOTT");
        private static readonly string ScottTables = TreeKeys.Group("d1", "SCOTT", TreeGroups.Table);
        private static readonly string EmpKey = TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP");

        // ---------- 준비 ----------

        private static DbTreeData Db(string id = "d1", string name = "개발", bool connected = true, string mySchema = "SCOTT")
        {
            return new DbTreeData { DbId = id, Name = name, Detail = "scott@dev-db:1521/ORCLPDB1", Connected = connected, MySchema = mySchema };
        }

        private static SchemaInfo Schema(string name, bool maintained = false)
        {
            return new SchemaInfo { Name = name, OracleMaintained = maintained };
        }

        private static Dictionary<string, int> Counts(int tables, int views, int sequences, int code)
        {
            return new Dictionary<string, int>
            {
                { TreeGroups.Table, tables }, { TreeGroups.View, views }, { TreeGroups.Sequence, sequences }, { TreeGroups.Code, code }
            };
        }

        private static DbObjectInfo Obj(string type, string name, long? numRows = null, string status = "VALID")
        {
            return new DbObjectInfo { Owner = "SCOTT", Type = type, Name = name, NumRows = numRows, Status = status };
        }

        private static ObjectPage Page(params DbObjectInfo[] items)
        {
            return new ObjectPage { Items = items.ToList(), Limit = TreeRowsBuilder.ObjectPageSize };
        }

        private static ColumnInfo Col(string name, int position, string type, bool nullable = true, bool pk = false)
        {
            return new ColumnInfo { Owner = "SCOTT", ObjectName = "EMP", Name = name, Position = position, TypeLabel = type, Nullable = nullable, PrimaryKey = pk };
        }

        private static TreeState State(params string[] expandedKeys)
        {
            var state = new TreeState();
            foreach (var key in expandedKeys)
                state.Expanded[key] = true;
            return state;
        }

        private static List<TreeRow> Build(DbTreeData db, TreeState state, bool showSystem = false)
        {
            return TreeRowsBuilder.Build(new[] { db }, state, showSystem);
        }

        private static SearchHit ObjectHit(string owner, string type, string name)
        {
            return new SearchHit { Owner = owner, ObjectType = type, ObjectName = name };
        }

        private static SearchHit ColumnHit(string owner, string type, string objectName, string column, string columnType)
        {
            return new SearchHit { Owner = owner, ObjectType = type, ObjectName = objectName, ColumnName = column, ColumnType = columnType };
        }

        private static List<TreeRow> Search(DbTreeData db, TreeSearchResult result, string term, TreeState state = null, bool showSystem = false)
        {
            var results = new Dictionary<string, TreeSearchResult> { { db.DbId, result } };
            return TreeRowsBuilder.BuildSearch(new[] { db }, results, term, state ?? new TreeState(), showSystem);
        }

        private static string[] Keys(IEnumerable<TreeRow> rows)
        {
            return rows.Select(r => r.Key).ToArray();
        }

        private static (int, int)[] Spans(TreeRow row)
        {
            return row.Highlights.Select(h => (h.Start, h.Length)).ToArray();
        }

        /// <summary>DB 펼침 + 스키마 SCOTT·HR 캐시. 펼칠 키를 더 줄 수 있다.</summary>
        private static TreeState Open(params string[] more)
        {
            return State(new[] { DbKey, ScottKey }.Concat(more).ToArray());
        }

        private static DbTreeData DbWithScott()
        {
            var db = Db();
            db.Schemas = new List<SchemaInfo> { Schema("SCOTT"), Schema("HR") };
            return db;
        }

        // ---------- TreeKeys ----------

        [Fact]
        public void Keys_Format_UnitSeparatorBetweenParts()
        {
            Assert.Equal("db" + S + "d1", TreeKeys.Db("d1"));
            Assert.Equal("s" + S + "d1" + S + "SCOTT", TreeKeys.Schema("d1", "SCOTT"));
            Assert.Equal("g" + S + "d1" + S + "SCOTT" + S + "CODE", TreeKeys.Group("d1", "SCOTT", TreeGroups.Code));
            Assert.Equal("o" + S + "d1" + S + "SCOTT" + S + "VIEW" + S + "V1", TreeKeys.Object("d1", "SCOTT", "VIEW", "V1"));
            Assert.Equal("c" + S + "d1" + S + "SCOTT" + S + "EMP" + S + "ENAME", TreeKeys.Column("d1", "SCOTT", "EMP", "ENAME"));
        }

        [Fact]
        public void Keys_NamesWithColonsAndSpaces_DoNotCollideAndParseBack()
        {
            // 따옴표 식별자에는 ':'·공백이 올 수 있다. ':'를 구분자로 쓰면 아래 둘이 같은 키가 된다.
            Assert.NotEqual(TreeKeys.Schema("d1", "A:B"), TreeKeys.Schema("d1:A", "B"));
            Assert.NotEqual(TreeKeys.Column("d1", "S", "T U", "V"), TreeKeys.Column("d1", "S", "T", "U V"));

            var column = TreeKeys.Column("d:1", "MY:OWNER", "ORDER LINES", "QTY: 2");

            Assert.Equal("d:1", TreeKeys.DbIdOf(column));
            Assert.Equal(new[]
            {
                TreeKeys.Db("d:1"),
                TreeKeys.Schema("d:1", "MY:OWNER"),
                TreeKeys.Group("d:1", "MY:OWNER", TreeGroups.Table),
                TreeKeys.Object("d:1", "MY:OWNER", "TABLE", "ORDER LINES")
            }, TreeKeys.Ancestors(column));
            Assert.Equal(new[] { TreeKeys.Db("d:1"), TreeKeys.Schema("d:1", "MY:OWNER"), TreeKeys.Group("d:1", "MY:OWNER", TreeGroups.View) },
                TreeKeys.Ancestors(TreeKeys.Object("d:1", "MY:OWNER", "VIEW", "A B:C")));
        }

        [Fact]
        public void DbIdOf_EveryKindAndNoteKeys_ReturnsDbId()
        {
            var keys = new[]
            {
                TreeKeys.Db("d1"),
                TreeKeys.Schema("d1", "SCOTT"),
                TreeKeys.Group("d1", "SCOTT", TreeGroups.Code),
                TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP"),
                TreeKeys.Column("d1", "SCOTT", "EMP", "ENAME"),
                TreeKeys.Db("d1") + S + "note",
                TreeKeys.Group("d1", "SCOTT", TreeGroups.Table) + S + "more"
            };

            foreach (var key in keys)
                Assert.Equal("d1", TreeKeys.DbIdOf(key));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("d1")]
        [InlineData("db")]
        [InlineData("db:d1")]
        [InlineData("x\u001Fd1")]
        [InlineData("s\u001Fd1")]
        [InlineData("o\u001Fd1\u001FSCOTT\u001FTABLE")]
        [InlineData("db\u001Fd1\u001Fother")]
        [InlineData("db\u001Fd1\u001Fnote\u001Fnote")]
        public void DbIdOf_NotAKey_ReturnsNullAndNoAncestors(string key)
        {
            Assert.Null(TreeKeys.DbIdOf(key));
            Assert.Empty(TreeKeys.Ancestors(key));
        }

        [Fact]
        public void Ancestors_EveryKind_FromTop()
        {
            var db = TreeKeys.Db("d1");
            var scott = TreeKeys.Schema("d1", "SCOTT");

            Assert.Empty(TreeKeys.Ancestors(db));
            Assert.Equal(new[] { db }, TreeKeys.Ancestors(scott));
            Assert.Equal(new[] { db, scott }, TreeKeys.Ancestors(TreeKeys.Group("d1", "SCOTT", TreeGroups.Sequence)));
            Assert.Equal(new[] { db, scott, TreeKeys.Group("d1", "SCOTT", TreeGroups.Table) },
                TreeKeys.Ancestors(TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP")));
            Assert.Equal(new[] { db, scott, TreeKeys.Group("d1", "SCOTT", TreeGroups.Code) },
                TreeKeys.Ancestors(TreeKeys.Object("d1", "SCOTT", "PACKAGE", "PKG")));
            Assert.Equal(new[] { db, scott, TreeKeys.Group("d1", "SCOTT", TreeGroups.Table), TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP") },
                TreeKeys.Ancestors(TreeKeys.Column("d1", "SCOTT", "EMP", "EMPNO")));
        }

        [Fact]
        public void Ancestors_ColumnOfView_UsesGivenObjectType()
        {
            var column = TreeKeys.Column("d1", "SCOTT", "EMP_V", "ENAME");

            Assert.Equal(new[]
            {
                TreeKeys.Db("d1"),
                TreeKeys.Schema("d1", "SCOTT"),
                TreeKeys.Group("d1", "SCOTT", TreeGroups.View),
                TreeKeys.Object("d1", "SCOTT", "VIEW", "EMP_V")
            }, TreeKeys.Ancestors(column, "VIEW"));
            // 형식을 안 주면 테이블로 본다
            Assert.Equal(TreeKeys.Object("d1", "SCOTT", "TABLE", "EMP_V"), TreeKeys.Ancestors(column).Last());
            Assert.Equal(TreeKeys.Group("d1", "SCOTT", TreeGroups.Table), TreeKeys.Ancestors(column)[2]);
        }

        [Fact]
        public void Ancestors_NoteKey_ParentAncestorsThenParent()
        {
            var tables = TreeKeys.Group("d1", "SCOTT", TreeGroups.Table);

            Assert.Equal(new[] { TreeKeys.Db("d1"), TreeKeys.Schema("d1", "SCOTT"), tables }, TreeKeys.Ancestors(tables + S + "more"));
            Assert.Equal(new[] { TreeKeys.Db("d1") }, TreeKeys.Ancestors(TreeKeys.Db("d1") + S + "note"));
        }

        // ---------- TreeGroups ----------

        [Fact]
        public void TreeGroups_TitlesAndObjectTypes()
        {
            Assert.Equal(new[] { "TABLE", "VIEW", "SEQUENCE", "CODE" }, TreeGroups.All);
            Assert.Equal("테이블", TreeGroups.Title(TreeGroups.Table));
            Assert.Equal("뷰", TreeGroups.Title(TreeGroups.View));
            Assert.Equal("시퀀스", TreeGroups.Title(TreeGroups.Sequence));
            Assert.Equal("프로시저·함수·패키지", TreeGroups.Title(TreeGroups.Code));

            Assert.Equal(new[] { "TABLE" }, TreeGroups.ObjectTypes(TreeGroups.Table));
            Assert.Equal(new[] { "VIEW" }, TreeGroups.ObjectTypes(TreeGroups.View));
            Assert.Equal(new[] { "SEQUENCE" }, TreeGroups.ObjectTypes(TreeGroups.Sequence));
            Assert.Equal(new[] { "PROCEDURE", "FUNCTION", "PACKAGE" }, TreeGroups.ObjectTypes(TreeGroups.Code));
            Assert.Empty(TreeGroups.ObjectTypes("SYNONYM"));
            foreach (var group in TreeGroups.All)
                Assert.All(TreeGroups.ObjectTypes(group), type => Assert.Equal(group, TreeGroups.GroupOf(type)));
        }

        [Fact]
        public void TreeGroups_ObjectTypes_ReturnsNewArrayEachCall()
        {
            TreeGroups.ObjectTypes(TreeGroups.Code)[0] = "X";
            Assert.Equal("PROCEDURE", TreeGroups.ObjectTypes(TreeGroups.Code)[0]);
        }

        [Theory]
        [InlineData("TABLE", "TABLE", true)]
        [InlineData("VIEW", "VIEW", true)]
        [InlineData("SEQUENCE", "SEQUENCE", false)]
        [InlineData("PROCEDURE", "CODE", false)]
        [InlineData("FUNCTION", "CODE", false)]
        [InlineData("PACKAGE", "CODE", false)]
        [InlineData("PACKAGE BODY", null, false)]
        [InlineData("SYNONYM", null, false)]
        [InlineData("MATERIALIZED VIEW", null, false)]
        [InlineData("CODE", null, false)]
        [InlineData(null, null, false)]
        public void TreeGroups_GroupOfAndHasColumns(string objectType, string group, bool hasColumns)
        {
            Assert.Equal(group, TreeGroups.GroupOf(objectType));
            Assert.Equal(hasColumns, TreeGroups.HasColumns(objectType));
        }

        // ---------- NextLimit ----------

        [Fact]
        public void NextLimit_1000Then5000Then25000ThenAll_ZeroStaysZero()
        {
            var limit = TreeRowsBuilder.ObjectPageSize;
            Assert.Equal(1000, limit);
            limit = TreeRowsBuilder.NextLimit(limit);
            Assert.Equal(5000, limit);
            limit = TreeRowsBuilder.NextLimit(limit);
            Assert.Equal(25000, limit);
            limit = TreeRowsBuilder.NextLimit(limit);
            Assert.Equal(0, limit);
            Assert.Equal(0, TreeRowsBuilder.NextLimit(0));
        }

        [Theory]
        [InlineData(1, 1000)]
        [InlineData(999, 1000)]
        [InlineData(3000, 5000)]
        [InlineData(24999, 25000)]
        [InlineData(25001, 0)]
        [InlineData(-1, 0)]
        public void NextLimit_OffSequenceValues_NextLargerStepOrAll(int limit, int next)
        {
            Assert.Equal(next, TreeRowsBuilder.NextLimit(limit));
        }

        // ---------- Build: DB ----------

        [Fact]
        public void Build_DbRow_TextDetailAndCollapsedByDefault()
        {
            var db = DbWithScott();

            var row = Assert.Single(Build(db, new TreeState()));

            Assert.Equal(DbKey, row.Key);
            Assert.Equal(TreeRowKind.Database, row.Kind);
            Assert.Equal(0, row.Depth);
            Assert.Equal("개발", row.Text);
            Assert.Equal("scott@dev-db:1521/ORCLPDB1", row.Detail);
            Assert.Equal("d1", row.DbId);
            Assert.True(row.Expandable);
            Assert.False(row.Expanded);
            Assert.False(row.IsHit);
            Assert.False(row.IsDim);
        }

        [Fact]
        public void Build_DisconnectedDbExpanded_ExpandableButNoChildren()
        {
            var db = DbWithScott();
            db.Connected = false;
            db.Errors[DbKey] = "ORA-12541: 리스너가 없습니다";

            var row = Assert.Single(Build(db, State(DbKey)));

            Assert.True(row.Expandable);
            Assert.True(row.Expanded);
        }

        [Fact]
        public void Build_ConnectedExpandedWithoutSchemas_LoadingNoteWithSchemasLoad()
        {
            var db = Db();

            var rows = Build(db, State(DbKey));

            Assert.Equal(2, rows.Count);
            var note = rows[1];
            Assert.Equal(DbKey + S + "note", note.Key);
            Assert.Equal(TreeRowKind.Note, note.Kind);
            Assert.Equal(1, note.Depth);
            Assert.Equal("불러오는 중…", note.Text);
            Assert.False(note.Expandable);
            Assert.False(note.IsLoadMore);
            Assert.False(note.IsError);
            Assert.Equal(TreeLoadKind.Schemas, note.Load.Kind);
            Assert.Equal(DbKey, note.Load.Key);
            Assert.Equal("d1", note.Load.DbId);
        }

        [Fact]
        public void Build_MultipleDbs_InputOrderAndOwnExpansionState()
        {
            var a = DbWithScott();
            var b = Db("d2", "운영");
            b.Schemas = new List<SchemaInfo> { Schema("SCOTT") };

            var rows = TreeRowsBuilder.Build(new[] { b, a }, State(TreeKeys.Db("d2")), false);

            Assert.Equal(new[] { TreeKeys.Db("d2"), TreeKeys.Schema("d2", "SCOTT"), DbKey }, Keys(rows));
            Assert.All(rows, r => Assert.Equal(r.Key == DbKey ? "d1" : "d2", r.DbId));
        }

        [Fact]
        public void Build_NullState_AllCollapsed()
        {
            Assert.Single(TreeRowsBuilder.Build(new[] { DbWithScott() }, null, false));
        }

        // ---------- Build: 스키마 ----------

        [Fact]
        public void Build_Schemas_MySchemaFirstThenNameAndSystemHidden()
        {
            var db = Db();
            db.Schemas = new List<SchemaInfo> { Schema("SYS", true), Schema("HR"), Schema("SCOTT"), Schema("APP"), Schema("SYSTEM", true) };

            var hidden = Build(db, State(DbKey), showSystem: false);
            var shown = Build(db, State(DbKey), showSystem: true);

            Assert.Equal(new[] { "개발", "SCOTT", "APP", "HR" }, hidden.Select(r => r.Text));
            Assert.Equal(new[] { "개발", "SCOTT", "APP", "HR", "SYS", "SYSTEM" }, shown.Select(r => r.Text));
            var scott = shown[1];
            Assert.Equal(ScottKey, scott.Key);
            Assert.Equal(TreeRowKind.Schema, scott.Kind);
            Assert.Equal(1, scott.Depth);
            Assert.Equal("SCOTT", scott.Owner);
            Assert.True(scott.IsMySchema);
            Assert.True(scott.Expandable);
            Assert.False(scott.Expanded);
            Assert.All(shown.Skip(2), r => Assert.False(r.IsMySchema));
            Assert.Equal(new[] { false, false, false, true, true }, shown.Skip(1).Select(r => r.IsSystemSchema));
        }

        [Fact]
        public void Build_MySchemaMaintained_ShownFirstEvenWhenSystemHidden()
        {
            var db = Db(mySchema: "SYSTEM");
            db.Schemas = new List<SchemaInfo> { Schema("SYS", true), Schema("HR"), Schema("SYSTEM", true) };

            var rows = Build(db, State(DbKey), showSystem: false);

            Assert.Equal(new[] { "개발", "SYSTEM", "HR" }, rows.Select(r => r.Text));
            Assert.True(rows[1].IsMySchema);
            Assert.True(rows[1].IsSystemSchema);
        }

        [Fact]
        public void Build_SchemaEntriesWithoutName_Skipped()
        {
            var db = Db();
            db.Schemas = new List<SchemaInfo> { null, Schema(null), Schema(""), Schema("HR") };

            var rows = Build(db, State(DbKey), showSystem: true);

            Assert.Equal(new[] { DbKey, TreeKeys.Schema("d1", "HR") }, Keys(rows));
        }

        [Fact]
        public void Build_ExpandedSchemaWithoutCounts_GroupCountsLoadNote()
        {
            var rows = Build(DbWithScott(), Open());

            var note = rows[2];
            Assert.Equal(ScottKey + S + "note", note.Key);
            Assert.Equal(2, note.Depth);
            Assert.Equal("불러오는 중…", note.Text);
            Assert.Equal(TreeLoadKind.GroupCounts, note.Load.Kind);
            Assert.Equal(ScottKey, note.Load.Key);
            Assert.Equal("d1", note.Load.DbId);
            Assert.Equal("SCOTT", note.Load.Owner);
            Assert.Equal(TreeKeys.Schema("d1", "HR"), rows[3].Key);
            Assert.Equal(4, rows.Count);
        }

        [Fact]
        public void Build_Counts_FourGroupsWithCounts_ZeroGroupNotExpandable()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(3, 0, 2, 0);
            var viewsKey = TreeKeys.Group("d1", "SCOTT", TreeGroups.View);

            // 개수 0인 묶음은 상태가 펼침이어도 펼쳐지지 않는다
            var rows = Build(db, Open(viewsKey));

            var groups = rows.Where(r => r.Kind == TreeRowKind.Group).ToList();
            Assert.Equal(new[] { "테이블", "뷰", "시퀀스", "프로시저·함수·패키지" }, groups.Select(g => g.Text));
            Assert.Equal(TreeGroups.All, groups.Select(g => g.ObjectType));
            Assert.Equal(new int?[] { 3, 0, 2, 0 }, groups.Select(g => g.Count));
            Assert.Equal(new[] { true, false, true, false }, groups.Select(g => g.Expandable));
            Assert.All(groups, g => Assert.False(g.Expanded));
            Assert.All(groups, g => Assert.Equal(2, g.Depth));
            Assert.All(groups, g => Assert.Equal("SCOTT", g.Owner));
            Assert.Equal(new[] { DbKey, ScottKey, ScottTables, viewsKey }, Keys(rows.Take(4)));
            Assert.Equal(7, rows.Count); // DB, SCOTT, 묶음 4, HR
        }

        [Fact]
        public void Build_CountsWithoutSomeGroups_MissingGroupIsZero()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = new Dictionary<string, int> { { TreeGroups.View, 2 } };

            var groups = Build(db, Open()).Where(r => r.Kind == TreeRowKind.Group).ToList();

            Assert.Equal(new int?[] { 0, 2, 0, 0 }, groups.Select(g => g.Count));
            Assert.Equal(new[] { false, true, false, false }, groups.Select(g => g.Expandable));
        }

        [Fact]
        public void Build_AllCountsZero_NoAccessNoteInsteadOfGroups()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(0, 0, 0, 0);

            var rows = Build(db, Open());

            var note = rows[2];
            Assert.Equal(TreeRowKind.Note, note.Kind);
            Assert.Equal("볼 수 있는 객체가 없습니다 (권한 필요)", note.Text);
            Assert.Equal(ScottKey + S + "note", note.Key);
            Assert.Null(note.Load);
            Assert.False(note.IsError);
            Assert.DoesNotContain(rows, r => r.Kind == TreeRowKind.Group);
        }

        [Fact]
        public void Build_ErrorOnSchemaKey_ErrorNoteInsteadOfChildren()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(3, 0, 0, 0);
            db.Errors[ScottKey] = "ORA-01031: 권한이 불충분합니다";
            db.Loading.Add(ScottKey);

            var rows = Build(db, Open(ScottTables));

            var note = rows[2];
            Assert.Equal(ScottKey + S + "note", note.Key);
            Assert.True(note.IsError);
            Assert.Null(note.Load);
            Assert.Equal("ORA-01031: 권한이 불충분합니다", note.Text);
            Assert.Equal(2, note.Depth);
            Assert.DoesNotContain(rows, r => r.Kind == TreeRowKind.Group);
            Assert.Equal(TreeKeys.Schema("d1", "HR"), rows[3].Key);
        }

        [Fact]
        public void Build_ErrorOnDbKey_ErrorNoteInsteadOfSchemas()
        {
            var db = DbWithScott();
            db.Errors[DbKey] = "ORA-00942";

            var rows = Build(db, State(DbKey));

            Assert.Equal(2, rows.Count);
            Assert.True(rows[1].IsError);
            Assert.Equal(1, rows[1].Depth);
        }

        [Fact]
        public void Build_ErrorOnGroupOrObjectKey_ErrorNoteReplacesLoadedChildren()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(2, 0, 0, 0);
            var page = Page(Obj("TABLE", "EMP"), Obj("TABLE", "DEPT"));
            page.HasMore = true;
            db.Objects[ScottTables] = page;
            db.Columns[EmpKey] = new List<ColumnInfo> { Col("EMPNO", 1, "NUMBER(4)") };
            db.Errors[EmpKey] = "ORA-00942: 테이블 또는 뷰가 존재하지 않습니다";

            var rows = Build(db, Open(ScottTables, EmpKey));

            var columnNote = rows[rows.FindIndex(r => r.Key == EmpKey) + 1];
            Assert.Equal(EmpKey + S + "note", columnNote.Key);
            Assert.True(columnNote.IsError);
            Assert.Null(columnNote.Load);
            Assert.Equal(4, columnNote.Depth);
            Assert.Equal("ORA-00942: 테이블 또는 뷰가 존재하지 않습니다", columnNote.Text);
            Assert.DoesNotContain(rows, r => r.Kind == TreeRowKind.Column);
            Assert.Single(rows, r => r.IsLoadMore);

            // "더 보기"가 실패하면 이미 불러온 객체도 오류 Note 하나로 바뀐다(다시 펼칠 때 화면이 오류를 지움)
            db.Errors[ScottTables] = "ORA-01013: 사용자가 작업을 취소했습니다";
            rows = Build(db, Open(ScottTables, EmpKey));

            var groupNote = rows[rows.FindIndex(r => r.Key == ScottTables) + 1];
            Assert.Equal(ScottTables + S + "note", groupNote.Key);
            Assert.True(groupNote.IsError);
            Assert.Null(groupNote.Load);
            Assert.Equal(3, groupNote.Depth);
            Assert.DoesNotContain(rows, r => r.Kind == TreeRowKind.Object);
            Assert.DoesNotContain(rows, r => r.IsLoadMore);
        }

        // ---------- Build: 객체 ----------

        [Fact]
        public void Build_ExpandedGroupWithoutPage_ObjectsLoadWithPageSize()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(3, 0, 0, 0);

            var rows = Build(db, Open(ScottTables));

            var note = rows[3];
            Assert.Equal(ScottTables + S + "note", note.Key);
            Assert.Equal(3, note.Depth);
            Assert.Equal("불러오는 중…", note.Text);
            Assert.Equal(TreeLoadKind.Objects, note.Load.Kind);
            Assert.Equal(ScottTables, note.Load.Key);
            Assert.Equal("d1", note.Load.DbId);
            Assert.Equal("SCOTT", note.Load.Owner);
            Assert.Equal(TreeGroups.Table, note.Load.Group);
            Assert.Equal(1000, note.Load.Limit);
        }

        [Fact]
        public void Build_ObjectsByNameWithHasMore_LoadMoreNoteWithNextLimit()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(5000, 0, 0, 0);
            var page = Page(Obj("TABLE", "EMP"), Obj("TABLE", "BONUS"), Obj("TABLE", "DEPT"));
            page.HasMore = true;
            db.Objects[ScottTables] = page;

            var rows = Build(db, Open(ScottTables));

            var objects = rows.Where(r => r.Kind == TreeRowKind.Object).ToList();
            Assert.Equal(new[] { "BONUS", "DEPT", "EMP" }, objects.Select(o => o.Text));
            Assert.All(objects, o => Assert.Equal(3, o.Depth));
            var more = rows[rows.IndexOf(objects.Last()) + 1];
            Assert.Equal(ScottTables + S + "more", more.Key);
            Assert.Equal(TreeRowKind.Note, more.Kind);
            Assert.Equal(3, more.Depth);
            Assert.Equal("더 보기 (지금 3개)", more.Text);
            Assert.True(more.IsLoadMore);
            Assert.False(more.Expandable);
            Assert.Equal(TreeLoadKind.Objects, more.Load.Kind);
            Assert.Equal(ScottTables, more.Load.Key);
            Assert.Equal(TreeGroups.Table, more.Load.Group);
            Assert.Equal("SCOTT", more.Load.Owner);
            Assert.Equal(5000, more.Load.Limit);

            page.Limit = 25000;
            Assert.Equal(0, Build(db, Open(ScottTables)).Single(r => r.IsLoadMore).Load.Limit);

            page.HasMore = false;
            Assert.DoesNotContain(Build(db, Open(ScottTables)), r => r.IsLoadMore);
        }

        [Fact]
        public void Build_ObjectRow_KeyTypeAndExpandableOnlyForTablesAndViews()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(1, 1, 1, 0);
            var views = TreeKeys.Group("d1", "SCOTT", TreeGroups.View);
            var sequences = TreeKeys.Group("d1", "SCOTT", TreeGroups.Sequence);
            var seqKey = TreeKeys.Object("d1", "SCOTT", "SEQUENCE", "EMP_SEQ");
            db.Objects[ScottTables] = Page(Obj("TABLE", "EMP"));
            db.Objects[views] = Page(Obj("VIEW", "EMP_V"));
            db.Objects[sequences] = Page(Obj("SEQUENCE", "EMP_SEQ"));

            // 시퀀스는 상태가 펼침이어도 펼쳐지지 않는다
            var rows = Build(db, Open(ScottTables, views, sequences, seqKey));

            var emp = rows.Single(r => r.Key == EmpKey);
            Assert.Equal(TreeRowKind.Object, emp.Kind);
            Assert.Equal("TABLE", emp.ObjectType);
            Assert.Equal("EMP", emp.ObjectName);
            Assert.Equal("SCOTT", emp.Owner);
            Assert.Equal("d1", emp.DbId);
            Assert.True(emp.Expandable);
            Assert.True(rows.Single(r => r.Key == TreeKeys.Object("d1", "SCOTT", "VIEW", "EMP_V")).Expandable);
            var seq = rows.Single(r => r.Key == seqKey);
            Assert.False(seq.Expandable);
            Assert.False(seq.Expanded);
            Assert.Null(seq.Detail);
            // 시퀀스 다음 행은 하위(Note 등)가 아니라 다음 묶음
            Assert.Equal(TreeKeys.Group("d1", "SCOTT", TreeGroups.Code), rows[rows.IndexOf(seq) + 1].Key);
        }

        [Fact]
        public void Build_TableNumRows_ApproxRowCountDetail()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(3, 0, 0, 0);
            db.Objects[ScottTables] = Page(Obj("TABLE", "BIG", 1234567), Obj("TABLE", "EMPTY", 0), Obj("TABLE", "NOSTATS"));

            var rows = Build(db, Open(ScottTables)).Where(r => r.Kind == TreeRowKind.Object).ToList();

            Assert.Equal(new[] { "≈1,234,567행", "≈0행", null }, rows.Select(r => r.Detail));
        }

        [Fact]
        public void Build_CodeGroup_FunctionAndPackageDetail()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(0, 0, 0, 3);
            var code = TreeKeys.Group("d1", "SCOTT", TreeGroups.Code);
            db.Objects[code] = Page(Obj("PACKAGE", "PKG_A"), Obj("PROCEDURE", "ADD_JOB"), Obj("FUNCTION", "F_TAX"));

            var rows = Build(db, Open(code)).Where(r => r.Kind == TreeRowKind.Object).ToList();

            Assert.Equal(new[] { "ADD_JOB", "F_TAX", "PKG_A" }, rows.Select(r => r.Text));
            Assert.Equal(new[] { null, "FUNCTION", "PACKAGE" }, rows.Select(r => r.Detail));
            Assert.Equal(new[] { "PROCEDURE", "FUNCTION", "PACKAGE" }, rows.Select(r => r.ObjectType));
            Assert.All(rows, r => Assert.False(r.Expandable));
            Assert.Equal(TreeKeys.Object("d1", "SCOTT", "FUNCTION", "F_TAX"), rows[1].Key);
        }

        [Fact]
        public void Build_InvalidObject_InvalidDetailJoinedWithTypeText()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(1, 1, 0, 2);
            var views = TreeKeys.Group("d1", "SCOTT", TreeGroups.View);
            var code = TreeKeys.Group("d1", "SCOTT", TreeGroups.Code);
            db.Objects[ScottTables] = Page(Obj("TABLE", "EMP", 14));
            db.Objects[views] = Page(Obj("VIEW", "BROKEN_V", status: "INVALID"));
            db.Objects[code] = Page(Obj("FUNCTION", "F_BAD", status: "INVALID"), Obj("PROCEDURE", "P_BAD", status: "INVALID"));

            var rows = Build(db, Open(ScottTables, views, code)).Where(r => r.Kind == TreeRowKind.Object).ToDictionary(r => r.Text);

            Assert.Equal("≈14행", rows["EMP"].Detail);
            Assert.Equal("INVALID", rows["BROKEN_V"].Detail);
            Assert.Equal("FUNCTION · INVALID", rows["F_BAD"].Detail);
            Assert.Equal("INVALID", rows["P_BAD"].Detail);
        }

        // ---------- Build: 열 ----------

        [Fact]
        public void Build_ExpandedTableWithoutColumns_ColumnsLoadNote()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(1, 0, 0, 0);
            db.Objects[ScottTables] = Page(Obj("TABLE", "EMP"));

            var rows = Build(db, Open(ScottTables, EmpKey));

            var note = rows[rows.FindIndex(r => r.Key == EmpKey) + 1];
            Assert.Equal(EmpKey + S + "note", note.Key);
            Assert.Equal(4, note.Depth);
            Assert.Equal("불러오는 중…", note.Text);
            Assert.Equal(TreeLoadKind.Columns, note.Load.Kind);
            Assert.Equal(EmpKey, note.Load.Key);
            Assert.Equal("d1", note.Load.DbId);
            Assert.Equal("SCOTT", note.Load.Owner);
            Assert.Equal("EMP", note.Load.ObjectName);
        }

        [Fact]
        public void Build_Columns_PositionOrderTypeDetailAndPrimaryKey()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(1, 0, 0, 0);
            db.Objects[ScottTables] = Page(Obj("TABLE", "EMP"));
            db.Columns[EmpKey] = new List<ColumnInfo>
            {
                Col("ENAME", 2, "VARCHAR2(10 BYTE)"),
                Col("DEPTNO", 3, "NUMBER(2)"),
                Col("EMPNO", 1, "NUMBER(4)", nullable: false, pk: true)
            };

            var rows = Build(db, Open(ScottTables, EmpKey));

            var columns = rows.Where(r => r.Kind == TreeRowKind.Column).ToList();
            Assert.Equal(new[] { "EMPNO", "ENAME", "DEPTNO" }, columns.Select(c => c.Text));
            Assert.Equal(new[] { "NUMBER(4) NOT NULL", "VARCHAR2(10 BYTE)", "NUMBER(2)" }, columns.Select(c => c.Detail));
            Assert.Equal(new[] { true, false, false }, columns.Select(c => c.IsPrimaryKey));
            var empno = columns[0];
            Assert.Equal(TreeKeys.Column("d1", "SCOTT", "EMP", "EMPNO"), empno.Key);
            Assert.Equal(4, empno.Depth);
            Assert.Equal("TABLE", empno.ObjectType);
            Assert.Equal("EMP", empno.ObjectName);
            Assert.Equal("EMPNO", empno.ColumnName);
            Assert.Equal("SCOTT", empno.Owner);
            Assert.False(empno.Expandable);
            Assert.Equal(rows.FindIndex(r => r.Key == EmpKey) + 1, rows.IndexOf(empno));
        }

        [Fact]
        public void Build_FullyExpanded_DepthPerKind()
        {
            var db = DbWithScott();
            db.GroupCounts[ScottKey] = Counts(1, 0, 0, 0);
            db.Objects[ScottTables] = Page(Obj("TABLE", "EMP"));
            db.Columns[EmpKey] = new List<ColumnInfo> { Col("EMPNO", 1, "NUMBER(4)") };

            var rows = Build(db, Open(ScottTables, EmpKey));

            Assert.Equal(new[] { 0, 1, 2, 3, 4, 2, 2, 2, 1 }, rows.Select(r => r.Depth));
            Assert.Equal(new[]
            {
                TreeRowKind.Database, TreeRowKind.Schema, TreeRowKind.Group, TreeRowKind.Object, TreeRowKind.Column,
                TreeRowKind.Group, TreeRowKind.Group, TreeRowKind.Group, TreeRowKind.Schema
            }, rows.Select(r => r.Kind));
        }

        [Fact]
        public void Build_KeyInLoading_NoteStillCarriesLoad()
        {
            var db = Db();
            db.Loading.Add(DbKey);

            var schemasNote = Build(db, State(DbKey))[1];

            Assert.Equal(TreeLoadKind.Schemas, schemasNote.Load.Kind);

            var withPage = DbWithScott();
            withPage.GroupCounts[ScottKey] = Counts(1, 0, 0, 0);
            withPage.Loading.Add(ScottTables);
            var objectsNote = Build(withPage, Open(ScottTables))[3];
            Assert.Equal(TreeLoadKind.Objects, objectsNote.Load.Kind);
            Assert.Equal("불러오는 중…", objectsNote.Text);

            // "더 보기"를 누른 뒤 불러오는 동안에도 같은 요청을 들고 있다
            var paged = DbWithScott();
            paged.GroupCounts[ScottKey] = Counts(5000, 0, 0, 0);
            var page = Page(Obj("TABLE", "EMP"));
            page.HasMore = true;
            paged.Objects[ScottTables] = page;
            paged.Loading.Add(ScottTables);
            var more = Build(paged, Open(ScottTables)).Single(r => r.IsLoadMore);
            Assert.Equal(TreeLoadKind.Objects, more.Load.Kind);
            Assert.Equal(5000, more.Load.Limit);
        }

        // ---------- BuildSearch ----------

        [Fact]
        public void BuildSearch_ObjectHit_DimPathDownToCollapsedHit()
        {
            var db = Db();
            db.GroupCounts[ScottKey] = Counts(14, 1, 0, 0);
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));

            var rows = Search(db, result, "emp");

            Assert.Equal(new[] { DbKey, ScottKey, ScottTables, EmpKey }, Keys(rows));
            Assert.All(rows.Take(3), r =>
            {
                Assert.True(r.IsDim);
                Assert.False(r.IsHit);
                Assert.True(r.Expandable);
                Assert.True(r.Expanded);
                Assert.Empty(r.Highlights);
            });
            Assert.True(rows[1].IsMySchema);
            var group = rows[2];
            Assert.Equal("테이블", group.Text);
            Assert.Equal(TreeGroups.Table, group.ObjectType);
            Assert.Equal(1, group.MatchCount);
            Assert.Equal(14, group.Count);
            var emp = rows[3];
            Assert.Equal(TreeRowKind.Object, emp.Kind);
            Assert.True(emp.IsHit);
            Assert.False(emp.IsDim);
            Assert.True(emp.Expandable);
            Assert.False(emp.Expanded);
            Assert.Equal("TABLE", emp.ObjectType);
            Assert.Equal(new[] { (0, 3) }, Spans(emp));
        }

        [Fact]
        public void BuildSearch_ColumnHit_DimTableOrViewAboveColumnHit()
        {
            var db = Db();
            var result = new TreeSearchResult();
            result.Columns.Add(ColumnHit("SCOTT", "VIEW", "EMP_V", "DEPTNO", "NUMBER(2)"));

            var rows = Search(db, result, "deptno");

            var viewKey = TreeKeys.Object("d1", "SCOTT", "VIEW", "EMP_V");
            var columnKey = TreeKeys.Column("d1", "SCOTT", "EMP_V", "DEPTNO");
            Assert.Equal(new[] { DbKey, ScottKey, TreeKeys.Group("d1", "SCOTT", TreeGroups.View), viewKey, columnKey }, Keys(rows));
            var group = rows[2];
            Assert.Equal(1, group.MatchCount);
            Assert.Null(group.Count); // 묶음 개수를 아직 안 불러옴
            var view = rows[3];
            Assert.True(view.IsDim);
            Assert.False(view.IsHit);
            Assert.True(view.Expandable);
            Assert.True(view.Expanded);
            Assert.Empty(view.Highlights);
            var column = rows[4];
            Assert.Equal(TreeRowKind.Column, column.Kind);
            Assert.True(column.IsHit);
            Assert.False(column.IsDim);
            Assert.False(column.Expandable);
            Assert.Equal("NUMBER(2)", column.Detail);
            Assert.Equal("VIEW", column.ObjectType);
            Assert.Equal("EMP_V", column.ObjectName);
            Assert.Equal("DEPTNO", column.ColumnName);
            Assert.Equal("SCOTT", column.Owner);
            Assert.Equal(new[] { (0, 6) }, Spans(column));
        }

        [Fact]
        public void BuildSearch_SchemaNameHit_CollapsedByDefault()
        {
            var db = Db();
            db.GroupCounts[ScottKey] = Counts(1, 0, 0, 0);
            var result = new TreeSearchResult();
            result.Schemas.Add(Schema("SCOTT"));

            var rows = Search(db, result, "cot");

            Assert.Equal(new[] { DbKey, ScottKey }, Keys(rows));
            Assert.True(rows[0].IsDim);
            Assert.True(rows[0].Expanded);
            var scott = rows[1];
            Assert.True(scott.IsHit);
            Assert.False(scott.IsDim);
            Assert.True(scott.Expandable);
            Assert.False(scott.Expanded);
            Assert.True(scott.IsMySchema);
            Assert.Equal(new[] { (1, 3) }, Spans(scott));
        }

        [Fact]
        public void BuildSearch_SchemaNameHitExpanded_CachedGroupsLikeNormalMode()
        {
            var db = Db();
            db.GroupCounts[ScottKey] = Counts(2, 0, 1, 0);
            db.Objects[ScottTables] = Page(Obj("TABLE", "EMP"), Obj("TABLE", "DEPT"));
            var result = new TreeSearchResult();
            result.Schemas.Add(Schema("SCOTT"));

            // 일반 하위 안에서 묶음을 펼치는 것도 searchState로 한다
            var rows = Search(db, result, "scott", State(ScottKey, ScottTables));

            Assert.True(rows[1].Expanded);
            var below = rows.Skip(2).ToList();
            Assert.Equal(new[] { "테이블", "DEPT", "EMP", "뷰", "시퀀스", "프로시저·함수·패키지" }, below.Select(r => r.Text));
            Assert.Equal(new int?[] { 2, null, null, 0, 1, 0 }, below.Select(r => r.Count));
            Assert.Equal(new[] { 2, 3, 3, 2, 2, 2 }, below.Select(r => r.Depth));
            Assert.All(below, r =>
            {
                Assert.False(r.IsHit);
                Assert.False(r.IsDim);
                Assert.Empty(r.Highlights);
                Assert.Null(r.MatchCount);
            });
            Assert.Equal(new[] { true, true, true, false, true, false }, below.Select(r => r.Expandable));
        }

        [Fact]
        public void BuildSearch_SchemaNameHitExpandedWithoutCounts_GroupCountsLoadNote()
        {
            var result = new TreeSearchResult();
            result.Schemas.Add(Schema("SCOTT"));

            var rows = Search(Db(), result, "scott", State(ScottKey));

            Assert.Equal(3, rows.Count);
            var note = rows[2];
            Assert.Equal(ScottKey + S + "note", note.Key);
            Assert.Equal(2, note.Depth);
            Assert.Equal(TreeLoadKind.GroupCounts, note.Load.Kind);
            Assert.Equal(ScottKey, note.Load.Key);
            Assert.Equal("SCOTT", note.Load.Owner);
        }

        [Fact]
        public void BuildSearch_ObjectHitExpanded_ColumnsFromCacheOrColumnsLoad()
        {
            var db = Db();
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));

            var loading = Search(db, result, "emp", State(EmpKey));

            Assert.Equal(EmpKey + S + "note", loading.Last().Key);
            Assert.Equal(TreeLoadKind.Columns, loading.Last().Load.Kind);
            Assert.Equal(EmpKey, loading.Last().Load.Key);

            db.Columns[EmpKey] = new List<ColumnInfo> { Col("ENAME", 2, "VARCHAR2(10)"), Col("EMPNO", 1, "NUMBER(4)", nullable: false, pk: true) };
            var rows = Search(db, result, "emp", State(EmpKey));

            var columns = rows.Skip(4).ToList();
            Assert.Equal(new[] { "EMPNO", "ENAME" }, columns.Select(c => c.Text));
            Assert.Equal(new[] { "NUMBER(4) NOT NULL", "VARCHAR2(10)" }, columns.Select(c => c.Detail));
            Assert.True(columns[0].IsPrimaryKey);
            Assert.All(columns, c =>
            {
                Assert.False(c.IsHit);
                Assert.False(c.IsDim);
                Assert.Empty(c.Highlights);
            });
        }

        [Fact]
        public void BuildSearch_AncestorCollapsedInSearchState_HidesDescendantsButStaysExpandable()
        {
            var db = Db();
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            result.Objects.Add(ObjectHit("HR", "TABLE", "EMPLOYEES"));
            var state = new TreeState();
            state.Expanded[ScottKey] = false;

            var rows = Search(db, result, "emp", state);

            Assert.Equal(new[]
            {
                DbKey, ScottKey, TreeKeys.Schema("d1", "HR"), TreeKeys.Group("d1", "HR", TreeGroups.Table), TreeKeys.Object("d1", "HR", "TABLE", "EMPLOYEES")
            }, Keys(rows));
            var scott = rows[1];
            Assert.True(scott.IsDim);
            Assert.True(scott.Expandable);
            Assert.False(scott.Expanded);

            state.Expanded[DbKey] = false;
            var db0 = Assert.Single(Search(db, result, "emp", state));
            Assert.True(db0.Expandable);
            Assert.False(db0.Expanded);
        }

        [Fact]
        public void BuildSearch_GroupOrColumnParentCollapsed_HidesHitsButKeepsMatchCountAndExpandable()
        {
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP_HIST"));
            result.Columns.Add(ColumnHit("SCOTT", "VIEW", "DEPT_V", "EMP_CNT", "NUMBER"));
            var viewsKey = TreeKeys.Group("d1", "SCOTT", TreeGroups.View);
            var deptV = TreeKeys.Object("d1", "SCOTT", "VIEW", "DEPT_V");
            var state = new TreeState();
            state.Expanded[ScottTables] = false;
            state.Expanded[deptV] = false;

            var rows = Search(Db(), result, "emp", state);

            Assert.Equal(new[] { DbKey, ScottKey, ScottTables, viewsKey, deptV }, Keys(rows));
            var tables = rows[2];
            Assert.True(tables.Expandable);
            Assert.False(tables.Expanded);
            Assert.Equal(2, tables.MatchCount);
            var view = rows[4];
            Assert.True(view.IsDim);
            Assert.True(view.Expandable);
            Assert.False(view.Expanded);
        }

        [Fact]
        public void BuildSearch_DbNameHitWithOtherHits_ExpandedHitWithSearchChildrenOnly()
        {
            var db = Db(name: "EMP 개발");
            db.Schemas = new List<SchemaInfo> { Schema("SCOTT"), Schema("HR") };
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));

            var rows = Search(db, result, "emp");

            // 하위 일치가 있으면 캐시의 스키마 목록(HR)이 아니라 일치 경로만 보인다
            Assert.Equal(new[] { DbKey, ScottKey, ScottTables, EmpKey }, Keys(rows));
            var dbRow = rows[0];
            Assert.True(dbRow.IsHit);
            Assert.False(dbRow.IsDim);
            Assert.True(dbRow.Expandable);
            Assert.True(dbRow.Expanded);
            Assert.Equal(new[] { (0, 3) }, Spans(dbRow));
        }

        [Fact]
        public void BuildSearch_HitExpandedWithLoadError_ErrorNoteLikeNormalMode()
        {
            var db = Db();
            db.GroupCounts[ScottKey] = Counts(1, 0, 0, 0);
            db.Errors[ScottKey] = "ORA-01031: 권한이 불충분합니다";
            var result = new TreeSearchResult();
            result.Schemas.Add(Schema("SCOTT"));

            var rows = Search(db, result, "scott", State(ScottKey));

            Assert.Equal(3, rows.Count);
            var note = rows[2];
            Assert.Equal(ScottKey + S + "note", note.Key);
            Assert.True(note.IsError);
            Assert.Null(note.Load);
            Assert.Equal(2, note.Depth);
            Assert.False(note.IsHit);
            Assert.False(note.IsDim);
        }

        [Fact]
        public void BuildSearch_DbNameHit_HitWithHighlightsCollapsed()
        {
            var db = Db(name: "Dev-dev DB");

            var row = Assert.Single(Search(db, new TreeSearchResult(), "DEV"));

            Assert.True(row.IsHit);
            Assert.False(row.IsDim);
            Assert.True(row.Expandable);
            Assert.False(row.Expanded);
            Assert.Equal(new[] { (0, 3), (4, 3) }, Spans(row));
        }

        [Fact]
        public void BuildSearch_DbNameHitExpanded_CachedSchemasLikeNormalMode()
        {
            var db = Db(name: "개발 DB");
            db.Schemas = new List<SchemaInfo> { Schema("SYS", true), Schema("HR"), Schema("SCOTT") };

            var hidden = Search(db, new TreeSearchResult(), "개발", State(DbKey), showSystem: false);
            var shown = Search(db, new TreeSearchResult(), "개발", State(DbKey), showSystem: true);

            Assert.Equal(new[] { "개발 DB", "SCOTT", "HR" }, hidden.Select(r => r.Text));
            Assert.Equal(new[] { "개발 DB", "SCOTT", "HR", "SYS" }, shown.Select(r => r.Text));
            Assert.True(hidden[0].IsHit);
            Assert.True(hidden[0].Expanded);
            Assert.Equal(new[] { (0, 2) }, Spans(hidden[0]));
            Assert.All(hidden.Skip(1), r =>
            {
                Assert.False(r.IsHit);
                Assert.False(r.IsDim);
                Assert.False(r.Expanded);
            });

            db.Schemas = null;
            var loading = Search(db, new TreeSearchResult(), "개발", State(DbKey));
            Assert.Equal(TreeLoadKind.Schemas, loading[1].Load.Kind);
        }

        [Fact]
        public void BuildSearch_OnlyDbsWithResultsEntryAndSomethingToShow()
        {
            var d1 = Db("d1", "개발");
            var d2 = Db("d2", "EMP 운영"); // 이름이 일치해도 results에 없으면(연결 안 됨) 빠진다
            var d3 = Db("d3", "검증");      // results는 있지만 일치가 없다
            var r1 = new TreeSearchResult();
            r1.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            var results = new Dictionary<string, TreeSearchResult> { { "d1", r1 }, { "d3", new TreeSearchResult() } };

            var rows = TreeRowsBuilder.BuildSearch(new[] { d1, d2, d3 }, results, "emp", new TreeState(), false);

            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.Equal("d1", r.DbId));
        }

        [Fact]
        public void BuildSearch_DbsInInputOrder()
        {
            var a = Db("a", "A");
            var b = Db("b", "B");
            var ra = new TreeSearchResult();
            ra.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            var rb = new TreeSearchResult();
            rb.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            var results = new Dictionary<string, TreeSearchResult> { { "a", ra }, { "b", rb } };

            var rows = TreeRowsBuilder.BuildSearch(new[] { b, a }, results, "emp", new TreeState(), false);

            Assert.Equal(new[] { "B", "A" }, rows.Where(r => r.Kind == TreeRowKind.Database).Select(r => r.Text));
            Assert.Equal(8, rows.Select(r => r.Key).Distinct().Count());
        }

        [Fact]
        public void BuildSearch_Truncated_NoteAtEndOfThatDb()
        {
            var d1 = Db("d1", "개발");
            var d2 = Db("d2", "운영");
            var r1 = new TreeSearchResult { Truncated = true };
            r1.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            r1.Columns.Add(ColumnHit("SCOTT", "TABLE", "DEPT", "EMP_COUNT", "NUMBER"));
            var r2 = new TreeSearchResult();
            r2.Objects.Add(ObjectHit("HR", "TABLE", "EMPLOYEES"));
            var results = new Dictionary<string, TreeSearchResult> { { "d1", r1 }, { "d2", r2 } };

            var rows = TreeRowsBuilder.BuildSearch(new[] { d1, d2 }, results, "emp", new TreeState(), false);

            var noteIndex = rows.FindIndex(r => r.Kind == TreeRowKind.Note);
            var note = rows[noteIndex];
            Assert.Equal(DbKey + S + "more", note.Key);
            Assert.Equal("결과가 많아 일부만 표시합니다 (DB마다 최대 500개)", note.Text);
            Assert.Equal(1, note.Depth);
            Assert.Null(note.Load);
            Assert.False(note.IsLoadMore);
            Assert.False(note.IsError);
            Assert.False(note.IsHit);
            Assert.False(note.IsDim);
            Assert.All(rows.Take(noteIndex), r => Assert.Equal("d1", r.DbId));
            Assert.Equal(TreeKeys.Db("d2"), rows[noteIndex + 1].Key);
            Assert.Equal(1, rows.Count(r => r.Kind == TreeRowKind.Note));
        }

        [Fact]
        public void BuildSearch_Error_ErrorNoteDirectlyUnderDb()
        {
            var result = new TreeSearchResult { Error = "ORA-01013: 사용자가 작업을 취소했습니다", Truncated = true };

            var rows = Search(Db(), result, "emp");

            Assert.Equal(3, rows.Count);
            var db = rows[0];
            Assert.True(db.IsDim);
            Assert.True(db.Expandable);
            Assert.True(db.Expanded);
            var note = rows[1];
            Assert.Equal(DbKey + S + "note", note.Key);
            Assert.True(note.IsError);
            Assert.Null(note.Load);
            Assert.Equal("ORA-01013: 사용자가 작업을 취소했습니다", note.Text);
            Assert.Equal(1, note.Depth);
            // 함께 있어도 키가 겹치지 않는다
            Assert.Equal(DbKey + S + "more", rows[2].Key);
        }

        [Fact]
        public void BuildSearch_ErrorWithPartialHits_ErrorNoteFirstThenHits()
        {
            // 객체 검색은 됐지만 열 검색이 실패한 경우 등
            var result = new TreeSearchResult { Error = "ORA-01652: 임시 세그먼트를 확장할 수 없습니다" };
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));

            var rows = Search(Db(), result, "emp");

            Assert.Equal(new[] { DbKey, DbKey + S + "note", ScottKey, ScottTables, EmpKey }, Keys(rows));
            Assert.True(rows[1].IsError);
            Assert.True(rows[4].IsHit);
        }

        [Fact]
        public void BuildSearch_Highlights_EveryOccurrenceIgnoringCase_OnlyOnHitRows()
        {
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("EMPS", "TABLE", "EMP_emp_Emp"));

            var rows = Search(Db(name: "개발"), result, "  eMp ");

            var obj = rows.Single(r => r.Kind == TreeRowKind.Object);
            Assert.Equal(new[] { (0, 3), (4, 3), (8, 3) }, Spans(obj));
            // 스키마 이름에도 검색어가 있지만 일치 행이 아니므로(상위 경로) 표시하지 않는다
            var schema = rows.Single(r => r.Kind == TreeRowKind.Schema);
            Assert.True(schema.IsDim);
            Assert.Empty(schema.Highlights);
            Assert.Empty(rows.Single(r => r.Kind == TreeRowKind.Group).Highlights);
        }

        [Fact]
        public void BuildSearch_Depths_PerKindAndNotes()
        {
            var result = new TreeSearchResult { Truncated = true };
            result.Columns.Add(ColumnHit("SCOTT", "TABLE", "EMP", "EMPNO", "NUMBER(4)"));

            var rows = Search(Db(), result, "empno");

            Assert.Equal(new[] { 0, 1, 2, 3, 4, 1 }, rows.Select(r => r.Depth));
            Assert.Equal(new[]
            {
                TreeRowKind.Database, TreeRowKind.Schema, TreeRowKind.Group, TreeRowKind.Object, TreeRowKind.Column, TreeRowKind.Note
            }, rows.Select(r => r.Kind));
        }

        [Fact]
        public void BuildSearch_ObjectHitAlsoColumnParent_OneRowThatIsHitAndExpanded()
        {
            var db = Db();
            db.GroupCounts[ScottKey] = Counts(14, 0, 0, 0);
            var result = new TreeSearchResult();
            result.Schemas.Add(Schema("SCOTT"));
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));
            result.Columns.Add(ColumnHit("SCOTT", "TABLE", "EMP", "EMPNO", "NUMBER(4)"));
            result.Columns.Add(ColumnHit("SCOTT", "TABLE", "EMP", "EMP_MGR", "NUMBER(4)"));
            result.Columns.Add(ColumnHit("SCOTT", "TABLE", "EMP", "EMPNO", "NUMBER(4)"));

            var rows = Search(db, result, "emp");

            Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
            Assert.Single(rows, r => r.Kind == TreeRowKind.Schema);
            Assert.Single(rows, r => r.Kind == TreeRowKind.Group);
            var emp = Assert.Single(rows, r => r.Kind == TreeRowKind.Object);
            Assert.True(emp.IsHit);
            Assert.False(emp.IsDim);
            Assert.True(emp.Expanded);
            Assert.Equal(new[] { "EMPNO", "EMP_MGR" }, rows.Where(r => r.Kind == TreeRowKind.Column).Select(r => r.Text));
            Assert.Equal(1, rows.Single(r => r.Kind == TreeRowKind.Group).MatchCount);
            // 스키마 이름도 일치하고 하위 일치도 있으면 일치 행이면서 펼침
            var scott = rows.Single(r => r.Kind == TreeRowKind.Schema);
            Assert.True(scott.IsHit);
            Assert.True(scott.Expanded);
        }

        [Fact]
        public void BuildSearch_Ordering_SchemasGroupsObjectsAndColumnsInHitOrder()
        {
            var db = Db();
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("HR", "TABLE", "X1"));
            result.Objects.Add(ObjectHit("ADMX", "TABLE", "X2"));
            result.Objects.Add(ObjectHit("SCOTT", "PACKAGE", "ZX_PKG"));
            result.Objects.Add(ObjectHit("SCOTT", "PROCEDURE", "AX_PROC"));
            result.Objects.Add(ObjectHit("SCOTT", "VIEW", "VX"));
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "TX_B"));
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "TX_A"));
            result.Objects.Add(ObjectHit("SCOTT", "SEQUENCE", "SX"));
            // 열은 결과 순서(COLUMN_ID 순) 그대로: 이름 순이 아니다
            result.Columns.Add(ColumnHit("SCOTT", "TABLE", "TX_A", "X_SECOND", "NUMBER"));
            result.Columns.Add(ColumnHit("SCOTT", "TABLE", "TX_A", "A_X", "NUMBER"));

            var rows = Search(db, result, "x");

            Assert.Equal(new[] { "SCOTT", "ADMX", "HR" }, rows.Where(r => r.Kind == TreeRowKind.Schema).Select(r => r.Text));
            Assert.Equal(new[] { "테이블", "뷰", "시퀀스", "프로시저·함수·패키지", "테이블", "테이블" },
                rows.Where(r => r.Kind == TreeRowKind.Group).Select(r => r.Text));
            Assert.Equal(new[] { "TX_A", "X_SECOND", "A_X", "TX_B", "VX", "SX", "AX_PROC", "ZX_PKG" },
                rows.SkipWhile(r => r.Kind != TreeRowKind.Object).TakeWhile(r => r.Owner == "SCOTT")
                    .Where(r => r.Kind == TreeRowKind.Object || r.Kind == TreeRowKind.Column).Select(r => r.Text));
            Assert.Equal("PACKAGE", rows.Single(r => r.Text == "ZX_PKG").Detail);
            Assert.Null(rows.Single(r => r.Text == "AX_PROC").Detail);
            Assert.Equal(2, rows.Single(r => r.Key == ScottTables).MatchCount);
        }

        [Fact]
        public void BuildSearch_UnknownTypesAndColumnsOfNonTables_Skipped()
        {
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "SYNONYM", "EMP_SYN"));
            result.Objects.Add(ObjectHit("SCOTT", "PACKAGE BODY", "EMP_PKG"));
            result.Columns.Add(ColumnHit("SCOTT", "SEQUENCE", "EMP_SEQ", "EMP", "NUMBER"));

            Assert.Empty(Search(Db(), result, "emp"));
        }

        [Fact]
        public void BuildSearch_SchemaFlagsForDimSchemaFromCache()
        {
            var db = Db();
            db.Schemas = new List<SchemaInfo> { Schema("SYS", true), Schema("SCOTT") };
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SYS", "TABLE", "DUAL"));

            var rows = Search(db, result, "dual", showSystem: true);

            var sys = rows.Single(r => r.Kind == TreeRowKind.Schema);
            Assert.True(sys.IsSystemSchema);
            Assert.False(sys.IsMySchema);
        }

        [Fact]
        public void BuildSearch_EmptyTerm_NoDbNameHitAndNoHighlights()
        {
            var result = new TreeSearchResult();
            result.Objects.Add(ObjectHit("SCOTT", "TABLE", "EMP"));

            var rows = Search(Db(), result, "   ");

            Assert.True(rows[0].IsDim);
            Assert.True(rows[3].IsHit);
            Assert.Empty(rows[3].Highlights);
            Assert.Empty(Search(Db(), new TreeSearchResult(), null));
        }
    }
}
