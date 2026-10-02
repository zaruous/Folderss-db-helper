using System;
using System.Collections.Generic;
using System.Text;

namespace MyPlugin
{
    public enum SqlKind
    {
        /// <summary>SELECT, WITH</summary>
        Query,
        /// <summary>INSERT, UPDATE, DELETE, MERGE</summary>
        Dml,
        /// <summary>CREATE, ALTER(ALTER SESSION 제외), DROP, TRUNCATE, RENAME, GRANT, REVOKE, COMMENT, PURGE, ANALYZE, AUDIT, NOAUDIT, FLASHBACK. Oracle은 DDL 전후로 자동 커밋한다.</summary>
        Ddl,
        /// <summary>BEGIN, DECLARE, CALL, EXEC/EXECUTE(BEGIN … END;로 바꿔 실행)</summary>
        PlSql,
        /// <summary>COMMIT, ROLLBACK, SAVEPOINT, SET TRANSACTION</summary>
        Transaction,
        /// <summary>그 밖(ALTER SESSION, LOCK TABLE, EXPLAIN PLAN 등)</summary>
        Other
    }

    public enum SqlTransactionAction
    {
        None,
        /// <summary>COMMIT [WORK] (뒤에 COMMENT·WRITE 옵션이 있어도 커밋. FORCE는 Other)</summary>
        Commit,
        /// <summary>ROLLBACK [WORK] (TO SAVEPOINT·FORCE 아님)</summary>
        Rollback,
        /// <summary>SAVEPOINT, ROLLBACK TO SAVEPOINT, SET TRANSACTION, COMMIT·ROLLBACK FORCE — SQL로 그대로 실행</summary>
        Other
    }

    /// <summary>실행할 문장 하나.</summary>
    public sealed class SqlStatement
    {
        /// <summary>실행할 텍스트. 앞뒤 공백 제거(앞의 주석도 뺀다 — Start부터). PL/SQL 블록이 아니면 끝의 ';'를 뺀다(ODP.NET은 ';'가 있으면 ORA-00911).
        /// PL/SQL 블록은 END …; 의 ';'를 유지하고 끝의 '/' 줄은 뺀다. 문장 안의 주석은 그대로 둔다.
        /// EXEC/EXECUTE x는 "BEGIN x; END;"(x 끝의 ';'와 뒤 주석은 뺌).</summary>
        public string Text { get; set; }

        /// <summary>원문에서 문장이 시작하는 위치(앞 공백·주석 다음, 첫 토큰). <see cref="SqlScript.Parse"/>로 만든 문장은 0.</summary>
        public int Start { get; set; }

        /// <summary>원문에서 문장이 차지하는 길이(끝 구분자 ';' 또는 '/' 줄 포함). 빈 줄·스크립트 끝에서 끝나면 마지막 토큰(주석 포함)까지.</summary>
        public int Length { get; set; }

        public SqlKind Kind { get; set; }

        /// <summary>주석을 건너뛴 첫 키워드(대문자). 예: SELECT, UPDATE, CREATE, EXEC. 여는 괄호로 시작하면 괄호 안 첫 키워드. 키워드로 시작하지 않으면 "".</summary>
        public string Verb { get; set; }

        public SqlTransactionAction TransactionAction { get; set; }

        /// <summary>끝의 ';'를 빼고 실행 텍스트를 만들었으면 true.</summary>
        public bool StrippedSemicolon { get; set; }

        /// <summary>문자열·주석 밖, 괄호 깊이 0에서 FOR UPDATE가 있는 SELECT. 잠금을 잡으므로 트랜잭션 안에서 실행해야 한다.</summary>
        public bool ForUpdate { get; set; }

        /// <summary>'/' 줄(또는 스크립트 끝)로 끝나는 PL/SQL 블록(DECLARE·BEGIN, CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]
        /// PROCEDURE·FUNCTION·PACKAGE [BODY]·TRIGGER·TYPE [BODY]·LIBRARY·JAVA).</summary>
        public bool IsPlSqlBlock { get; set; }

