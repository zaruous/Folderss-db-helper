using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace MyPlugin
{
    internal sealed class IndexColumnChoice
    {
        public string Name { get; set; }
        public bool Descending { get; set; }
    }
    // UI 선택 모델은 DBMS 문법을 알지 않는다. SQL 표현은 호출자가 제공한다.
    internal static class IndexColumnsLogic
    {
        public static List<IndexColumnChoice> Parse(string text, IEnumerable<string> available)
        {
            var names = new HashSet<string>(available, StringComparer.Ordinal);
            var result = new List<IndexColumnChoice>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var terms = new List<string>(); var part = new StringBuilder(); bool quoted = false;
            foreach (var ch in text)
            {
                if (ch == '"') quoted = !quoted;
                if (ch == ',' && !quoted) { terms.Add(part.ToString().Trim()); part.Clear(); }
                else part.Append(ch);
            }
            if (quoted) throw new ArgumentException("컬럼 목록의 따옴표가 맞지 않습니다.");
            terms.Add(part.ToString().Trim());
            foreach (var term in terms)
            {
                var name = term; bool desc = false;
                if (term.StartsWith("\"", StringComparison.Ordinal))
                {
                    int i = 1; var identifier = new StringBuilder(); bool closed = false;
                    for (; i < term.Length; i++)
                    {
                        if (term[i] != '"') identifier.Append(term[i]);
                        else if (i + 1 < term.Length && term[i + 1] == '"') { identifier.Append('"'); i++; }
                        else { i++; closed = true; break; }
                    }
                    if (!closed) throw new ArgumentException("컬럼 이름을 해석할 수 없습니다.");
                    name = identifier.ToString();
                    var order = term.Substring(i).Trim();
                    if (order.Length > 0 && !order.Equals("ASC", StringComparison.OrdinalIgnoreCase) && !order.Equals("DESC", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("표현식 인덱스는 컬럼 선택기로 변경할 수 없습니다.");
                    desc = order.Equals("DESC", StringComparison.OrdinalIgnoreCase);
                }
                else if (!names.Contains(name))
                {
                    var space = name.LastIndexOf(' ');
                    if (space >= 0) { var order = name.Substring(space + 1); if (order.Equals("ASC", StringComparison.OrdinalIgnoreCase) || order.Equals("DESC", StringComparison.OrdinalIgnoreCase)) { desc = order.Equals("DESC", StringComparison.OrdinalIgnoreCase); name = name.Substring(0, space).TrimEnd(); } }
                }
                if (!names.Contains(name)) throw new ArgumentException("테이블 컬럼으로 해석할 수 없는 기존 정의입니다: " + term);
                if (result.Any(c => c.Name == name)) throw new ArgumentException("인덱스 컬럼이 중복되었습니다: " + name);
                result.Add(new IndexColumnChoice { Name = name, Descending = desc });
            }
            return result;
        }
    }
}
