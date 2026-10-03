using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MyPlugin
{
    /// <summary>트리 행 하나에 붙는 화면 상태(TreeRow에 없는 것). 연결 상태·색은 DB 행에만 쓴다.</summary>
    internal sealed class RowBadges
    {
        public bool Connected { get; set; }
        public bool Connecting { get; set; }
        /// <summary>세션이 끊긴 오류를 냈음(다시 연결해야 함).</summary>
        public bool Broken { get; set; }
        /// <summary>그 DB를 대상으로 실행 중인 탭이 있음.</summary>
        public bool Running { get; set; }
        public bool ReadOnly { get; set; }
        /// <summary>커밋 대기 표시(DbSession.PendingText). 없으면 null.</summary>
        public string Pending { get; set; }
        /// <summary>접속 색 표시(OracleConnectionProfile.Color).</summary>
        public string Color { get; set; }
        /// <summary>검색: 지금 고른 일치(Enter·▲▼로 옮겨 다니는 위치).</summary>
        public bool Current { get; set; }
        /// <summary>"더 보기" 행: 지금 불러오는 중.</summary>
        public bool Loading { get; set; }
        /// <summary>연결 중인 접속의 저장 값이 연결할 때와 다름(다시 연결하면 적용).</summary>
        public bool ProfileChanged { get; set; }
    }

    /// <summary>트리 패널의 화면과 무관한 판단(순수 로직, 테스트 대상): 행 비교, 검색 막대·바닥줄 문장, 펼침 상태 다루기.</summary>
    internal static class TreePanelLogic
    {
        /// <summary>검색 결과 아래에 화면이 직접 붙이는 안내 행의 키. TreeKeys 형식이 아니라 DB id가 없다(DbIdOf → null).</summary>
        public const string OfflineNoteKey = "search\u001Foffline";

        private const char Sep = '\u001E';

        /// <summary>
        /// 행이 화면에 보이는 모양을 한 문자열로. 같은 키의 행을 새로 만들었을 때 이 값이 같으면 다시 그리지 않는다.
        /// </summary>
        public static string Signature(TreeRow row, RowBadges badges)
        {
            if (row == null)
                return "";
            var sb = new StringBuilder();
            sb.Append(row.Key).Append(Sep)
              .Append((int)row.Kind).Append(Sep)
              .Append(row.Depth).Append(Sep)
              .Append(row.Text).Append(Sep)
              .Append(row.Detail).Append(Sep)
              .Append(Flags(row.Expandable, row.Expanded, row.IsHit, row.IsDim, row.IsMySchema, row.IsSystemSchema, row.IsPrimaryKey,
                  row.IsLoadMore, row.IsError)).Append(Sep)
              .Append(row.Count).Append(Sep)
              .Append(row.MatchCount).Append(Sep);
            foreach (var span in row.Highlights)
                sb.Append(span.Start).Append(',').Append(span.Length).Append(';');
            if (badges != null)
            {
                sb.Append(Sep)
                  .Append(Flags(badges.Connected, badges.Connecting, badges.Broken, badges.Running, badges.ReadOnly, badges.Current, badges.Loading,
                      badges.ProfileChanged))
                  .Append(Sep).Append(badges.Pending)
                  .Append(Sep).Append(badges.Color);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 이전 목록과 새 목록을 키로 맞춘다: 앞에서부터 같은 키 수(prefix)와, 그 뒤 남은 부분의 뒤에서부터 같은 키 수(suffix).
        /// 가운데(이전 [prefix, 이전 수 - suffix), 새 [prefix, 새 수 - suffix))만 지우고 넣으면 된다. 같은 키의 행은 그 자리에서 내용만 바꾼다.
        /// </summary>
        public static void Align(IReadOnlyList<string> oldKeys, IReadOnlyList<string> newKeys, out int prefix, out int suffix)
        {
            var oldCount = oldKeys == null ? 0 : oldKeys.Count;
            var newCount = newKeys == null ? 0 : newKeys.Count;
            prefix = 0;
            while (prefix < oldCount && prefix < newCount && string.Equals(oldKeys[prefix], newKeys[prefix], StringComparison.Ordinal))
                prefix++;
            suffix = 0;
            while (suffix < oldCount - prefix && suffix < newCount - prefix
                   && string.Equals(oldKeys[oldCount - 1 - suffix], newKeys[newCount - 1 - suffix], StringComparison.Ordinal))
                suffix++;
        }

        /// <summary>
        /// 묶음 행의 개수 표시. 검색이면 "일치 / 전체"(같으면 전체만, 전체를 모르면 일치 수만), 아니면 전체 수. 모르면 "".
        /// </summary>
        public static string GroupMeta(TreeRow row)
        {
            if (row == null || row.Kind != TreeRowKind.Group)
                return "";
            if (row.MatchCount.HasValue)
            {
                if (!row.Count.HasValue)
                    return Number(row.MatchCount.Value);
                return row.MatchCount.Value == row.Count.Value
                    ? Number(row.Count.Value)
                    : Number(row.MatchCount.Value) + " / " + Number(row.Count.Value);
            }
            return row.Count.HasValue ? Number(row.Count.Value) : "";
        }

        /// <summary>보이는 검색 일치 행의 키(위에서부터). Enter·▲▼는 이 순서로 옮겨 다닌다.</summary>
        public static List<string> HitKeys(IEnumerable<TreeRow> rows)
        {
            return (rows ?? Enumerable.Empty<TreeRow>()).Where(r => r != null && r.IsHit).Select(r => r.Key).ToList();
        }

        /// <summary>
        /// 검색 막대: "일치 n개 (DB a · 스키마 b · 객체 c · 열 d)". DB는 DB 이름 일치가 있을 때만, 열은 열까지 찾을 때만 넣는다.
        /// 보이는 일치가 없으면 "일치 0개".
        /// </summary>
        public static string SearchSummary(IEnumerable<TreeRow> rows, bool columns)
        {
            int db = 0, schema = 0, obj = 0, column = 0;
            foreach (var row in rows ?? Enumerable.Empty<TreeRow>())
            {
                if (row == null || !row.IsHit)
                    continue;
                switch (row.Kind)
                {
                    case TreeRowKind.Database: db++; break;
                    case TreeRowKind.Schema: schema++; break;
                    case TreeRowKind.Column: column++; break;
                    default: obj++; break;
                }
            }
            var total = db + schema + obj + column;
            if (total == 0)
                return "일치 0개";
            var parts = new List<string>();
            if (db > 0)
                parts.Add("DB " + Number(db));
            parts.Add("스키마 " + Number(schema));
            parts.Add("객체 " + Number(obj));
            if (columns)
                parts.Add("열 " + Number(column));
            return "일치 " + Number(total) + "개 (" + string.Join(" · ", parts) + ")";
        }

        /// <summary>검색 막대의 위치 "i/n". 지금 위치가 없으면 "".</summary>
        public static string Position(int index, int count)
        {
            return index >= 0 && index < count ? Number(index + 1) + "/" + Number(count) : "";
        }

        /// <summary>다음(step &gt; 0)·이전 일치 위치. 지금 위치가 없으면 다음은 처음, 이전은 마지막. 끝에서는 반대쪽 끝으로 돈다. 일치가 없으면 -1.</summary>
        public static int Step(int index, int step, int count)
        {
            if (count <= 0)
                return -1;
            if (index < 0 || index >= count)
                return step < 0 ? count - 1 : 0;
            var next = index + (step < 0 ? -1 : 1);
            if (next < 0)
                return count - 1;
            return next >= count ? 0 : next;
        }

        /// <summary>일반 모드 바닥줄: "DB n개 · 연결 m개"(+ " · 내장 스키마 숨김").</summary>
        public static string NormalFooter(int dbCount, int connectedCount, bool systemHidden)
        {
            return "DB " + Number(dbCount) + "개 · 연결 " + Number(connectedCount) + "개" + (systemHidden ? " · 내장 스키마 숨김" : "");
        }

        /// <summary>검색 모드 바닥줄. 아직 결과를 기다리는 DB가 있으면 "검색 중…".</summary>
        public static string SearchFooter(int connectedCount, int waitingCount)
        {
            if (waitingCount > 0)
                return "검색 중…";
            return "검색 결과 · 연결된 DB " + Number(connectedCount) + "개 · 종류마다 최대 " + Number(OracleMetadata.SearchLimit) + "개";
        }

        /// <summary>검색 결과가 없을 때 안내. 열을 찾지 않았거나 내장 스키마를 뺐으면 줄을 바꿔 힌트를 붙인다.</summary>
        public static string NoMatchText(string term, bool columns, bool includeSystem)
        {
            var text = "'" + (term ?? "").Trim() + "'와(과) 일치하는 항목이 없습니다 (검색 대상: " + (columns ? "스키마·객체·열" : "스키마·객체") + ").";
            if (!columns)
                text += "\n열 이름이면 검색 대상을 '스키마·객체·열'로 바꾸세요.";
            if (!includeSystem)
                text += "\n내장 스키마는 [내장]을 체크해야 검색됩니다.";
            return text;
        }

        /// <summary>검색하지 않은(연결 안 된) DB 안내. 없으면 null.</summary>
        public static string OfflineNote(int count)
        {
            return count > 0 ? "연결 안 된 DB " + Number(count) + "개는 검색하지 않았습니다" : null;
        }

        /// <summary>내장 스키마를 숨겨 안 보이는 스키마가 있는지(연결되어 스키마 목록을 불러온 DB 기준, 내 스키마는 늘 보임).</summary>
        public static bool HidesSystemSchemas(IEnumerable<DbTreeData> dbs, bool showSystem)
        {
            if (showSystem || dbs == null)
                return false;
            return dbs.Any(db => db != null && db.Connected && db.Schemas != null
                && db.Schemas.Any(s => s != null && s.OracleMaintained && !string.IsNullOrEmpty(s.Name) && s.Name != db.MySchema));
        }

        /// <summary>
        /// 키의 상위 노드를 모두 펼친다(검색을 지운 뒤에도 고른 항목이 보이게). 열이면 그 테이블·뷰의 형식(columnObjectType)으로 상위 객체 키를 만든다.
        /// </summary>
        public static void ExpandAncestors(TreeState state, string key, string columnObjectType)
        {
            if (state == null || string.IsNullOrEmpty(key))
                return;
            foreach (var ancestor in TreeKeys.Ancestors(key, columnObjectType))
                state.Expanded[ancestor] = true;
        }

        /// <summary>그 DB의 펼침 상태를 모두 지운다(연결을 끊을 때 — DB 행도 접힘).</summary>
        public static void ForgetDb(TreeState state, string dbId)
        {
            if (state == null || string.IsNullOrEmpty(dbId))
                return;
            foreach (var key in state.Expanded.Keys.Where(k => TreeKeys.DbIdOf(k) == dbId).ToList())
                state.Expanded.Remove(key);
        }

        /// <summary>바로 위 노드의 키(DB 행이면 null). 열이면 행의 ObjectType(그 테이블·뷰의 형식)으로 상위 객체 키를 만든다.</summary>
        public static string ParentKey(TreeRow row)
        {
            if (row == null)
                return null;
            var ancestors = TreeKeys.Ancestors(row.Key, row.ObjectType);
            return ancestors.Count == 0 ? null : ancestors[ancestors.Count - 1];
        }

        private static string Flags(params bool[] values)
        {
            var chars = new char[values.Length];
            for (var i = 0; i < values.Length; i++)
                chars[i] = values[i] ? '1' : '0';
            return new string(chars);
        }

        private static string Number(int value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