        /// <summary>실행 전에 확인을 받아야 하는 이유(사람이 읽을 한국어 문장). 없으면 null.
        /// WHERE 없는 UPDATE·DELETE(괄호 깊이 0 기준), DROP·TRUNCATE.</summary>
        public string Danger { get; set; }
    }

    /// <summary>
    /// SQL 편집기 텍스트를 문장으로 나누고 종류를 판정한다(순수 로직, 테스트 대상).
    /// 구분 규칙(SQL Developer와 비슷하게):
    /// - 문자열('…', '' 이스케이프), Oracle 대체 따옴표(q'[…]', q'{…}', q'(…)', q'&lt;…&gt;', 그 밖 같은 문자 짝), 따옴표 식별자("…"),
    ///   줄 주석(--), 블록 주석(/* */) 안의 구분자는 무시한다. 닫히지 않은 문자열·주석은 끝까지 이어진 것으로 본다(예외 없음).
    /// - PL/SQL 블록이 아닌 문장은 ';', 공백만 있는 빈 줄, 또는 '/'만 있는 줄에서 끝난다.
    /// - PL/SQL 블록은 '/'만 있는 줄 또는 스크립트 끝에서만 끝난다(안의 ';'·빈 줄로는 안 끝남).
    /// - 주석·공백만 있는 조각은 문장으로 내지 않는다.
    /// </summary>
    public static class SqlScript
    {
        private const string UpdateWithoutWhere = "WHERE 절이 없습니다. 테이블의 모든 행이 바뀝니다.";
        private const string DeleteWithoutWhere = "WHERE 절이 없습니다. 테이블의 모든 행이 삭제됩니다.";
        private const string Irreversible = "되돌릴 수 없는 문장입니다. DDL은 바로 커밋되어 롤백할 수 없습니다.";

        /// <summary>스크립트 전체를 문장 목록으로. 순서는 원문 순서.</summary>
        public static List<SqlStatement> Split(string script)
        {
            var result = new List<SqlStatement>();
            if (string.IsNullOrEmpty(script))
                return result;
            var tokens = Tokenize(script);
            var i = SkipSeparators(script, tokens, 0);
            while (i < tokens.Count)
            {
                var block = IsBlockHeader(script, tokens, i);
                var end = i;
                while (end < tokens.Count && !EndsStatement(script, tokens[end], block))
                    end++;
                // ';'와 '/' 줄은 문장 범위에 넣는다. 빈 줄·스크립트 끝에서 끝나면 마지막 토큰까지.
                var terminatorEnd = -1;
                var semicolon = false;
                if (end < tokens.Count && tokens[end].Type != TokenType.BlankLine)
                {
                    terminatorEnd = tokens[end].End;
                    semicolon = tokens[end].Type == TokenType.Symbol;
                }
                result.Add(Build(script, tokens, i, end, terminatorEnd, block, semicolon));
                i = SkipSeparators(script, tokens, end + 1);
            }
            return result;
        }

        /// <summary>
        /// 커서 위치의 문장. 커서가 어떤 문장 범위(Start ≤ caret ≤ Start+Length) 안이면 그 문장,
        /// 문장 사이(공백·주석만 있는 곳)면 커서 뒤 가장 가까운 문장, 뒤에 없으면 앞 문장. 문장이 없으면 null.
        /// </summary>
        public static SqlStatement AtCaret(string script, int caret)
        {
            var statements = Split(script);
            if (statements.Count == 0)
                return null;
            foreach (var statement in statements)
            {
                if (statement.Start <= caret && caret <= statement.Start + statement.Length)
                    return statement;
            }
            foreach (var statement in statements)
            {
                if (statement.Start > caret)
                    return statement;
            }
            return statements[statements.Count - 1];
        }

