using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MyPlugin
{
    /// <summary>
    /// 트랜잭션 블록 안에서 실행할 수 없는 문장 판정(<see cref="IDbProvider.RequiresNoTransaction"/>).
    /// 문장 앞 단어 몇 개(주석·공백 건너뜀, 대문자)로 본다. 목록에 없는 문장은 트랜잭션 안에서 실행한다 — 서버가 거부하면 그 오류가 그대로 보인다.
    /// </summary>
    internal static class DdlTransactionRules
    {
        private static readonly Regex Concurrently = new Regex(@"\bCONCURRENTLY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// PostgreSQL: CREATE·DROP DATABASE, ALTER DATABASE … SET TABLESPACE, CREATE·DROP TABLESPACE, ALTER SYSTEM, VACUUM,
        /// CREATE·DROP INDEX CONCURRENTLY, REINDEX … CONCURRENTLY·REINDEX DATABASE·SYSTEM, 인자 없는 CLUSTER, CREATE·DROP SUBSCRIPTION, DISCARD ALL.
        /// </summary>
        public static bool PostgresNoTransaction(SqlStatement statement)
        {
            var w = LeadingWords(statement, 4);
            if (w.Count == 0)
                return false;
            var text = statement.Text ?? "";
            switch (w[0])
            {
                case "VACUUM":
                    return true;
                case "CLUSTER":
                    return w.Count == 1;
                case "DISCARD":
                    return At(w, 1) == "ALL";
                case "REINDEX":
                    return At(w, 1) == "DATABASE" || At(w, 1) == "SYSTEM" || Concurrently.IsMatch(text);
                case "ALTER":
                    if (At(w, 1) == "SYSTEM")
                        return true;
                    return At(w, 1) == "DATABASE" && Regex.IsMatch(text, @"\bSET\s+TABLESPACE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                case "CREATE":
                case "DROP":
                    var what = At(w, 1) == "UNIQUE" ? At(w, 2) : At(w, 1);
                    if (what == "DATABASE" || what == "TABLESPACE" || what == "SUBSCRIPTION")
                        return true;
                    return what == "INDEX" && Concurrently.IsMatch(text);
                default:
                    return false;
            }
        }

        /// <summary>
        /// SQL Server: CREATE·ALTER·DROP DATABASE, CREATE·ALTER·DROP FULLTEXT CATALOG·INDEX, BACKUP, RESTORE, RECONFIGURE, KILL.
        /// </summary>
        public static bool SqlServerNoTransaction(SqlStatement statement)
        {
            var w = LeadingWords(statement, 3);
            if (w.Count == 0)
                return false;
            switch (w[0])
            {
                case "BACKUP":
                case "RESTORE":
                case "RECONFIGURE":
                case "KILL":
                    return true;
                case "CREATE":
                case "ALTER":
                case "DROP":
                    return At(w, 1) == "DATABASE" || At(w, 1) == "FULLTEXT";
                default:
                    return false;
            }
        }

        /// <summary>문장 앞의 단어(영문자·숫자·'_') 최대 count개, 대문자. 주석·공백은 건너뛰고 단어가 아닌 글자에서 멈춘다.</summary>
        internal static List<string> LeadingWords(SqlStatement statement, int count)
        {
            var words = new List<string>();
            var s = statement == null ? null : statement.Text;
            if (string.IsNullOrEmpty(s))
                return words;
            var i = 0;
            while (i < s.Length && words.Count < count)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    var end = s.IndexOf('\n', i);
                    i = end < 0 ? s.Length : end + 1;
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                }
                else if (char.IsLetterOrDigit(c) || c == '_')
                {
                    var word = new StringBuilder();
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                        word.Append(s[i++]);
                    words.Add(word.ToString().ToUpperInvariant());
                }
                else
                {
                    break;
                }
            }
            return words;
        }

        private static string At(List<string> words, int index)
        {
            return index < words.Count ? words[index] : "";
        }
    }
}
