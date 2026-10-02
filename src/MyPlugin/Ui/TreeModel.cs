using System;
using System.Collections.Generic;

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

        public static string Db(string dbId) { throw new NotImplementedException(); }
        public static string Schema(string dbId, string owner) { throw new NotImplementedException(); }
        public static string Group(string dbId, string owner, string group) { throw new NotImplementedException(); }
        public static string Object(string dbId, string owner, string objectType, string name) { throw new NotImplementedException(); }
        public static string Column(string dbId, string owner, string objectName, string column) { throw new NotImplementedException(); }

        /// <summary>키의 DB id. 키 형식이 아니면 null.</summary>
        public static string DbIdOf(string key) { throw new NotImplementedException(); }

        /// <summary>상위 키들(위에서부터). 열의 상위 객체 키는 그 테이블·뷰 형식을 알 수 없으므로 objectType을 받는다.
        /// 예: 열 → [db, schema, group(TreeGroups.GroupOf(objectType)), object]. DB 키는 빈 목록.</summary>
        public static IReadOnlyList<string> Ancestors(string key, string columnObjectType = "TABLE") { throw new NotImplementedException(); }
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

        /// <summary>테이블, 뷰, 시퀀스, 프로시저·함수·패키지</summary>
        public static string Title(string group) { throw new NotImplementedException(); }

        /// <summary>OBJECT_TYPE → 묶음. 트리에 없는 종류면 null.</summary>
        public static string GroupOf(string objectType) { throw new NotImplementedException(); }

        /// <summary>묶음 → OBJECT_TYPE 목록(CODE면 PROCEDURE, FUNCTION, PACKAGE).</summary>
        public static string[] ObjectTypes(string group) { throw new NotImplementedException(); }

        /// <summary>펼치면 열이 나오는 형식(TABLE, VIEW).</summary>
        public static bool HasColumns(string objectType) { throw new NotImplementedException(); }
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
    /// - results[db].Truncated면 그 DB 맨 끝에 "결과가 많아 일부만 표시합니다 (DB마다 최대 500개)" Note. Error면 DB 아래 오류 Note.
    /// - Highlights: Text에서 term(앞뒤 공백 제거, 대소문자 무시)의 모든 위치.
    /// </summary>
    public static class TreeRowsBuilder
    {
        public const int ObjectPageSize = 1000;

        /// <summary>"더 보기" 다음 limit: 1000 → 5000 → 25000 → 0(전부).</summary>
        public static int NextLimit(int limit) { throw new NotImplementedException(); }

        public static List<TreeRow> Build(IReadOnlyList<DbTreeData> dbs, TreeState state, bool showSystem)
        {
            throw new NotImplementedException();
        }

        public static List<TreeRow> BuildSearch(IReadOnlyList<DbTreeData> dbs, IReadOnlyDictionary<string, TreeSearchResult> results,
            string term, TreeState searchState, bool showSystem)
        {
            throw new NotImplementedException();
        }
    }
}
