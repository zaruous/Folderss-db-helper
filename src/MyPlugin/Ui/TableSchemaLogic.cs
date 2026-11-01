using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPlugin
{
    /// <summary>
    /// 편집 표 안의 컬럼 목록 표기(DBMS 무관): 이름을 "..."로 감싸고 ", "로 잇는다("A", "B" DESC). IndexColumnsLogic.Parse가 되읽는다.
    /// 실제 DDL의 식별자 표기(Oracle·PostgreSQL "…", SQL Server […])는 ISchemaDdl이 정한다.
    /// </summary>
    internal static class TableSchemaLogic
    {
        public static string QuoteName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new ArgumentException("이름을 입력하세요.");
            return "\"" + name.Trim().Replace("\"", "\"\"") + "\"";
        }

        public static string IndexColumns(IEnumerable<IndexColumnChoice> columns)
            => string.Join(", ", columns.Select(c => QuoteName(c.Name) + (c.Descending ? " DESC" : "")));
    }
}