        /// <summary>
        /// 선택 영역처럼 이미 문장 하나로 정해진 텍스트를 분석한다. 주석·공백(과 ';'·'/' 줄 같은 빈 구분자)뿐이면 null.
        /// 가운데의 ';'·빈 줄로는 나누지 않고 끝의 '/' 줄과(PL/SQL 블록이 아니면) 끝의 ';' 하나만 뗀다.
        /// Length는 텍스트 처음부터 문장 끝(뗀 구분자 포함)까지.
        /// </summary>
        public static SqlStatement Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var tokens = Tokenize(text);
            var first = SkipSeparators(text, tokens, 0);
            if (first >= tokens.Count)
                return null;
            var block = IsBlockHeader(text, tokens, first);
            var limit = tokens.Count;
            var terminatorEnd = -1;
            var last = LastSignificant(tokens, first, limit);
            if (tokens[last].Type == TokenType.SlashLine)
            {
                terminatorEnd = tokens[last].End;
                limit = last;
                last = LastSignificant(tokens, first, limit);
            }
            var semicolon = !block && IsSymbol(text, tokens[last], ';');
            if (semicolon)
            {
                if (terminatorEnd < 0)
                    terminatorEnd = tokens[last].End;
                limit = last;
            }
            var statement = Build(text, tokens, first, limit, terminatorEnd, block, semicolon);
            statement.Length += statement.Start;
            statement.Start = 0;
            return statement;
        }

