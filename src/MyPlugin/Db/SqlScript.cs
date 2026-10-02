using System;
using System.Collections.Generic;

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
        /// <summary>COMMIT [WORK]</summary>
        Commit,
        /// <summary>ROLLBACK [WORK] (TO SAVEPOINT 아님)</summary>
        Rollback,
        /// <summary>SAVEPOINT, ROLLBACK TO SAVEPOINT, SET TRANSACTION — SQL로 그대로 실행</summary>
        Other
    }

    /// <summary>실행할 문장 하나.</summary>
    public sealed class SqlStatement
    {
        /// <summary>실행할 텍스트. 앞뒤 공백 제거. PL/SQL 블록이 아니면 끝의 ';'를 뺀다(ODP.NET은 ';'가 있으면 ORA-00911).
        /// PL/SQL 블록은 END …; 의 ';'를 유지하고 끝의 '/' 줄은 뺀다. 문장 안의 주석은 그대로 둔다.</summary>
        public string Text { get; set; }

        /// <summary>원문에서 문장이 시작하는 위치(앞 공백 다음). <see cref="SqlScript.Parse"/>로 만든 문장은 0.</summary>
        public int Start { get; set; }

        /// <summary>원문에서 문장이 차지하는 길이(끝 구분자 ';' 또는 '/' 줄 포함).</summary>
        public int Length { get; set; }

        public SqlKind Kind { get; set; }

        /// <summary>주석을 건너뛴 첫 키워드(대문자). 예: SELECT, UPDATE, CREATE, EXEC. 여는 괄호로 시작하면 괄호 안 첫 키워드.</summary>
        public string Verb { get; set; }

        public SqlTransactionAction TransactionAction { get; set; }

        /// <summary>끝의 ';'를 빼고 실행 텍스트를 만들었으면 true.</summary>
        public bool StrippedSemicolon { get; set; }

        /// <summary>문자열·주석 밖, 괄호 깊이 0에서 FOR UPDATE가 있는 SELECT. 잠금을 잡으므로 트랜잭션 안에서 실행해야 한다.</summary>
        public bool ForUpdate { get; set; }

        /// <summary>'/' 줄로 끝나는 PL/SQL 블록(DECLARE·BEGIN, CREATE [OR REPLACE] PROCEDURE·FUNCTION·PACKAGE [BODY]·TRIGGER·TYPE [BODY]).</summary>
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
        /// <summary>스크립트 전체를 문장 목록으로. 순서는 원문 순서.</summary>
        public static List<SqlStatement> Split(string script)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 커서 위치의 문장. 커서가 어떤 문장 범위(Start ≤ caret ≤ Start+Length) 안이면 그 문장,
        /// 문장 사이(공백·주석만 있는 곳)면 커서 뒤 가장 가까운 문장, 뒤에 없으면 앞 문장. 문장이 없으면 null.
        /// </summary>
        public static SqlStatement AtCaret(string script, int caret)
        {
            throw new NotImplementedException();
        }

        /// <summary>선택 영역처럼 이미 문장 하나로 정해진 텍스트를 분석한다. 주석·공백뿐이면 null.</summary>
        public static SqlStatement Parse(string text)
        {
            throw new NotImplementedException();
        }

        /// <summary>LIKE 패턴용 이스케이프: '\' → '\\', '%' → '\%', '_' → '\_'. SQL에는 ESCAPE '\'를 함께 쓴다.</summary>
        public static string EscapeLike(string term)
        {
            throw new NotImplementedException();
        }

        /// <summary>"포함" 검색 패턴: "%" + EscapeLike(앞뒤 공백 뺀 term의 대문자) + "%".</summary>
        public static string ContainsPattern(string term)
        {
            throw new NotImplementedException();
        }
    }
}
