using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace MyPlugin
{
    /// <summary>
    /// 트리 불러오기·서버 검색 결과를 캐시(DbTreeData·TreeSearchResult) 형태로 정리한다(순수 로직, 테스트 대상).
    /// 조회 자체는 화면(DbTreePanel)이 DbSession.QueryAsync로 한다.
    /// </summary>
    internal static class TreeLoaderLogic
    {
        /// <summary>
        /// limit+1행까지 받은 객체 목록을 페이지로 만든다. limit보다 많으면 앞의 limit개만 두고 HasMore.
        /// limit 0(제한 없음)이면 모두 두고 HasMore는 false.
        /// </summary>
        public static ObjectPage Page(IEnumerable<DbObjectInfo> items, int limit)
        {
            var list = items == null ? new List<DbObjectInfo>() : items.ToList();
            var hasMore = limit > 0 && list.Count > limit;
            if (hasMore)
                list.RemoveRange(limit, list.Count - limit);
            return new ObjectPage { Items = list, HasMore = hasMore, Limit = Math.Max(0, limit) };
        }

        /// <summary>GroupCounts 조회의 한 행(OBJECT_TYPE, CNT). 값이 없으면 (null, 0).</summary>
        public static KeyValuePair<string, long> ReadGroupCount(IDataRecord record)
        {
            var type = record["OBJECT_TYPE"];
            var count = record["CNT"];
            return new KeyValuePair<string, long>(
                type == null || type is DBNull ? null : Convert.ToString(type, CultureInfo.InvariantCulture),
                count == null || count is DBNull ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// (OBJECT_TYPE, 개수) 행들을 묶음별 개수로 합친다(PROCEDURE·FUNCTION·PACKAGE → CODE).
        /// 네 묶음(TreeGroups.All)이 늘 있고 없던 묶음은 0. 트리에 없는 종류와 0 이하 개수는 버린다.
        /// </summary>
        public static Dictionary<string, int> GroupCounts(IEnumerable<KeyValuePair<string, long>> rows)
        {
            var counts = TreeGroups.All.ToDictionary(g => g, g => 0);
            foreach (var row in rows ?? Enumerable.Empty<KeyValuePair<string, long>>())
            {
                var group = TreeGroups.GroupOf(row.Key);
                if (group == null || row.Value <= 0)
                    continue;
                counts[group] = (int)Math.Min(int.MaxValue, counts[group] + row.Value);
            }
            return counts;
        }

        /// <summary>
        /// DB 하나의 검색 결과. 목록마다 limit+1개까지 받은 것이며, limit보다 많은 목록은 limit개로 잘라 Truncated로 표시한다.
        /// 목록이 null이면 그 검색은 하지 않았거나 실패한 것(빈 목록). 빈 오류 문장은 null로 둔다.
        /// </summary>
        public static TreeSearchResult SearchResult(IEnumerable<SchemaInfo> schemas, IEnumerable<SearchHit> objects, IEnumerable<SearchHit> columns,
            int limit, string error)
        {
            var result = new TreeSearchResult { Error = string.IsNullOrWhiteSpace(error) ? null : error };
            var truncated = AddLimited(result.Schemas, schemas, limit);
            truncated |= AddLimited(result.Objects, objects, limit);
            truncated |= AddLimited(result.Columns, columns, limit);
            result.Truncated = truncated;
            return result;
        }

        /// <summary>
        /// 화면이 바로 실행할 불러오기: Load가 있고 "더 보기"(누를 때만 실행)가 아니며, 그 DB가 그 키를 아직 불러오는 중이 아닌 행. 같은 키는 한 번만.
        /// dataOf: DbId → 그 DB의 캐시(없으면 null — 건너뜀).
        /// </summary>
        public static List<TreeLoadRequest> PendingLoads(IEnumerable<TreeRow> rows, Func<string, DbTreeData> dataOf)
        {
            var loads = new List<TreeLoadRequest>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows ?? Enumerable.Empty<TreeRow>())
            {
                var load = row == null ? null : row.Load;
                if (load == null || row.IsLoadMore || string.IsNullOrEmpty(load.Key) || string.IsNullOrEmpty(load.DbId))
                    continue;
                var db = dataOf == null ? null : dataOf(load.DbId);
                if (db == null || db.Loading.Contains(load.Key) || !seen.Add(load.Key))
                    continue;
                loads.Add(load);
            }
            return loads;
        }

        private static bool AddLimited<T>(List<T> target, IEnumerable<T> source, int limit)
        {
            if (source == null)
                return false;
            var list = source.ToList();
            var truncated = limit > 0 && list.Count > limit;
            target.AddRange(truncated ? list.Take(limit) : list);
            return truncated;
        }
    }
}