        /// <summary>LIKE 패턴용 이스케이프: '\' → '\\', '%' → '\%', '_' → '\_'. SQL에는 ESCAPE '\'를 함께 쓴다.</summary>
        public static string EscapeLike(string term)
        {
            if (string.IsNullOrEmpty(term))
                return "";
            var sb = new StringBuilder(term.Length + 8);
            foreach (var c in term)
            {
                if (c == '\\' || c == '%' || c == '_')
                    sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>"포함" 검색 패턴: "%" + EscapeLike(앞뒤 공백 뺀 term의 대문자) + "%".</summary>
        public static string ContainsPattern(string term)
        {
            return "%" + EscapeLike((term ?? "").Trim().ToUpperInvariant()) + "%";
        }

        // ---- 문장 만들기 ----

        // tokens[first, limit)가 문장 내용(끝 구분자 제외). terminatorEnd는 문장 범위에 넣을 구분자의 끝(없으면 -1).
        private static SqlStatement Build(string s, List<Token> tokens, int first, int limit, int terminatorEnd, bool block, bool strippedSemicolon)
        {
            var significant = new List<Token>();
            var contentEnd = tokens[first].End;
            for (var i = first; i < limit; i++)
            {
                var t = tokens[i];
                if (t.IsMarker)
                    continue;
                contentEnd = t.End;
                if (!t.IsComment)
                    significant.Add(t);
            }
            var start = tokens[first].Start;
            var statement = new SqlStatement
            {
                Start = start,
                Length = (terminatorEnd >= 0 ? terminatorEnd : contentEnd) - start,
                // 닫히지 않은 문자열·주석이 스크립트 끝까지 이어지면 끝 공백이 들어 있다
                Text = s.Substring(start, contentEnd - start).TrimEnd(),
                IsPlSqlBlock = block,
                StrippedSemicolon = strippedSemicolon
            };
            Analyze(statement, s, significant);
            return statement;
        }

        // sig: 문장 내용에서 주석을 뺀 토큰
        private static void Analyze(SqlStatement statement, string s, List<Token> sig)
        {
            var v = 0;
            while (v < sig.Count && IsSymbol(s, sig[v], '('))
                v++;
            var verb = WordText(s, sig, v) ?? "";
            var next = WordText(s, sig, v + 1);
            statement.Verb = verb;
            statement.Kind = KindOf(verb, next);
            statement.TransactionAction = TransactionActionOf(verb, next, s, sig, v);
            statement.ForUpdate = statement.Kind == SqlKind.Query && HasTopLevel(s, sig, "FOR", "UPDATE");
            statement.Danger = DangerOf(verb, s, sig);
            if (v == 0 && (verb == "EXEC" || verb == "EXECUTE"))
                statement.Text = ExecAsBlock(s, sig, statement.Text);
        }

        private static SqlKind KindOf(string verb, string next)
        {
            switch (verb)
            {
                case "SELECT":
                case "WITH":
                    return SqlKind.Query;
                case "INSERT":
                case "UPDATE":
                case "DELETE":
                case "MERGE":
                    return SqlKind.Dml;
                case "ALTER":
                    return next == "SESSION" ? SqlKind.Other : SqlKind.Ddl;
                case "CREATE":
                case "DROP":
                case "TRUNCATE":
                case "RENAME":
                case "GRANT":
                case "REVOKE":
                case "COMMENT":
                case "PURGE":
                case "ANALYZE":
                case "AUDIT":
                case "NOAUDIT":
                case "FLASHBACK":
                    return SqlKind.Ddl;
                case "BEGIN":
                case "DECLARE":
                case "CALL":
                case "EXEC":
                case "EXECUTE":
                    return SqlKind.PlSql;
                case "COMMIT":
                case "ROLLBACK":
                case "SAVEPOINT":
                    return SqlKind.Transaction;
                case "SET":
                    return next == "TRANSACTION" ? SqlKind.Transaction : SqlKind.Other;
                default:
                    return SqlKind.Other;
            }
        }

        private static SqlTransactionAction TransactionActionOf(string verb, string next, string s, List<Token> sig, int v)
        {
            if (verb == "COMMIT" || verb == "ROLLBACK")
            {
                var k = v + 1;
                if (k < sig.Count && IsWord(s, sig[k], "WORK"))
                    k++;
                if (k >= sig.Count)
                    return verb == "COMMIT" ? SqlTransactionAction.Commit : SqlTransactionAction.Rollback;
                // COMMENT·WRITE는 커밋 방식 옵션일 뿐이다. ROLLBACK TO [SAVEPOINT]와 FORCE(다른 분산 트랜잭션 정리)는
                // 현재 트랜잭션을 끝내지 않으므로 SQL로 보낸다.
                if (verb == "COMMIT" && (IsWord(s, sig[k], "COMMENT") || IsWord(s, sig[k], "WRITE")))
                    return SqlTransactionAction.Commit;
                return SqlTransactionAction.Other;
            }
            if (verb == "SAVEPOINT" || (verb == "SET" && next == "TRANSACTION"))
                return SqlTransactionAction.Other;
            return SqlTransactionAction.None;
        }

        private static string DangerOf(string verb, string s, List<Token> sig)
        {
            switch (verb)
            {
                case "UPDATE":
                    return HasTopLevel(s, sig, "WHERE", null) ? null : UpdateWithoutWhere;
                case "DELETE":
                    return HasTopLevel(s, sig, "WHERE", null) ? null : DeleteWithoutWhere;
                case "DROP":
                case "TRUNCATE":
                    return Irreversible;
                default:
                    return null;
            }
        }

        // 괄호 깊이 0에서 keyword(then이 있으면 바로 뒤에 then)가 나오는지. 부질의 안의 WHERE·FOR UPDATE는 세지 않는다.
        private static bool HasTopLevel(string s, List<Token> sig, string keyword, string then)
        {
            var depth = 0;
            for (var i = 0; i < sig.Count; i++)
            {
                var t = sig[i];
                if (IsSymbol(s, t, '('))
                    depth++;
                else if (IsSymbol(s, t, ')'))
                    depth--;
                else if (depth == 0 && IsWord(s, t, keyword) && (then == null || (i + 1 < sig.Count && IsWord(s, sig[i + 1], then))))
                    return true;
            }
            return false;
        }

        // EXEC x → BEGIN x; END;  x 끝의 ';'와 뒤 주석은 뺀다(끝 줄 주석이 "; END;"까지 주석으로 만들지 않게).
        private static string ExecAsBlock(string s, List<Token> sig, string text)
        {
            var last = sig.Count - 1;
            while (last > 0 && IsSymbol(s, sig[last], ';'))
                last--;
            if (last < 1)
                return text;  // EXEC만 있으면 그대로 보내 DB 오류로 알린다
            return "BEGIN " + s.Substring(sig[1].Start, sig[last].End - sig[1].Start) + "; END;";
        }

        // ---- 문장 경계 ----

        // 첫 키워드(주석·빈 줄은 건너뜀)로 '/' 줄까지 이어지는 PL/SQL 블록인지 판단한다.
        private static bool IsBlockHeader(string s, List<Token> tokens, int first)
        {
            var words = new List<string>();
            for (var i = first; i < tokens.Count && words.Count < 7; i++)
            {
                var t = tokens[i];
                if (t.IsComment || t.Type == TokenType.BlankLine)
                    continue;
                if (t.Type != TokenType.Word)
                    break;
                words.Add(Upper(s, t));
            }
            var w0 = WordAt(words, 0);
            if (w0 == "DECLARE" || w0 == "BEGIN")
                return true;
            if (w0 != "CREATE")
                return false;
            var k = 1;
            if (WordAt(words, k) == "OR" && WordAt(words, k + 1) == "REPLACE")
                k += 2;
            if (WordAt(words, k) == "EDITIONABLE" || WordAt(words, k) == "NONEDITIONABLE")
                k++;
            // CREATE [OR REPLACE] [AND {RESOLVE|COMPILE}] [NOFORCE] JAVA …
            if (WordAt(words, k) == "AND" && (WordAt(words, k + 1) == "RESOLVE" || WordAt(words, k + 1) == "COMPILE"))
                k += 2;
            if (WordAt(words, k) == "NOFORCE")
                k++;
            switch (WordAt(words, k))
            {
                case "PROCEDURE":
                case "FUNCTION":
                case "PACKAGE":
                case "TRIGGER":
                // TYPE은 BODY가 없으면 SQL 형식 명세일 수도 있지만 SQL*Plus처럼 '/' 줄까지 한 문장으로 본다
                case "TYPE":
                case "LIBRARY":
                case "JAVA":
                    return true;
                default:
                    return false;
            }
        }

        private static bool EndsStatement(string s, Token t, bool block)
        {
            if (t.Type == TokenType.SlashLine)
                return true;
            return !block && (t.Type == TokenType.BlankLine || IsSymbol(s, t, ';'));
        }

        // 문장 앞의 주석·빈 줄과 빈 조각의 구분자(';', '/' 줄)를 건너뛴다
        private static int SkipSeparators(string s, List<Token> tokens, int i)
        {
            while (i < tokens.Count && (tokens[i].IsComment || tokens[i].IsMarker || IsSymbol(s, tokens[i], ';')))
                i++;
            return i;
        }

        // [first, limit)에서 주석·빈 줄이 아닌 마지막 토큰. tokens[first]는 그런 토큰이어야 한다.
        private static int LastSignificant(List<Token> tokens, int first, int limit)
        {
            var i = limit - 1;
            while (i > first && (tokens[i].IsComment || tokens[i].Type == TokenType.BlankLine))
                i--;
            return i;
        }

        private static string WordAt(List<string> words, int index)
        {
            return index < words.Count ? words[index] : null;
        }

        private static string WordText(string s, List<Token> sig, int index)
        {
            return index < sig.Count && sig[index].Type == TokenType.Word ? Upper(s, sig[index]) : null;
        }

        private static bool IsSymbol(string s, Token t, char c)
        {
            return t.Type == TokenType.Symbol && s[t.Start] == c;
        }

        private static bool IsWord(string s, Token t, string keyword)
        {
            return t.Type == TokenType.Word && t.End - t.Start == keyword.Length
                && string.Compare(s, t.Start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string Upper(string s, Token t)
        {
            return s.Substring(t.Start, t.End - t.Start).ToUpperInvariant();
        }

        // ---- 토큰 나누기 ----

        private enum TokenType
        {
            /// <summary>키워드·식별자(글자로 시작, 글자·숫자·_·$·#)</summary>
            Word,
            Number,
            /// <summary>'…', N'…', q'[…]', nq'[…]'</summary>
            Literal,
            QuotedIdentifier,
            LineComment,
            BlockComment,
            /// <summary>그 밖의 한 글자: ( ) ; , / 등</summary>
            Symbol,
            /// <summary>문자열·주석 밖의 공백만 있는 줄(길이 0 표시)</summary>
            BlankLine,
            /// <summary>문자열·주석 밖의 '/'만 있는 줄의 '/'</summary>
            SlashLine
        }

        private readonly struct Token
        {
            public Token(TokenType type, int start, int end)
            {
                Type = type;
                Start = start;
                End = end;
            }

            public TokenType Type { get; }
            public int Start { get; }
            public int End { get; }
            public bool IsComment { get { return Type == TokenType.LineComment || Type == TokenType.BlockComment; } }
            public bool IsMarker { get { return Type == TokenType.BlankLine || Type == TokenType.SlashLine; } }
        }

        /// <summary>
        /// 한 번 훑어 토큰으로 나눈다. 공백은 버리고, 문자열·주석 밖의 빈 줄과 '/'만 있는 줄은 표시 토큰으로 남긴다.
        /// 줄바꿈은 CRLF·LF·CR. 닫히지 않은 문자열·주석·따옴표 식별자는 끝까지로 본다.
        /// </summary>
        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var n = s.Length;
            var pos = 0;
            // 지금 줄에 공백 말고 무엇이 있었는지. 여러 줄 문자열·주석이 끝난 줄도 내용이 있는 줄이다.
            var lineHasContent = false;
            while (pos < n)
            {
                var c = s[pos];
                if (c == '\n' || c == '\r')
                {
                    if (!lineHasContent)
                        tokens.Add(new Token(TokenType.BlankLine, pos, pos));
                    pos += c == '\r' && pos + 1 < n && s[pos + 1] == '\n' ? 2 : 1;
                    lineHasContent = false;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                    continue;
                }

                var start = pos;
                var next = pos + 1 < n ? s[pos + 1] : '\0';
                TokenType type;
                if (c == '-' && next == '-')
                {
                    pos = LineEnd(s, pos);
                    // 줄 끝 공백은 토큰에 넣지 않는다(다음 바퀴에서 공백으로 건너뜀)
                    while (pos > start + 2 && char.IsWhiteSpace(s[pos - 1]))
                        pos--;
                    type = TokenType.LineComment;
                }
                else if (c == '/' && next == '*')
                {
                    var close = s.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    pos = close < 0 ? n : close + 2;
                    type = TokenType.BlockComment;
                }
                else if (c == '/' && !lineHasContent && IsBlankUntilLineEnd(s, pos + 1))
                {
                    pos++;
                    type = TokenType.SlashLine;
                }
                else if (c == '\'')
                {
                    pos = StringEnd(s, pos + 1);
                    type = TokenType.Literal;
                }
                else if (c == '"')
                {
                    var close = s.IndexOf('"', pos + 1);
                    pos = close < 0 ? n : close + 1;
                    type = TokenType.QuotedIdentifier;
                }
                else if (char.IsLetter(c))
                {
                    pos++;
                    while (pos < n && IsWordPart(s[pos]))
                        pos++;
                    type = TokenType.Word;
                    // 접두어가 낱말 전체일 때만 접두 문자열이다: q'[…]', nq'[…]', N'…' (xq'…'는 xq 다음 보통 문자열)
                    if (pos < n && s[pos] == '\'')
                    {
                        var length = pos - start;
                        if (IsQPrefix(s, start, length) && pos + 1 < n && !char.IsWhiteSpace(s[pos + 1]))
                        {
                            pos = QuoteEnd(s, pos + 1);
                            type = TokenType.Literal;
                        }
                        else if (length == 1 && (c == 'n' || c == 'N'))
                        {
                            pos = StringEnd(s, pos + 1);
                            type = TokenType.Literal;
                        }
                    }
                }
                else if (IsDigit(c) || (c == '.' && IsDigit(next)))
                {
                    pos = NumberEnd(s, pos);
                    type = TokenType.Number;
                }
                else
                {
                    pos++;
                    type = TokenType.Symbol;
                }
                tokens.Add(new Token(type, start, pos));
                lineHasContent = true;
            }
            return tokens;
        }

        private static int LineEnd(string s, int pos)
        {
            while (pos < s.Length && s[pos] != '\n' && s[pos] != '\r')
                pos++;
            return pos;
        }

        private static bool IsBlankUntilLineEnd(string s, int pos)
        {
            for (; pos < s.Length && s[pos] != '\n' && s[pos] != '\r'; pos++)
            {
                if (!char.IsWhiteSpace(s[pos]))
                    return false;
            }
            return true;
        }

        // pos: 여는 따옴표 다음. '' 는 따옴표 한 글자다.
        private static int StringEnd(string s, int pos)
        {
            while (pos < s.Length)
            {
                if (s[pos] == '\'')
                {
                    if (pos + 1 < s.Length && s[pos + 1] == '\'')
                    {
                        pos += 2;
                        continue;
                    }
                    return pos + 1;
                }
                pos++;
            }
            return s.Length;
        }

        // pos: q' 다음의 여는 구분자. [ { ( < 는 짝 문자로, 그 밖은 같은 문자로 닫고 바로 뒤에 '가 와야 끝난다.
        private static int QuoteEnd(string s, int pos)
        {
            var open = s[pos];
            var close = open == '[' ? ']' : open == '{' ? '}' : open == '(' ? ')' : open == '<' ? '>' : open;
            for (var i = pos + 1; i + 1 < s.Length; i++)
            {
                if (s[i] == close && s[i + 1] == '\'')
                    return i + 2;
            }
            return s.Length;
        }

        // Oracle 숫자: 123, 1.5, .5, 1e-3, 2f, 3D. 1FROM·1WHERE처럼 붙여 쓴 키워드를 숫자에 삼키지 않는다.
        private static int NumberEnd(string s, int pos)
        {
            var n = s.Length;
            while (pos < n && IsDigit(s[pos]))
                pos++;
            // '..'(PL/SQL 범위 1..10)의 첫 '.'은 소수점이 아니다
            if (pos < n && s[pos] == '.' && !(pos + 1 < n && s[pos + 1] == '.'))
            {
                pos++;
                while (pos < n && IsDigit(s[pos]))
                    pos++;
            }
            if (pos < n && (s[pos] == 'e' || s[pos] == 'E'))
            {
                var j = pos + 1;
                if (j < n && (s[j] == '+' || s[j] == '-'))
                    j++;
                if (j < n && IsDigit(s[j]))
                {
                    pos = j;
                    while (pos < n && IsDigit(s[pos]))
                        pos++;
                }
            }
            if (pos < n && (s[pos] == 'f' || s[pos] == 'F' || s[pos] == 'd' || s[pos] == 'D') && !(pos + 1 < n && IsWordPart(s[pos + 1])))
                pos++;
            return pos;
        }

        private static bool IsQPrefix(string s, int start, int length)
        {
            if (length == 1)
                return s[start] == 'q' || s[start] == 'Q';
            return length == 2 && (s[start] == 'n' || s[start] == 'N') && (s[start + 1] == 'q' || s[start + 1] == 'Q');
        }

        private static bool IsWordPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }
    }
}
