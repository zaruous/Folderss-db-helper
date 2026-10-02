using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    public enum TreeRowKind { Database, Schema, Group, Object, Column, Note }

    public struct TextSpan
    {
        public int Start;
        public int Length;
        public TextSpan(int start, int length) { Start = start; Length = length; }
    }

    /// <summary>트리 한 줄. 화면은 평면 목록(ListBox)에 Depth만큼 들여 써서 그린다. WPF와 무관한 순수 데이터.</summary>
    public sealed class TreeRow
    {
        public string Key { get; set; }
        public TreeRowKind Kind { get; set; }
        public int Depth { get; set; }

        /// <summary>DB 이름, 스키마·객체·열 이름, 묶음 제목("테이블"), 안내 문장(Note).</summary>
        public string Text { get; set; }

        /// <summary>보조 글자. DB: user@host:port/service, 열: "NUMBER(4) NOT NULL", 테이블: "≈1,234행"(통계 있을 때),
        /// 프로시저 묶음의 FUNCTION·PACKAGE: "FUNCTION"/"PACKAGE", INVALID 객체: "INVALID".</summary>
        public string Detail { get; set; }

        public bool Expandable { get; set; }
        public bool Expanded { get; set; }

        /// <summary>검색: 이 노드 이름이 검색어와 일치.</summary>
        public bool IsHit { get; set; }

        /// <summary>검색: 일치하지 않지만 일치 노드의 상위라서 보이는 노드(흐리게).</summary>
        public bool IsDim { get; set; }

        /// <summary>Text 안 일치 위치(대소문자 무시).</summary>
        public List<TextSpan> Highlights { get; } = new List<TextSpan>();

        /// <summary>Group: 전체 개수(알면). </summary>
        public int? Count { get; set; }

        /// <summary>Group(검색): 보이는 일치 개수. 화면은 Count가 있으면 "일치 / 전체", 없으면 일치 수만.</summary>
        public int? MatchCount { get; set; }

        public bool IsMySchema { get; set; }
        public bool IsSystemSchema { get; set; }
        public bool IsPrimaryKey { get; set; }

        public string DbId { get; set; }
        public string Owner { get; set; }
        /// <summary>Group: TreeGroups 값. Object·Column: ALL_OBJECTS.OBJECT_TYPE(열이면 그 테이블·뷰의 형식).</summary>
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string ColumnName { get; set; }

        /// <summary>Note 중 데이터를 불러와야 하는 자리("불러오는 중…")와 "더 보기": 화면이 이 요청을 실행한다(이미 Loading이면 건너뜀).</summary>
        public TreeLoadRequest Load { get; set; }

        /// <summary>Note가 "더 보기"(누르면 Load 실행)면 true.</summary>
        public bool IsLoadMore { get; set; }

        /// <summary>Note가 오류 안내면 true.</summary>
        public bool IsError { get; set; }
    }

    /// <summary>
    /// 트리 노드 키. 구분자는 이름에 쓰이지 않는 U+001F(따옴표 식별자에는 ':' 등 거의 모든 문자가 올 수 있음).
    /// db␟{dbId} / s␟{dbId}␟{owner} / g␟{dbId}␟{owner}␟{group} / o␟{dbId}␟{owner}␟{objectType}␟{name} / c␟{dbId}␟{owner}␟{objectName}␟{column}
    /// Note 행 키는 부모 키 + "␟note" 또는 "␟more".
    /// </summary>
    public static class TreeKeys
    {
        public const char Sep = '\u001F';

        /// <summary>안내 Note(불러오는 중, 오류, 권한 필요) 키의 끝 부분.</summary>
        internal const string NoteSuffix = "note";

        /// <summary>"더 보기", 검색의 "결과가 많아 일부만 표시" Note 키의 끝 부분. 오류 Note와 같은 부모 아래 함께 있어도 키가 겹치지 않는다.</summary>
        internal const string MoreSuffix = "more";

        public static string Db(string dbId) { return "db" + Sep + dbId; }
        public static string Schema(string dbId, string owner) { return "s" + Sep + dbId + Sep + owner; }
        public static string Group(string dbId, string owner, string group) { return "g" + Sep + dbId + Sep + owner + Sep + group; }
        public static string Object(string dbId, string owner, string objectType, string name) { return "o" + Sep + dbId + Sep + owner + Sep + objectType + Sep + name; }
        public static string Column(string dbId, string owner, string objectName, string column) { return "c" + Sep + dbId + Sep + owner + Sep + objectName + Sep + column; }

        /// <summary>키의 DB id. 키 형식이 아니면 null. Note 행 키도 그 부모의 DB id.</summary>
        public static string DbIdOf(string key)
        {
            var parts = Split(key);
            return parts == null ? null : parts[1];
        }

        /// <summary>상위 키들(위에서부터). 열의 상위 객체 키는 그 테이블·뷰 형식을 알 수 없으므로 objectType을 받는다.
        /// 예: 열 → [db, schema, group(TreeGroups.GroupOf(objectType)), object]. DB 키는 빈 목록.
        /// Note 행 키는 부모의 상위 + 부모. 키 형식이 아니면 빈 목록.</summary>
        public static IReadOnlyList<string> Ancestors(string key, string columnObjectType = "TABLE")
        {
            var list = new List<string>();
            var parts = Split(key);
            if (parts == null)
                return list;
            var kind = parts[0];
            if (parts.Length > PartCount(kind))
            {
                var parent = key.Substring(0, key.LastIndexOf(Sep));
                list.AddRange(Ancestors(parent, columnObjectType));
                list.Add(parent);
                return list;
            }
            if (kind == "db")
                return list;
            var dbId = parts[1];
            list.Add(Db(dbId));
            if (kind == "s")
                return list;
            var owner = parts[2];
            list.Add(Schema(dbId, owner));
            if (kind == "g")
                return list;
            var objectType = kind == "o" ? parts[3] : string.IsNullOrEmpty(columnObjectType) ? TreeGroups.Table : columnObjectType;
            var group = TreeGroups.GroupOf(objectType);
            if (group != null)
                list.Add(Group(dbId, owner, group));
            if (kind == "c")
                list.Add(Object(dbId, owner, objectType, parts[3]));
            return list;
        }

        /// <summary>키를 부분으로 나눈다. Note 키는 끝에 "note"/"more" 부분이 하나 더 있다. 키 형식이 아니면 null.</summary>
        private static string[] Split(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            var parts = key.Split(Sep);
            var count = PartCount(parts[0]);
            if (count == 0)
                return null;
            if (parts.Length == count)
                return parts;
            if (parts.Length == count + 1 && (parts[count] == NoteSuffix || parts[count] == MoreSuffix))
                return parts;
            return null;
        }

        private static int PartCount(string kind)
        {
            switch (kind)
            {
                case "db": return 2;
                case "s": return 3;
                case "g": return 4;
                case "o":
                case "c": return 5;
                default: return 0;
            }
        }
    }

    /// <summary>객체 종류 묶음.</summary>
    public static class TreeGroups
    {
        public const string Table = "TABLE";
        public const string View = "VIEW";
        public const string Sequence = "SEQUENCE";
        /// <summary>PROCEDURE·FUNCTION·PACKAGE</summary>
        public const string Code = "CODE";

        public static readonly string[] All = { Table, View, Sequence, Code };

        /// <summary>테이블, 뷰, 시퀀스, 프로시저·함수·패키지. 모르는 묶음이면 받은 값 그대로.</summary>
        public static string Title(string group)
        {
            switch (group)
            {
                case Table: return "테이블";
                case View: return "뷰";
                case Sequence: return "시퀀스";
                case Code: return "프로시저·함수·패키지";
                default: return group;
            }
        }

        /// <summary>OBJECT_TYPE → 묶음. 트리에 없는 종류면 null.</summary>
        public static string GroupOf(string objectType)
        {
            switch (objectType)
            {
                case "TABLE": return Table;
                case "VIEW": return View;
                case "SEQUENCE": return Sequence;
                case "PROCEDURE":
                case "FUNCTION":
                case "PACKAGE": return Code;
                default: return null;
            }
        }

        /// <summary>묶음 → OBJECT_TYPE 목록(CODE면 PROCEDURE, FUNCTION, PACKAGE). 모르는 묶음이면 빈 배열. 부를 때마다 새 배열.</summary>
        public static string[] ObjectTypes(string group)
        {
            switch (group)
            {
                case Table: return new[] { "TABLE" };
                case View: return new[] { "VIEW" };
                case Sequence: return new[] { "SEQUENCE" };
                case Code: return new[] { "PROCEDURE", "FUNCTION", "PACKAGE" };
                default: return new string[0];
            }
        }

        /// <summary>펼치면 열이 나오는 형식(TABLE, VIEW).</summary>
        public static bool HasColumns(string objectType)
        {
            return objectType == "TABLE" || objectType == "VIEW";
        }
    }

    public sealed class ObjectPage
    {
        public List<DbObjectInfo> Items { get; set; } = new List<DbObjectInfo>();
        /// <summary>limit보다 많아 일부만 불러옴.</summary>
        public bool HasMore { get; set; }
        /// <summary>이 페이지를 불러올 때 쓴 limit(0 = 제한 없음).</summary>
        public int Limit { get; set; }
    }

    /// <summary>DB(접속) 하나의 표시 정보와 불러온 메타데이터 캐시. 화면이 채우고 빌더는 읽기만 한다.</summary>
    public sealed class DbTreeData
    {
        public string DbId { get; set; }
        public string Name { get; set; }
        /// <summary>user@host:port/service</summary>
        public string Detail { get; set; }
        public bool Connected { get; set; }
        public bool Connecting { get; set; }
        /// <summary>접속 사용자의 스키마(대문자). 스키마 목록 맨 위에 두고 "내 스키마"로 표시.</summary>
        public string MySchema { get; set; }

        /// <summary>null = 아직 안 불러옴.</summary>
        public List<SchemaInfo> Schemas { get; set; }

        /// <summary>스키마 키 → 묶음 → 개수. 없으면 아직 안 불러옴.</summary>
        public Dictionary<string, Dictionary<string, int>> GroupCounts { get; } = new Dictionary<string, Dictionary<string, int>>();

        /// <summary>묶음 키 → 객체 페이지. 없으면 아직 안 불러옴.</summary>
        public Dictionary<string, ObjectPage> Objects { get; } = new Dictionary<string, ObjectPage>();

        /// <summary>객체 키(테이블·뷰) → 열. 없으면 아직 안 불러옴.</summary>
        public Dictionary<string, List<ColumnInfo>> Columns { get; } = new Dictionary<string, List<ColumnInfo>>();

        /// <summary>지금 불러오는 중인 노드 키.</summary>
        public HashSet<string> Loading { get; } = new HashSet<string>();

        /// <summary>노드 키 → 불러오기 오류 문장(예: ORA-01031 권한 없음). 화면은 다시 펼칠 때 지운다.</summary>
        public Dictionary<string, string> Errors { get; } = new Dictionary<string, string>();
    }

    /// <summary>DB 하나의 서버 검색 결과(OracleMetadata.Search*). 이미 내장 스키마 포함 여부가 반영된 결과.</summary>
    public sealed class TreeSearchResult
    {
        public List<SchemaInfo> Schemas { get; } = new List<SchemaInfo>();
        public List<SearchHit> Objects { get; } = new List<SearchHit>();
        public List<SearchHit> Columns { get; } = new List<SearchHit>();
        /// <summary>결과가 많아 일부만 받음.</summary>
        public bool Truncated { get; set; }
        /// <summary>검색 실패 문장(있으면 그 DB 아래 오류 Note).</summary>
        public string Error { get; set; }
    }

    public enum TreeLoadKind { Schemas, GroupCounts, Objects, Columns }

    public sealed class TreeLoadRequest
    {
        public TreeLoadKind Kind { get; set; }
        /// <summary>불러온 결과를 넣을 노드 키(DB / 스키마 / 묶음 / 객체 키). Loading·Errors도 이 키로 관리한다.</summary>
        public string Key { get; set; }
        public string DbId { get; set; }
        public string Owner { get; set; }
        public string Group { get; set; }
        public string ObjectName { get; set; }
        /// <summary>Objects만: 쓸 limit(0 = 제한 없음).</summary>
        public int Limit { get; set; }
    }

    /// <summary>펼침 상태. 키가 없으면 기본값(일반 모드: 접힘, 검색 모드: 규칙에 따름).</summary>
    public sealed class TreeState
    {
        public Dictionary<string, bool> Expanded { get; } = new Dictionary<string, bool>();
    }

    /// <summary>
    /// 캐시와 펼침 상태로 트리 행을 만든다(순수 로직, 테스트 대상).
    /// 일반 모드(Build):
    /// - DB(프로필 순서). 연결 안 됨이면 펼칠 수 있지만(Expandable) 하위 없음 — 화면이 펼칠 때 연결한다. 연결 중이면 Detail 뒤에 표시는 화면 몫.
    /// - 펼친 DB: Schemas가 null이면 "불러오는 중…" Note(Load=Schemas). 있으면 스키마 행: 내 스키마 먼저, 나머지 이름 순. showSystem=false면 OracleMaintained 제외(내 스키마는 항상 보임).
    /// - 펼친 스키마: GroupCounts가 없으면 Load=GroupCounts Note. 있으면 묶음 4개(개수 0인 묶음은 Expandable=false). 모든 개수가 0이면 묶음 대신 "볼 수 있는 객체가 없습니다 (권한 필요)" Note.
    /// - 펼친 묶음: Objects가 없으면 Load=Objects(Limit=ObjectPageSize) Note. 있으면 객체 행(이름 순, TABLE·VIEW만 Expandable), HasMore면 "더 보기" Note(IsLoadMore, Load=Objects, Limit=NextLimit).
    /// - 펼친 테이블·뷰: Columns가 없으면 Load=Columns Note. 있으면 열 행(Position 순).
    /// - 어떤 노드든 Errors에 키가 있으면 그 노드의 하위 대신 오류 Note(IsError, Load 없음).
    /// - Loading에 키가 있어도 Note의 Load는 그대로 둔다(화면이 Loading을 보고 건너뜀).
    /// 검색 모드(BuildSearch): results에 있는(=연결된) DB만. 일치 노드와 그 상위 경로만 보인다.
    /// - 일치: DB 이름, 스키마 이름(results.Schemas), 객체 이름(results.Objects), 열 이름(results.Columns — 그 테이블·뷰는 IsDim 상위로).
    /// - 하위에 일치가 있는 노드: 기본 펼침(searchState에 false면 접힘). 접혀도 Expandable.
    /// - 하위에 일치가 없는 일치 노드: 기본 접힘. searchState에 true면 캐시(dbs)로 일반 모드와 같은 하위를 보인다(없으면 Load Note).
    /// - 묶음 행: MatchCount = 그 묶음에서 보이는 객체 수, Count = 캐시의 GroupCounts(있으면).
    /// - results[db].Truncated면 그 DB 맨 끝에 "결과가 많아 일부만 표시합니다 (스키마·객체·열마다 최대 500개) — 더 좁혀 검색하세요" Note. Error면 DB 아래 오류 Note.
    /// - Highlights: Text에서 term(앞뒤 공백 제거, 대소문자 무시)의 모든 위치.
    /// </summary>
    public static class TreeRowsBuilder
    {
        public const int ObjectPageSize = 1000;

        private const string LoadingText = "불러오는 중…";
        private const string NoAccessText = "볼 수 있는 객체가 없습니다 (권한 필요)";
        // 한도는 DB 하나의 스키마·객체·열 검색마다 따로다(OracleMetadata.SearchLimit)
        private const string TruncatedText = "결과가 많아 일부만 표시합니다 (스키마·객체·열마다 최대 500개) — 더 좁혀 검색하세요";

        private static readonly int[] LimitSteps = { ObjectPageSize, 5000, 25000 };

        /// <summary>"더 보기" 다음 limit: 1000 → 5000 → 25000 → 0(전부).</summary>
        public static int NextLimit(int limit)
        {
            if (limit <= 0)
                return 0;
            foreach (var step in LimitSteps)
            {
                if (step > limit)
                    return step;
            }
            return 0;
        }

        public static List<TreeRow> Build(IReadOnlyList<DbTreeData> dbs, TreeState state, bool showSystem)
        {
            var rows = new List<TreeRow>();
            if (dbs == null)
                return rows;
            state = state ?? new TreeState();
            foreach (var db in dbs)
            {
                if (db != null)
                    AddRow(rows, new BuildContext(db, state, showSystem), DbRow(db));
            }
            return rows;
        }

        /// <summary>results: DbId → 그 DB의 검색 결과. term이 비면 DB 이름은 일치하지 않고 Highlights도 없다.</summary>
        public static List<TreeRow> BuildSearch(IReadOnlyList<DbTreeData> dbs, IReadOnlyDictionary<string, TreeSearchResult> results,
            string term, TreeState searchState, bool showSystem)
        {
            var rows = new List<TreeRow>();
            if (dbs == null || results == null)
                return rows;
            var needle = (term ?? "").Trim();
            searchState = searchState ?? new TreeState();
            foreach (var db in dbs)
            {
                TreeSearchResult result;
                if (db == null || db.DbId == null || !results.TryGetValue(db.DbId, out result) || result == null)
                    continue;
                var root = CollectHits(db, result);
                root.Hit = Contains(db.Name, needle);
                if (!string.IsNullOrEmpty(result.Error))
                    root.HeadNote = ErrorNote(root.Row, result.Error);
                if (result.Truncated)
                    root.TailNote = Note(root.Row, TreeKeys.MoreSuffix, TruncatedText);
                // 일치도 안내 Note도 없는 DB는 보이지 않는다(일치 노드와 그 상위 경로만 보임).
                if (root.Hit || root.HasContent)
                    EmitSearch(rows, new BuildContext(db, searchState, showSystem), root, needle);
            }
            return rows;
        }

        // ---------- 일반 모드(검색 모드에서 하위 일치가 없는 일치 노드를 펼칠 때도 씀) ----------

        /// <summary>행을 넣고, 펼쳐져 있으면(상태에 없으면 접힘) 하위 행을 이어 붙인다.</summary>
        private static void AddRow(List<TreeRow> rows, BuildContext ctx, TreeRow row)
        {
            row.Expanded = row.Expandable && IsExpanded(ctx.State, row.Key, false);
            rows.Add(row);
            if (row.Expanded)
                AppendChildren(rows, ctx, row);
        }

        private static void AppendChildren(List<TreeRow> rows, BuildContext ctx, TreeRow parent)
        {
            var db = ctx.Db;
            // 연결 안 된 DB는 펼쳐도 하위가 없다(화면이 펼칠 때 연결한다).
            if (parent.Kind == TreeRowKind.Database && !db.Connected)
                return;
            string error;
            if (db.Errors.TryGetValue(parent.Key, out error))
            {
                rows.Add(ErrorNote(parent, error));
                return;
            }
            switch (parent.Kind)
            {
                case TreeRowKind.Database:
                    AppendSchemas(rows, ctx, parent);
                    break;
                case TreeRowKind.Schema:
                    AppendGroups(rows, ctx, parent);
                    break;
                case TreeRowKind.Group:
                    AppendObjects(rows, ctx, parent);
                    break;
                case TreeRowKind.Object:
                    AppendColumns(rows, ctx, parent);
                    break;
            }
        }

        private static void AppendSchemas(List<TreeRow> rows, BuildContext ctx, TreeRow dbRow)
        {
            var db = ctx.Db;
            if (db.Schemas == null)
            {
                rows.Add(LoadNote(dbRow, new TreeLoadRequest { Kind = TreeLoadKind.Schemas, Key = dbRow.Key, DbId = db.DbId }));
                return;
            }
            // 내 스키마는 내장이어도 보인다(SYSTEM으로 접속한 경우 등).
            var schemaRows = db.Schemas
                .Where(s => s != null && !string.IsNullOrEmpty(s.Name) && (ctx.ShowSystem || !s.OracleMaintained || IsMySchema(db, s.Name)))
                .Select(s => SchemaRow(db, s.Name, s.OracleMaintained))
                .ToList();
            schemaRows.Sort(CompareSchemas);
            foreach (var row in schemaRows)
                AddRow(rows, ctx, row);
        }

        private static void AppendGroups(List<TreeRow> rows, BuildContext ctx, TreeRow schemaRow)
        {
            Dictionary<string, int> counts;
            if (!ctx.Db.GroupCounts.TryGetValue(schemaRow.Key, out counts) || counts == null)
            {
                rows.Add(LoadNote(schemaRow, new TreeLoadRequest { Kind = TreeLoadKind.GroupCounts, Key = schemaRow.Key, DbId = ctx.Db.DbId, Owner = schemaRow.Owner }));
                return;
            }
            // ALL_USERS에는 나오지만 이 계정이 볼 수 있는 객체가 하나도 없는 스키마: 빈 묶음 4개 대신 이유를 보인다.
            if (!TreeGroups.All.Any(g => CountOf(counts, g) > 0))
            {
                rows.Add(Note(schemaRow, TreeKeys.NoteSuffix, NoAccessText));
                return;
            }
            foreach (var group in TreeGroups.All)
            {
                var row = GroupRow(ctx.Db, schemaRow.Owner, group);
                row.Count = CountOf(counts, group);
                row.Expandable = row.Count > 0;
                AddRow(rows, ctx, row);
            }
        }

        private static void AppendObjects(List<TreeRow> rows, BuildContext ctx, TreeRow groupRow)
        {
            ObjectPage page;
            if (!ctx.Db.Objects.TryGetValue(groupRow.Key, out page) || page == null)
            {
                rows.Add(LoadNote(groupRow, ObjectsLoad(ctx.Db, groupRow, ObjectPageSize)));
                return;
            }
            var objectRows = (page.Items ?? Enumerable.Empty<DbObjectInfo>())
                .Where(o => o != null)
                .Select(o => ObjectRow(ctx.Db.DbId, groupRow.Owner, o.Type, o.Name, o.NumRows, o.Status))
                .ToList();
            objectRows.Sort(CompareObjects);
            foreach (var row in objectRows)
                AddRow(rows, ctx, row);
            if (page.HasMore)
            {
                var more = Note(groupRow, TreeKeys.MoreSuffix, "더 보기 (지금 " + objectRows.Count.ToString(CultureInfo.InvariantCulture) + "개)");
                more.IsLoadMore = true;
                more.Load = ObjectsLoad(ctx.Db, groupRow, NextLimit(page.Limit));
                rows.Add(more);
            }
        }

        private static void AppendColumns(List<TreeRow> rows, BuildContext ctx, TreeRow objectRow)
        {
            List<ColumnInfo> columns;
            if (!ctx.Db.Columns.TryGetValue(objectRow.Key, out columns) || columns == null)
            {
                rows.Add(LoadNote(objectRow, new TreeLoadRequest
                {
                    Kind = TreeLoadKind.Columns, Key = objectRow.Key, DbId = ctx.Db.DbId, Owner = objectRow.Owner, ObjectName = objectRow.ObjectName
                }));
                return;
            }
            foreach (var column in columns.Where(c => c != null).OrderBy(c => c.Position))
            {
                var row = ColumnRow(ctx.Db.DbId, objectRow.Owner, objectRow.ObjectType, objectRow.ObjectName, column.Name,
                    column.TypeLabel + (column.Nullable ? "" : " NOT NULL"));
                row.IsPrimaryKey = column.PrimaryKey;
                AddRow(rows, ctx, row);
            }
        }

        private static TreeLoadRequest ObjectsLoad(DbTreeData db, TreeRow groupRow, int limit)
        {
            return new TreeLoadRequest
            {
                Kind = TreeLoadKind.Objects, Key = groupRow.Key, DbId = db.DbId, Owner = groupRow.Owner, Group = groupRow.ObjectType, Limit = limit
            };
        }

        // ---------- 검색 모드 ----------

        /// <summary>DB 하나의 검색 결과를 일치 노드와 그 상위 경로로 모은다. 같은 키는 한 노드로 합친다.</summary>
        private static SearchNode CollectHits(DbTreeData db, TreeSearchResult result)
        {
            var root = new SearchNode(DbRow(db));
            // 상위 경로로만 보이는 스키마에도 내장 표시를 하려고 캐시의 스키마 목록을 함께 본다.
            var maintained = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var s in (db.Schemas ?? Enumerable.Empty<SchemaInfo>()).Concat(result.Schemas))
            {
                if (s != null && s.Name != null)
                    maintained[s.Name] = s.OracleMaintained;
            }
            foreach (var s in result.Schemas)
            {
                if (s != null && !string.IsNullOrEmpty(s.Name))
                    SchemaNode(root, db, maintained, s.Name).Hit = true;
            }
            foreach (var hit in result.Objects)
            {
                var objectNode = ObjectNode(root, db, maintained, hit);
                if (objectNode != null)
                    objectNode.Hit = true;
            }
            foreach (var hit in result.Columns)
            {
                if (hit == null || string.IsNullOrEmpty(hit.ColumnName) || !TreeGroups.HasColumns(hit.ObjectType))
                    continue;
                var objectNode = ObjectNode(root, db, maintained, hit);
                if (objectNode != null)
                    objectNode.Child(ColumnRow(db.DbId, hit.Owner, hit.ObjectType, hit.ObjectName, hit.ColumnName, hit.ColumnType)).Hit = true;
            }
            return root;
        }

        private static SearchNode SchemaNode(SearchNode root, DbTreeData db, Dictionary<string, bool> maintained, string owner)
        {
            bool isSystem;
            maintained.TryGetValue(owner, out isSystem);
            return root.Child(SchemaRow(db, owner, isSystem));
        }

        /// <summary>객체(또는 열 일치의 상위 테이블·뷰) 노드를 스키마·묶음 노드 아래에 둔다. 트리에 없는 종류면 null.</summary>
        private static SearchNode ObjectNode(SearchNode root, DbTreeData db, Dictionary<string, bool> maintained, SearchHit hit)
        {
            var group = hit == null ? null : TreeGroups.GroupOf(hit.ObjectType);
            if (group == null || string.IsNullOrEmpty(hit.Owner) || string.IsNullOrEmpty(hit.ObjectName))
                return null;
            var groupRow = GroupRow(db, hit.Owner, group);
            Dictionary<string, int> counts;
            if (db.GroupCounts.TryGetValue(TreeKeys.Schema(db.DbId, hit.Owner), out counts) && counts != null)
                groupRow.Count = CountOf(counts, group);
            var groupNode = SchemaNode(root, db, maintained, hit.Owner).Child(groupRow);
            return groupNode.Child(ObjectRow(db.DbId, hit.Owner, hit.ObjectType, hit.ObjectName, null, null));
        }

        private static void EmitSearch(List<TreeRow> rows, BuildContext ctx, SearchNode node, string needle)
        {
            var row = node.Row;
            row.IsHit = node.Hit;
            row.IsDim = !node.Hit;
            if (node.Hit)
                AddHighlights(row, needle);
            if (!node.HasContent)
            {
                // 하위에 일치가 없는 일치 노드: 기본 접힘. searchState에서 펼치면 캐시로 일반 모드와 같은 하위를 보인다.
                AddRow(rows, ctx, row);
                return;
            }
            if (row.Kind == TreeRowKind.Group)
                row.MatchCount = node.Children.Count;
            row.Expandable = true;
            row.Expanded = IsExpanded(ctx.State, row.Key, true);
            rows.Add(row);
            if (!row.Expanded)
                return;
            if (node.HeadNote != null)
                rows.Add(node.HeadNote);
            foreach (var child in Ordered(node))
                EmitSearch(rows, ctx, child, needle);
            if (node.TailNote != null)
                rows.Add(node.TailNote);
        }

        private static List<SearchNode> Ordered(SearchNode node)
        {
            var children = new List<SearchNode>(node.Children);
            switch (node.Row.Kind)
            {
                case TreeRowKind.Database:
                    children.Sort((a, b) => CompareSchemas(a.Row, b.Row));
                    break;
                case TreeRowKind.Schema:
                    children.Sort((a, b) => Array.IndexOf(TreeGroups.All, a.Row.ObjectType).CompareTo(Array.IndexOf(TreeGroups.All, b.Row.ObjectType)));
                    break;
                case TreeRowKind.Group:
                    children.Sort((a, b) => CompareObjects(a.Row, b.Row));
                    break;
                // 열은 검색 결과 순서(테이블마다 COLUMN_ID 순) 그대로 둔다.
            }
            return children;
        }

        private static void AddHighlights(TreeRow row, string needle)
        {
            if (needle.Length == 0 || row.Text == null)
                return;
            var index = row.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                row.Highlights.Add(new TextSpan(index, needle.Length));
                index = row.Text.IndexOf(needle, index + needle.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool Contains(string text, string needle)
        {
            return needle.Length > 0 && text != null && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------- 행 ----------

        private static TreeRow DbRow(DbTreeData db)
        {
            return new TreeRow
            {
                Key = TreeKeys.Db(db.DbId), Kind = TreeRowKind.Database, Depth = 0, Text = db.Name, Detail = db.Detail,
                DbId = db.DbId, Expandable = true
            };
        }

        private static TreeRow SchemaRow(DbTreeData db, string owner, bool oracleMaintained)
        {
            return new TreeRow
            {
                Key = TreeKeys.Schema(db.DbId, owner), Kind = TreeRowKind.Schema, Depth = 1, Text = owner,
                DbId = db.DbId, Owner = owner, Expandable = true, IsMySchema = IsMySchema(db, owner), IsSystemSchema = oracleMaintained
            };
        }

        /// <summary>Count·Expandable은 부르는 쪽이 정한다(일반 모드: 개수 &gt; 0, 검색 모드: 보이는 일치가 있음).</summary>
        private static TreeRow GroupRow(DbTreeData db, string owner, string group)
        {
            return new TreeRow
            {
                Key = TreeKeys.Group(db.DbId, owner, group), Kind = TreeRowKind.Group, Depth = 2, Text = TreeGroups.Title(group),
                DbId = db.DbId, Owner = owner, ObjectType = group
            };
        }

        /// <summary>Detail: "≈n행"(통계 있을 때), 프로시저 묶음의 FUNCTION·PACKAGE는 형식, INVALID — 여럿이면 " · "로 잇는다.</summary>
        private static TreeRow ObjectRow(string dbId, string owner, string objectType, string name, long? numRows, string status)
        {
            var details = new List<string>();
            if (numRows.HasValue)
                details.Add("≈" + numRows.Value.ToString("N0", CultureInfo.InvariantCulture) + "행");
            if (TreeGroups.GroupOf(objectType) == TreeGroups.Code && objectType != "PROCEDURE")
                details.Add(objectType);
            if (status == "INVALID")
                details.Add("INVALID");
            return new TreeRow
            {
                Key = TreeKeys.Object(dbId, owner, objectType, name), Kind = TreeRowKind.Object, Depth = 3, Text = name,
                Detail = details.Count == 0 ? null : string.Join(" · ", details),
                DbId = dbId, Owner = owner, ObjectType = objectType, ObjectName = name, Expandable = TreeGroups.HasColumns(objectType)
            };
        }

        private static TreeRow ColumnRow(string dbId, string owner, string objectType, string objectName, string column, string detail)
        {
            return new TreeRow
            {
                Key = TreeKeys.Column(dbId, owner, objectName, column), Kind = TreeRowKind.Column, Depth = 4, Text = column, Detail = detail,
                DbId = dbId, Owner = owner, ObjectType = objectType, ObjectName = objectName, ColumnName = column
            };
        }

        private static TreeRow Note(TreeRow parent, string suffix, string text)
        {
            return new TreeRow
            {
                Key = parent.Key + TreeKeys.Sep + suffix, Kind = TreeRowKind.Note, Depth = parent.Depth + 1, Text = text,
                DbId = parent.DbId, Owner = parent.Owner
            };
        }

        private static TreeRow LoadNote(TreeRow parent, TreeLoadRequest load)
        {
            var note = Note(parent, TreeKeys.NoteSuffix, LoadingText);
            note.Load = load;
            return note;
        }

        private static TreeRow ErrorNote(TreeRow parent, string error)
        {
            var note = Note(parent, TreeKeys.NoteSuffix, error);
            note.IsError = true;
            return note;
        }

        // ---------- 공통 ----------

        /// <summary>내 스키마 먼저, 나머지는 이름 순.</summary>
        private static int CompareSchemas(TreeRow a, TreeRow b)
        {
            if (a.IsMySchema != b.IsMySchema)
                return a.IsMySchema ? -1 : 1;
            return string.CompareOrdinal(a.Owner, b.Owner);
        }

        private static int CompareObjects(TreeRow a, TreeRow b)
        {
            var byName = string.CompareOrdinal(a.ObjectName, b.ObjectName);
            return byName != 0 ? byName : string.CompareOrdinal(a.ObjectType, b.ObjectType);
        }

        private static bool IsMySchema(DbTreeData db, string owner)
        {
            return db.MySchema != null && owner == db.MySchema;
        }

        private static int CountOf(Dictionary<string, int> counts, string group)
        {
            int count;
            return counts.TryGetValue(group, out count) ? count : 0;
        }

        private static bool IsExpanded(TreeState state, string key, bool defaultValue)
        {
            bool expanded;
            return state.Expanded.TryGetValue(key, out expanded) ? expanded : defaultValue;
        }

        private sealed class BuildContext
        {
            public BuildContext(DbTreeData db, TreeState state, bool showSystem)
            {
                Db = db;
                State = state;
                ShowSystem = showSystem;
            }

            public DbTreeData Db { get; }

            /// <summary>일반 모드는 펼침 상태, 검색 모드는 searchState(일반 하위를 펼칠 때도 이것을 쓴다).</summary>
            public TreeState State { get; }

            public bool ShowSystem { get; }
        }

        /// <summary>검색 결과 나무의 노드: 일치 노드 또는 일치의 상위 경로.</summary>
        private sealed class SearchNode
        {
            private readonly Dictionary<string, SearchNode> _byKey = new Dictionary<string, SearchNode>();

            public SearchNode(TreeRow row)
            {
                Row = row;
            }

            public TreeRow Row { get; }
            public bool Hit { get; set; }
            public List<SearchNode> Children { get; } = new List<SearchNode>();

            /// <summary>DB만: 바로 아래 검색 오류 Note.</summary>
            public TreeRow HeadNote { get; set; }

            /// <summary>DB만: 맨 끝 "결과가 많아 일부만 표시합니다" Note.</summary>
            public TreeRow TailNote { get; set; }

            /// <summary>펼치면 검색 결과로 보일 하위가 있음. 없으면 일반 모드 규칙으로 펼친다.</summary>
            public bool HasContent { get { return Children.Count > 0 || HeadNote != null || TailNote != null; } }

            /// <summary>같은 키의 하위가 이미 있으면 그것을 돌려준다(객체 일치이면서 열 일치의 상위인 객체 등).</summary>
            public SearchNode Child(TreeRow row)
            {
                SearchNode child;
                if (!_byKey.TryGetValue(row.Key, out child))
                {
                    child = new SearchNode(row);
                    _byKey.Add(row.Key, child);
                    Children.Add(child);
                }
                return child;
            }
        }
    }
}
