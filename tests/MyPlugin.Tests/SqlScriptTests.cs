using System;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class SqlScriptTests
    {
        private const string UpdateDanger = "WHERE 절이 없습니다. 테이블의 모든 행이 바뀝니다.";
        private const string DeleteDanger = "WHERE 절이 없습니다. 테이블의 모든 행이 삭제됩니다.";
        private const string DdlDanger = "되돌릴 수 없는 문장입니다. DDL은 바로 커밋되어 롤백할 수 없습니다.";

        // ';', '/' 줄, 빈 줄, PL/SQL 블록, 앞뒤 주석이 섞인 스크립트
        private const string MixedScript = "-- 머리 주석\n"
            + "SELECT 1 FROM dual;\n"
            + "/* 블록 주석 */ UPDATE t SET a = 1\n"
            + "/\n"
            + "BEGIN\n"
            + "  NULL;\n"
            + "END;\n"
            + "  /  \n"
            + "\n"
            + "DELETE FROM t WHERE id = 1 -- 끝\n"
            + "\n"
            + "EXEC p(1);\n"
            + "SELECT 2 FROM dual";

        private static string[] Texts(string script)
        {
            return SqlScript.Split(script).Select(s => s.Text).ToArray();
        }

        // ---- 나누기: 구분자 ----

        [Fact]
        public void Split_Semicolon_SeparatesStatements()
        {
            Assert.Equal(new[] { "SELECT 1 FROM dual", "SELECT 2 FROM dual" }, Texts("SELECT 1 FROM dual; SELECT 2 FROM dual;"));
        }

        [Fact]
        public void Split_BlankLine_SeparatesStatements()
        {
            Assert.Equal(new[] { "SELECT 1 FROM dual", "SELECT 2\nFROM dual" }, Texts("SELECT 1 FROM dual\n\nSELECT 2\nFROM dual"));
        }

        [Fact]
        public void Split_WhitespaceOnlyLine_CountsAsBlankLine()
        {
            Assert.Equal(new[] { "SELECT 1 FROM dual", "SELECT 2 FROM dual" }, Texts("SELECT 1 FROM dual\n \t \nSELECT 2 FROM dual"));
        }

        [Fact]
        public void Split_CommentOnlyLine_IsNotBlankLine()
        {
            Assert.Equal(new[] { "SELECT 1\n-- 주석\nFROM dual" }, Texts("SELECT 1\n-- 주석\nFROM dual"));
        }

        [Fact]
        public void Split_SlashLine_SeparatesStatements_SpacesAroundSlashAllowed()
        {
            var statements = SqlScript.Split("SELECT 1 FROM dual\n/\nSELECT 2 FROM dual\n  /  \n");

            Assert.Equal(new[] { "SELECT 1 FROM dual", "SELECT 2 FROM dual" }, statements.Select(s => s.Text));
            Assert.All(statements, s => Assert.False(s.StrippedSemicolon));
        }

        [Theory]
        [InlineData("SELECT 4\n/ 2 FROM dual")]
        [InlineData("SELECT 4 /\n2 FROM dual")]
        [InlineData("SELECT 4 /* 몫 */ /\n2 FROM dual")]
        public void Split_SlashNotAloneOnLine_IsDivision(string script)
        {
            Assert.Equal(new[] { script }, Texts(script));
        }

        [Fact]
        public void Split_TrailingSpacesAfterLineComment_AreNotInRange()
        {
            const string script = "SELECT 1 -- 주석   \n\nSELECT 2";

            var first = SqlScript.Split(script)[0];

            Assert.Equal("SELECT 1 -- 주석", script.Substring(first.Start, first.Length));
        }

        [Fact]
        public void Split_BlankLineRightAfterMultiLineComment_Splits()
        {
            Assert.Equal(new[] { "SELECT 1 /*\n*/", "SELECT 2" }, Texts("SELECT 1 /*\n*/\n\nSELECT 2"));
        }

        [Fact]
        public void Split_LineWhereMultiLineCommentOrStringEnds_IsNotBlank()
        {
            Assert.Equal(new[] { "SELECT 1 /*\n*/\nFROM dual" }, Texts("SELECT 1 /*\n*/\nFROM dual"));
            Assert.Equal(new[] { "SELECT 'a\n'\nFROM dual" }, Texts("SELECT 'a\n'\nFROM dual"));
        }

        [Fact]
        public void Split_SlashLineAfterSemicolon_DoesNotAddStatement()
        {
            Assert.Equal(new[] { "CREATE TABLE t (a NUMBER)", "SELECT 1 FROM dual" }, Texts("CREATE TABLE t (a NUMBER);\n/\nSELECT 1 FROM dual;\n/\n"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n\t\n")]
        [InlineData("-- 주석만\n/* 블록\n\n주석 */\n")]
        [InlineData(";;\n;\n/\n  /")]
        public void Split_NothingToRun_ReturnsEmpty(string script)
        {
            Assert.Empty(SqlScript.Split(script));
        }

        [Fact]
        public void Split_LeadingCommentsAreNotPartOfStatement()
        {
            var statement = Assert.Single(SqlScript.Split("-- 머리 주석\n/* 설명 */\nSELECT 1 FROM dual;"));

            Assert.Equal("SELECT 1 FROM dual", statement.Text);
            Assert.Equal("SELECT", statement.Verb);
        }

        // ---- 나누기: 문자열·식별자·주석 안의 구분자 ----

        [Fact]
        public void Split_SemicolonAndDashesInsideString_DoNotSplit()
        {
            Assert.Equal(new[] { "SELECT 'a;b -- c', 'it''s;' FROM dual", "SELECT 2 FROM dual" },
                Texts("SELECT 'a;b -- c', 'it''s;' FROM dual; SELECT 2 FROM dual"));
        }

        [Theory]
        [InlineData("q'[a;b]'")]
        [InlineData("q'!x;y!'")]
        [InlineData("q'{a;b}'")]
        [InlineData("q'(a;b)'")]
        [InlineData("q'<a;b>'")]
        [InlineData("Q'[a;b]'")]
        [InlineData("nq'[a;b]'")]
        [InlineData("NQ'[a;b]'")]
        [InlineData("Nq'#a;b#'")]
        [InlineData("N'a;b'")]
        // 안에 ' 가 있으면 보통 문자열로는 거기서 끝나므로 대체 따옴표로 읽어야만 한 문장이 된다
        [InlineData("q'[it's; -- 주석 아님]'")]
        [InlineData("q'!x';y!'")]
        [InlineData("q'{a';b}'")]
        [InlineData("q'(a';b)'")]
        [InlineData("q'<a';b>'")]
        [InlineData("Q'[a';b]'")]
        [InlineData("nq'[a';b]'")]
        [InlineData("NQ'[a';b]'")]
        [InlineData("Nq'#a';b#'")]
        [InlineData("q'[a]b';]'")]
        [InlineData("N'a'';b'")]
        public void Split_SemicolonInsideAlternativeQuoting_DoesNotSplit(string literal)
        {
            Assert.Equal(new[] { "SELECT " + literal + " FROM dual", "SELECT 2 FROM dual" },
                Texts("SELECT " + literal + " FROM dual;\nSELECT 2 FROM dual;"));
        }

        [Fact]
        public void Split_QAtEndOfLongerWord_IsNotAlternativeQuote()
        {
            // xq는 접두어가 아니므로 'a;b'는 보통 문자열이다(대체 따옴표로 보면 끝까지 이어져 한 문장이 된다)
            Assert.Equal(new[] { "SELECT xq'a;b' FROM dual", "SELECT 2 FROM dual" }, Texts("SELECT xq'a;b' FROM dual; SELECT 2 FROM dual"));
        }

        [Fact]
        public void Split_SemicolonInsideQuotedIdentifier_DoesNotSplit()
        {
            Assert.Equal(new[] { "SELECT 1 AS \"quo;ted\" FROM dual", "SELECT 2 FROM dual" },
                Texts("SELECT 1 AS \"quo;ted\" FROM dual; SELECT 2 FROM dual"));
        }

        [Fact]
        public void Split_SemicolonInsideComments_DoesNotSplit_AndCommentsStayInText()
        {
            Assert.Equal(new[] { "SELECT 1 /* a;b */ FROM dual -- c;d", "SELECT 2 FROM dual" },
                Texts("SELECT 1 /* a;b */ FROM dual -- c;d\n; SELECT 2 FROM dual"));
        }

        [Fact]
        public void Split_BlankLinesAndSlashInsideBlockComment_DoNotSplit()
        {
            Assert.Equal(new[] { "SELECT 1 /*\n\n/\n\n*/ FROM dual", "SELECT 2 FROM dual" },
                Texts("SELECT 1 /*\n\n/\n\n*/ FROM dual\n\nSELECT 2 FROM dual"));
        }

        [Fact]
        public void Split_BlankLineAndSlashLineInsideString_DoNotSplit()
        {
            Assert.Equal(new[] { "SELECT 'a\n\n/\nb', q'[\n\n]' FROM dual" }, Texts("SELECT 'a\n\n/\nb', q'[\n\n]' FROM dual"));
        }

        // ---- PL/SQL 블록 ----

        [Fact]
        public void Split_BeginBlock_KeepsInnerSemicolonsAndBlankLines_EndsAtSlash()
        {
            var statements = SqlScript.Split("BEGIN\n  x := 1;\n\n  y := 2;\nEND;\n/\nSELECT 1 FROM dual;");

            Assert.Equal(2, statements.Count);
            var block = statements[0];
            Assert.Equal("BEGIN\n  x := 1;\n\n  y := 2;\nEND;", block.Text);
            Assert.True(block.IsPlSqlBlock);
            Assert.Equal(SqlKind.PlSql, block.Kind);
            Assert.Equal("BEGIN", block.Verb);
            Assert.False(block.StrippedSemicolon);
            Assert.Equal("SELECT 1 FROM dual", statements[1].Text);
            Assert.False(statements[1].IsPlSqlBlock);
        }

        [Fact]
        public void Split_CreateOrReplacePackageBody_EndsAtSlashAfterEndName()
        {
            const string body = "CREATE OR REPLACE PACKAGE BODY pkg AS\n"
                + "  PROCEDURE p IS\n"
                + "  BEGIN\n"
                + "    UPDATE t SET a = 1;\n"
                + "  END p;\n"
                + "\n"
                + "  FUNCTION f RETURN NUMBER IS\n"
                + "  BEGIN\n"
                + "    RETURN 1;\n"
                + "  END f;\n"
                + "END pkg;";

            var statements = SqlScript.Split(body + "\n/\nSELECT pkg.f FROM dual;\n");

            Assert.Equal(2, statements.Count);
            Assert.Equal(body, statements[0].Text);
            Assert.True(statements[0].IsPlSqlBlock);
            Assert.Equal(SqlKind.Ddl, statements[0].Kind);
            Assert.Equal("CREATE", statements[0].Verb);
            Assert.Null(statements[0].Danger);
            Assert.Equal("SELECT pkg.f FROM dual", statements[1].Text);
        }

        [Fact]
        public void Split_DeclareBlock_IsOneStatement()
        {
            const string block = "DECLARE\n  v NUMBER;\nBEGIN\n  SELECT 1 INTO v FROM dual;\n\n  DELETE FROM t;\nEND;";

            var statement = Assert.Single(SqlScript.Split("-- 익명 블록\n" + block + "\n/\n"));

            Assert.Equal(block, statement.Text);
            Assert.True(statement.IsPlSqlBlock);
            Assert.Equal(SqlKind.PlSql, statement.Kind);
            Assert.Equal("DECLARE", statement.Verb);
            Assert.Null(statement.Danger);
        }

        [Fact]
        public void Split_BlockAtEndWithoutSlash_EndsAtEndOfScript()
        {
            var statements = SqlScript.Split("SELECT 1 FROM dual;\nBEGIN\n  NULL;\nEND;\n");

            Assert.Equal(2, statements.Count);
            Assert.Equal("BEGIN\n  NULL;\nEND;", statements[1].Text);
            Assert.True(statements[1].IsPlSqlBlock);
        }

        [Fact]
        public void Split_BlockWithoutSlash_EndsAtBlankLineAfterUnindentedEnd()
        {
            // '/' 줄이 없으면 들여쓰지 않은 END; 줄 뒤의 빈 줄에서 끝난다(아래 문장을 블록에 삼키지 않음)
            var statements = SqlScript.Split("BEGIN NULL; END;\n\nSELECT 1 FROM dual;");

            Assert.Equal(new[] { "BEGIN NULL; END;", "SELECT 1 FROM dual" }, statements.Select(s => s.Text));
            Assert.True(statements[0].IsPlSqlBlock);
            // 익명 블록은 잘못 잘려도 컴파일 오류로 끝나 아무것도 실행되지 않으므로 묻지 않는다
            Assert.Null(statements[0].Danger);
            Assert.Equal(SqlKind.Query, statements[1].Kind);
        }

        [Fact]
        public void AtCaret_SelectAfterCreateWithoutSlash_IsNotPartOfTheProcedure()
        {
            const string procedure = "CREATE OR REPLACE PROCEDURE p AS\nBEGIN\n  NULL;\nEND;";
            const string script = procedure + "\n\nSELECT * FROM emp;";

            var select = SqlScript.AtCaret(script, script.IndexOf("emp", StringComparison.Ordinal));
            var created = SqlScript.AtCaret(script, script.IndexOf("NULL", StringComparison.Ordinal));

            Assert.Equal("SELECT * FROM emp", select.Text);
            Assert.Equal(SqlKind.Query, select.Kind);
            Assert.Null(select.Danger);
            Assert.Equal(procedure, created.Text);
            Assert.Equal(procedure.Length, created.Length);
            Assert.True(created.IsPlSqlBlock);
            // 끝을 짐작한 CREATE는 객체를 INVALID로 바꿀 수 있어 실행 전에 확인받는다
            Assert.NotNull(created.Danger);
            Assert.Contains("'/'", created.Danger);

            // 편집기(WPF TextBox)는 CRLF로 줄을 바꾼다
            var crlf = script.Replace("\n", "\r\n");
            Assert.Equal("SELECT * FROM emp", SqlScript.AtCaret(crlf, crlf.IndexOf("emp", StringComparison.Ordinal)).Text);
            Assert.Equal(procedure.Replace("\n", "\r\n"), SqlScript.AtCaret(crlf, 0).Text);
        }

        [Fact]
        public void Split_PackageBodyWithoutSlash_IndentedEndsDoNotEndTheBlock()
        {
            const string body = "CREATE OR REPLACE PACKAGE BODY pkg AS\n"
                + "  PROCEDURE p IS\n"
                + "  BEGIN\n"
                + "    NULL;\n"
                + "  END p;\n"
                + "\n"
                + "  FUNCTION f RETURN NUMBER IS\n"
                + "  BEGIN\n"
                + "    RETURN 1;\n"
                + "  END f;\n"
                + "END pkg;";

            var whole = Assert.Single(SqlScript.Split(body + "\n"));
            var split = SqlScript.Split(body + "\n\nSELECT pkg.f FROM dual;");

            Assert.Equal(body, whole.Text);
            Assert.Null(whole.Danger);
            Assert.Equal(new[] { body, "SELECT pkg.f FROM dual" }, split.Select(s => s.Text));
        }

        [Fact]
        public void Split_BlocksWithoutSlash_EndIfLoopAndCaseDoNotEndTheBlock()
        {
            const string first = "BEGIN\nIF 1 = 1 THEN\nNULL;\nEND IF;\n\nFOR i IN 1..2 LOOP\nNULL;\nEND LOOP;\n\nNULL;\nEND;";
            const string second = "DECLARE\n  x NUMBER;\nBEGIN\n  x := 1;\nEND; -- 끝";

            var statements = SqlScript.Split(first + "\n\n" + second + "\n\nCOMMIT;");

            Assert.Equal(new[] { first, second, "COMMIT" }, statements.Select(s => s.Text));
            Assert.All(statements.Take(2), s => Assert.True(s.IsPlSqlBlock));
        }

        [Theory]
        [InlineData("CREATE OR REPLACE PACKAGE BODY pkg AS\nPROCEDURE p1 IS\nBEGIN\n  NULL;\nEND p1;\n\ng_last DATE;\n\n"
            + "FUNCTION f RETURN NUMBER IS\nBEGIN\n  RETURN 1;\nEND f;\n\nBEGIN\n  g_last := SYSDATE;\nEND pkg;")]
        [InlineData("CREATE OR REPLACE PACKAGE BODY pkg AS\nPROCEDURE p1 IS\nBEGIN\n  NULL;\nEND;\n\nPROCEDURE p2 IS\nBEGIN\n  NULL;\nEND;\n\nEND;")]
        [InlineData("CREATE OR REPLACE TYPE BODY t_obj AS\nMEMBER FUNCTION f RETURN NUMBER IS\nBEGIN\n  RETURN 1;\nEND;\n\n"
            + "STATIC FUNCTION g RETURN NUMBER IS\nBEGIN\n  RETURN 2;\nEND g;\n\nEND;")]
        [InlineData("CREATE OR REPLACE PROCEDURE outer_p IS\nPROCEDURE inner_p IS\nBEGIN\n  NULL;\nEND inner_p;\n\nBEGIN\n  inner_p;\nEND outer_p;")]
        [InlineData("DECLARE\nPROCEDURE log(m VARCHAR2) IS\nBEGIN\n  DBMS_OUTPUT.PUT_LINE(m);\nEND log;\n\nBEGIN\n  log('x');\nEND;")]
        public void Split_UnindentedInnerEndsWithoutSlash_DoNotEndTheBlock(string block)
        {
            // 들여쓰지 않은 서브프로그램의 "END p1;"·"END;" 뒤 빈 줄은 블록의 끝이 아니다
            // (잘라서 실행하면 CREATE는 본문만 남아 INVALID가 되고, 익명 블록은 컴파일 오류)
            foreach (var nl in new[] { "\n", "\r\n" })
            {
                var text = block.Replace("\n", nl);

                var whole = Assert.Single(SqlScript.Split(text + nl));
                Assert.Equal(text, whole.Text);
                Assert.Null(whole.Danger);
                Assert.Equal(new[] { text, "SELECT 1 FROM dual" }, Texts(text + nl + nl + "SELECT 1 FROM dual;"));
            }
        }

        [Fact]
        public void Split_BlockWithSlashLater_StillEndsOnlyAtSlash()
        {
            // '/' 줄이 있으면 그 줄이 끝이다(빈 줄 짐작은 '/' 줄이 없을 때만)
            var statements = SqlScript.Split("BEGIN\n  NULL;\nEND;\n\nNULL;\n/\nSELECT 1 FROM dual;");

            Assert.Equal(new[] { "BEGIN\n  NULL;\nEND;\n\nNULL;", "SELECT 1 FROM dual" }, statements.Select(s => s.Text));
            Assert.Null(statements[0].Danger);
        }

        [Fact]
        public void Split_TwoBlocksSeparatedBySlash()
        {
            Assert.Equal(new[] { "BEGIN a; END;", "DECLARE x NUMBER; BEGIN b; END;" },
                Texts("BEGIN a; END;\n/\nDECLARE x NUMBER; BEGIN b; END;\n/\n"));
        }

        [Theory]
        [InlineData("CREATE PROCEDURE p IS BEGIN NULL; END;")]
        [InlineData("create or replace function f return number is begin return 1; end;")]
        [InlineData("CREATE OR REPLACE PACKAGE pkg AS PROCEDURE p; END;")]
        [InlineData("CREATE OR REPLACE EDITIONABLE PACKAGE BODY pkg AS PROCEDURE p IS BEGIN NULL; END; END;")]
        [InlineData("CREATE NONEDITIONABLE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW BEGIN :NEW.a := 1; END;")]
        [InlineData("CREATE OR REPLACE TYPE t_obj AS OBJECT (a NUMBER, b VARCHAR2(10));")]
        [InlineData("CREATE OR REPLACE TYPE BODY t_obj AS MEMBER FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; END;")]
        [InlineData("CREATE OR REPLACE LIBRARY lib AS '/usr/lib/x.so';")]
        [InlineData("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"Hi\" AS public class Hi { static String hi() { return \"hi\"; } };")]
        [InlineData("CREATE /* 주석 */ OR REPLACE -- 줄 주석\nPROCEDURE p IS BEGIN NULL; END;")]
        [InlineData("CREATE OR REPLACE\n\nPROCEDURE p IS BEGIN NULL; END;")]
        public void Split_CreatePlSqlUnit_IsBlockEndingAtSlash(string unit)
        {
            var statements = SqlScript.Split(unit + "\n/\nSELECT 1 FROM dual;");

            Assert.Equal(2, statements.Count);
            Assert.Equal(unit, statements[0].Text);
            Assert.True(statements[0].IsPlSqlBlock);
            Assert.Equal(SqlKind.Ddl, statements[0].Kind);
            Assert.Equal("CREATE", statements[0].Verb);
            Assert.False(statements[0].StrippedSemicolon);
        }

        [Theory]
        [InlineData("CREATE TABLE t (a NUMBER)")]
        [InlineData("CREATE OR REPLACE VIEW v AS SELECT 1 a FROM dual")]
        [InlineData("CREATE OR REPLACE EDITIONABLE VIEW v AS SELECT 1 a FROM dual")]
        [InlineData("CREATE OR REPLACE FORCE EDITIONING VIEW v AS SELECT 1 a FROM dual")]
        [InlineData("CREATE UNIQUE INDEX i ON t (a)")]
        [InlineData("CREATE OR REPLACE PUBLIC SYNONYM s FOR t")]
        [InlineData("CREATE SEQUENCE s")]
        public void Split_CreateOtherObject_EndsAtSemicolon(string ddl)
        {
            var statements = SqlScript.Split(ddl + ";\nSELECT 1 FROM dual;");

            Assert.Equal(2, statements.Count);
            Assert.Equal(ddl, statements[0].Text);
            Assert.False(statements[0].IsPlSqlBlock);
            Assert.True(statements[0].StrippedSemicolon);
        }

        // ---- 실행 텍스트 ----

        [Fact]
        public void Split_TrailingSemicolon_IsStrippedAndReported()
        {
            var with = Assert.Single(SqlScript.Split("SELECT 1 FROM dual;"));
            Assert.Equal("SELECT 1 FROM dual", with.Text);
            Assert.True(with.StrippedSemicolon);

            var without = Assert.Single(SqlScript.Split("SELECT 1 FROM dual"));
            Assert.Equal("SELECT 1 FROM dual", without.Text);
            Assert.False(without.StrippedSemicolon);
        }

        [Fact]
        public void Split_PlSqlBlock_KeepsEndSemicolon()
        {
            var statement = Assert.Single(SqlScript.Split("BEGIN\n  NULL;\nEND;\n/"));

            Assert.EndsWith("END;", statement.Text);
            Assert.False(statement.StrippedSemicolon);
        }

        [Fact]
        public void Exec_WithArguments_BecomesAnonymousBlock()
        {
            var statements = SqlScript.Split("EXEC pkg.p(1, 'a;b');\nEXECUTE p2;\nexec p3(:x) -- 끝 주석");

            Assert.Equal(new[] { "BEGIN pkg.p(1, 'a;b'); END;", "BEGIN p2; END;", "BEGIN p3(:x); END;" }, statements.Select(s => s.Text));
            Assert.Equal(new[] { "EXEC", "EXECUTE", "EXEC" }, statements.Select(s => s.Verb));
            Assert.All(statements, s => Assert.Equal(SqlKind.PlSql, s.Kind));
            Assert.All(statements, s => Assert.False(s.IsPlSqlBlock));
            Assert.True(statements[0].StrippedSemicolon);
            Assert.False(statements[2].StrippedSemicolon);
        }

        [Fact]
        public void Exec_ParseWithTrailingSemicolonAndComment_StripsBoth()
        {
            Assert.Equal("BEGIN p(1); END;", SqlScript.Parse("-- 호출\nEXEC p(1); -- 끝\n").Text);
        }

        [Fact]
        public void Exec_LineCommentInsideArguments_IsKept()
        {
            Assert.Equal("BEGIN p(1, -- 첫째\n  2); END;", SqlScript.Parse("EXEC p(1, -- 첫째\n  2);").Text);
        }

        [Fact]
        public void Exec_SqlPlusLineContinuation_JoinsLinesInsteadOfMinus()
        {
            // SQL*Plus의 줄 이음표 '-'를 남기면 p(1, -2)로 바뀐다
            Assert.Equal("BEGIN p(1,  2); END;", SqlScript.Parse("EXEC p(1, -\n2);").Text);
            Assert.Equal("BEGIN pkg.run('a-b',  :x,  3); END;", SqlScript.Split("EXEC pkg.run('a-b', -  \r\n:x, -\n3);\nSELECT 1 FROM dual;")[0].Text);
            // 줄 가운데의 빼기와 주석이 뒤에 붙은 '-'는 그대로
            Assert.Equal("BEGIN p(4 - 1); END;", SqlScript.Parse("EXEC p(4 - 1);").Text);
            Assert.Equal("BEGIN p(1, - -- 빼기\n2); END;", SqlScript.Parse("EXEC p(1, - -- 빼기\n2);").Text);
        }

        [Fact]
        public void Exec_WithoutArguments_IsLeftAsIs()
        {
            var statement = SqlScript.Parse("EXEC;");

            Assert.Equal("EXEC", statement.Text);
            Assert.Equal(SqlKind.PlSql, statement.Kind);
        }

        // ---- 종류 판정 ----

        [Theory]
        [InlineData("SELECT 1 FROM dual", SqlKind.Query, "SELECT")]
        [InlineData("select 1 from dual", SqlKind.Query, "SELECT")]
        [InlineData("WITH x AS (SELECT 1 a FROM dual) SELECT a FROM x", SqlKind.Query, "WITH")]
        [InlineData("(SELECT 1 FROM dual)", SqlKind.Query, "SELECT")]
        [InlineData("( /* 주석 */ (SELECT 1 FROM dual) UNION (SELECT 2 FROM dual))", SqlKind.Query, "SELECT")]
        [InlineData("SELECT 이름 FROM 사원", SqlKind.Query, "SELECT")]
        [InlineData("INSERT INTO t VALUES (1)", SqlKind.Dml, "INSERT")]
        [InlineData("UPDATE t SET a = 1 WHERE b = 2", SqlKind.Dml, "UPDATE")]
        [InlineData("DELETE FROM t WHERE b = 2", SqlKind.Dml, "DELETE")]
        [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = s.a", SqlKind.Dml, "MERGE")]
        [InlineData("CREATE TABLE t (a NUMBER)", SqlKind.Ddl, "CREATE")]
        [InlineData("ALTER TABLE t ADD b NUMBER", SqlKind.Ddl, "ALTER")]
        [InlineData("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD'", SqlKind.Other, "ALTER")]
        [InlineData("alter /* 주석 */ session set current_schema = scott", SqlKind.Other, "ALTER")]
        [InlineData("ALTER SYSTEM SET open_cursors = 500", SqlKind.Other, "ALTER")]
        [InlineData("DROP TABLE t", SqlKind.Ddl, "DROP")]
        [InlineData("TRUNCATE TABLE t", SqlKind.Ddl, "TRUNCATE")]
        [InlineData("RENAME t TO u", SqlKind.Ddl, "RENAME")]
        [InlineData("GRANT SELECT ON t TO u", SqlKind.Ddl, "GRANT")]
        [InlineData("REVOKE SELECT ON t FROM u", SqlKind.Ddl, "REVOKE")]
        [InlineData("COMMENT ON TABLE t IS 'x'", SqlKind.Ddl, "COMMENT")]
        [InlineData("PURGE RECYCLEBIN", SqlKind.Ddl, "PURGE")]
        [InlineData("ANALYZE TABLE t COMPUTE STATISTICS", SqlKind.Ddl, "ANALYZE")]
        [InlineData("AUDIT SELECT ON t", SqlKind.Ddl, "AUDIT")]
        [InlineData("NOAUDIT SELECT ON t", SqlKind.Ddl, "NOAUDIT")]
        [InlineData("FLASHBACK TABLE t TO BEFORE DROP", SqlKind.Ddl, "FLASHBACK")]
        [InlineData("BEGIN NULL; END;", SqlKind.PlSql, "BEGIN")]
        [InlineData("DECLARE x NUMBER; BEGIN NULL; END;", SqlKind.PlSql, "DECLARE")]
        [InlineData("CALL p(1)", SqlKind.PlSql, "CALL")]
        [InlineData("EXEC p(1)", SqlKind.PlSql, "EXEC")]
        [InlineData("execute p(1)", SqlKind.PlSql, "EXECUTE")]
        [InlineData("COMMIT", SqlKind.Transaction, "COMMIT")]
        [InlineData("ROLLBACK", SqlKind.Transaction, "ROLLBACK")]
        [InlineData("SAVEPOINT a", SqlKind.Transaction, "SAVEPOINT")]
        [InlineData("SET TRANSACTION READ ONLY", SqlKind.Transaction, "SET")]
        [InlineData("SET CONSTRAINTS ALL DEFERRED", SqlKind.Transaction, "SET")]
        [InlineData("set constraint fk_emp_dept immediate", SqlKind.Transaction, "SET")]
        [InlineData("SET ROLE ALL", SqlKind.Other, "SET")]
        [InlineData("LOCK TABLE t IN EXCLUSIVE MODE", SqlKind.Other, "LOCK")]
        [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM dual", SqlKind.Other, "EXPLAIN")]
        [InlineData("'키워드 아님'", SqlKind.Other, "")]
        public void Kind_AndVerb_FromFirstKeyword(string sql, SqlKind kind, string verb)
        {
            var parsed = SqlScript.Parse(sql);
            Assert.Equal(kind, parsed.Kind);
            Assert.Equal(verb, parsed.Verb);

            var split = Assert.Single(SqlScript.Split(sql + ";"));
            Assert.Equal(kind, split.Kind);
            Assert.Equal(verb, split.Verb);
        }

        [Theory]
        [InlineData("COMMIT", SqlTransactionAction.Commit)]
        [InlineData("commit work", SqlTransactionAction.Commit)]
        [InlineData("COMMIT WRITE NOWAIT", SqlTransactionAction.Commit)]
        [InlineData("COMMIT WORK COMMENT 'batch'", SqlTransactionAction.Commit)]
        [InlineData("COMMIT FORCE '22.57.53'", SqlTransactionAction.Other)]
        [InlineData("ROLLBACK", SqlTransactionAction.Rollback)]
        [InlineData("ROLLBACK WORK", SqlTransactionAction.Rollback)]
        [InlineData("ROLLBACK TO SAVEPOINT a", SqlTransactionAction.Other)]
        [InlineData("ROLLBACK WORK TO a", SqlTransactionAction.Other)]
        [InlineData("ROLLBACK FORCE '22.57.53'", SqlTransactionAction.Other)]
        [InlineData("SAVEPOINT a", SqlTransactionAction.Other)]
        [InlineData("SET TRANSACTION READ ONLY", SqlTransactionAction.Other)]
        [InlineData("SET CONSTRAINTS ALL DEFERRED", SqlTransactionAction.Other)]
        [InlineData("SET ROLE ALL", SqlTransactionAction.None)]
        [InlineData("SELECT 1 FROM dual", SqlTransactionAction.None)]
        [InlineData("BEGIN COMMIT; END;", SqlTransactionAction.None)]
        public void TransactionAction_FromStatement(string sql, SqlTransactionAction action)
        {
            Assert.Equal(action, SqlScript.Parse(sql).TransactionAction);
        }

        [Fact]
        public void TransactionAction_InScriptWithSemicolons()
        {
            var statements = SqlScript.Split("UPDATE t SET a = 1 WHERE b = 2;\nCOMMIT;\nROLLBACK WORK;\nROLLBACK TO SAVEPOINT s1;");

            Assert.Equal(new[] { SqlTransactionAction.None, SqlTransactionAction.Commit, SqlTransactionAction.Rollback, SqlTransactionAction.Other },
                statements.Select(s => s.TransactionAction));
            Assert.Equal(SqlKind.Transaction, statements[1].Kind);
        }

        [Theory]
        [InlineData("SELECT * FROM t FOR UPDATE", true)]
        [InlineData("select * from t where a = 1 for update of a nowait", true)]
        [InlineData("SELECT * FROM t FOR /* 잠금 */ UPDATE SKIP LOCKED", true)]
        [InlineData("WITH x AS (SELECT 1 a FROM dual) SELECT * FROM x FOR UPDATE", true)]
        [InlineData("SELECT * FROM t WHERE a = 1FOR UPDATE", true)]
        [InlineData("SELECT * FROM t", false)]
        [InlineData("SELECT 'FOR UPDATE' FROM t", false)]
        [InlineData("SELECT q'[FOR UPDATE]' FROM t", false)]
        [InlineData("SELECT * FROM t -- FOR UPDATE", false)]
        [InlineData("SELECT * FROM t /* FOR UPDATE */", false)]
        [InlineData("SELECT * FROM (SELECT * FROM t FOR UPDATE)", false)]
        [InlineData("SELECT * FROM t PIVOT (SUM(a) FOR b IN (1, 2))", false)]
        [InlineData("UPDATE t SET a = 1 WHERE b IN (SELECT b FROM u FOR UPDATE)", false)]
        [InlineData("DECLARE CURSOR c IS SELECT * FROM t FOR UPDATE; BEGIN NULL; END;", false)]
        public void ForUpdate_OnlyTopLevelSelectOutsideStringsAndComments(string sql, bool expected)
        {
            Assert.Equal(expected, SqlScript.Parse(sql).ForUpdate);
        }

        [Theory]
        [InlineData("UPDATE t SET a = 1", UpdateDanger)]
        [InlineData("update t set a = 1", UpdateDanger)]
        [InlineData("UPDATE t SET a = (SELECT x FROM y WHERE z = 1)", UpdateDanger)]
        [InlineData("UPDATE t SET a = 1 WHERE id = 1", null)]
        [InlineData("update t set a = 1 where id = 1", null)]
        [InlineData("UPDATE t SET a = 1 -- WHERE id = 1", UpdateDanger)]
        [InlineData("UPDATE t SET a = 1 /* WHERE id = 1 */", UpdateDanger)]
        [InlineData("UPDATE t SET a = 'WHERE id = 1'", UpdateDanger)]
        [InlineData("UPDATE t SET \"WHERE\" = 1", UpdateDanger)]
        [InlineData("UPDATE t SET where_flag = 1", UpdateDanger)]
        [InlineData("UPDATE t SET a=1WHERE id=2", null)]
        [InlineData("UPDATE 사원 SET 이름 = '홍'", UpdateDanger)]
        [InlineData("UPDATE 사원 SET 이름 = '홍' WHERE 번호 = 1", null)]
        [InlineData("DELETE t", DeleteDanger)]
        [InlineData("DELETE FROM t", DeleteDanger)]
        [InlineData("DELETE FROM t WHERE id = 1", null)]
        [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u)", null)]
        [InlineData("DELETE FROM t -- WHERE id = 1", DeleteDanger)]
        [InlineData("DROP TABLE x", DdlDanger)]
        [InlineData("drop table x purge", DdlDanger)]
        [InlineData("TRUNCATE TABLE x", DdlDanger)]
        [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = s.a", null)]
        [InlineData("INSERT INTO t SELECT * FROM u", null)]
        [InlineData("SELECT * FROM t", null)]
        [InlineData("ALTER TABLE t ADD b NUMBER", DdlDanger)]
        [InlineData("ALTER TABLE t DROP COLUMN b", DdlDanger)]
        [InlineData("alter table t truncate partition p2019", DdlDanger)]
        [InlineData("ALTER TABLE t DROP PARTITION p2019 UPDATE INDEXES", DdlDanger)]
        [InlineData("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD'", null)]
        [InlineData("ALTER SYSTEM KILL SESSION '12,345'", null)]
        [InlineData("PURGE RECYCLEBIN", DdlDanger)]
        [InlineData("FLASHBACK TABLE t TO TIMESTAMP SYSTIMESTAMP - INTERVAL '1' HOUR", DdlDanger)]
        [InlineData("FLASHBACK TABLE t TO BEFORE DROP", null)]
        [InlineData("BEGIN DELETE FROM t; END;", null)]
        public void Danger_FromStatement(string sql, string danger)
        {
            Assert.Equal(danger, SqlScript.Parse(sql).Danger);
        }

        [Fact]
        public void Danger_WhereOfNextStatementDoesNotCount()
        {
            var statements = SqlScript.Split("UPDATE t SET a = 1;\nSELECT * FROM u WHERE b = 1;");

            Assert.Equal(UpdateDanger, statements[0].Danger);
            Assert.Null(statements[1].Danger);
        }

        // ---- 커서 위치 ----

        [Fact]
        public void AtCaret_PicksStatementAroundCaret()
        {
            const string script = "SELECT 1 FROM dual;\n\nSELECT 2 FROM dual;\n\nSELECT 3 FROM dual;\n-- 끝\n";
            var semicolon = script.IndexOf(';');
            var second = script.IndexOf("SELECT 2", StringComparison.Ordinal);

            Assert.Equal("SELECT 1 FROM dual", SqlScript.AtCaret(script, 0).Text);
            Assert.Equal("SELECT 2 FROM dual", SqlScript.AtCaret(script, second + 3).Text);      // 두 번째 문장 안
            Assert.Equal("SELECT 1 FROM dual", SqlScript.AtCaret(script, semicolon + 1).Text);   // ';' 바로 뒤
            Assert.Equal("SELECT 2 FROM dual", SqlScript.AtCaret(script, semicolon + 2).Text);   // 빈 줄 → 다음 문장(마지막 아님)
            Assert.Equal("SELECT 3 FROM dual", SqlScript.AtCaret(script, script.Length).Text);   // 마지막 문장 뒤 → 마지막
        }

        [Fact]
        public void AtCaret_BetweenAdjacentStatements_PrefersTheOneJustEnded()
        {
            Assert.Equal("SELECT 1", SqlScript.AtCaret("SELECT 1;SELECT 2;", 9).Text);
            Assert.Equal("SELECT 2", SqlScript.AtCaret("SELECT 1;SELECT 2;", 10).Text);
        }

        [Fact]
        public void AtCaret_InCommentBeforeStatement_PicksFollowingStatement()
        {
            const string script = "SELECT 1 FROM dual;\n-- 다음 문장 설명\nSELECT 2 FROM dual;";

            Assert.Equal("SELECT 2 FROM dual", SqlScript.AtCaret(script, script.IndexOf("다음", StringComparison.Ordinal)).Text);
        }

        [Fact]
        public void AtCaret_BlankLineInsideBlock_PicksBlock()
        {
            const string script = "SELECT 1 FROM dual;\nBEGIN\n  NULL;\n\n  NULL;\nEND;\n/\nSELECT 2 FROM dual;";

            var statement = SqlScript.AtCaret(script, script.IndexOf("\n\n", StringComparison.Ordinal) + 1);

            Assert.True(statement.IsPlSqlBlock);
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("-- 주석만\n/* 또 주석 */\n", 5)]
        public void AtCaret_NoStatements_ReturnsNull(string script, int caret)
        {
            Assert.Null(SqlScript.AtCaret(script, caret));
        }

        // ---- 위치·길이 ----

        [Fact]
        public void StartAndLength_CoverFirstTokenThroughTerminator()
        {
            var statements = SqlScript.Split(MixedScript);

            Assert.Equal(new[]
            {
                "SELECT 1 FROM dual;",
                "UPDATE t SET a = 1\n/",
                "BEGIN\n  NULL;\nEND;\n  /",
                "DELETE FROM t WHERE id = 1 -- 끝",
                "EXEC p(1);",
                "SELECT 2 FROM dual"
            }, statements.Select(s => MixedScript.Substring(s.Start, s.Length)));
            Assert.All(statements, s => Assert.StartsWith(s.Verb, MixedScript.Substring(s.Start)));
        }

        [Fact]
        public void Parse_OfEachSplitRange_GivesSameStatement()
        {
            foreach (var s in SqlScript.Split(MixedScript))
            {
                var parsed = SqlScript.Parse(MixedScript.Substring(s.Start, s.Length));

                Assert.Equal(s.Text, parsed.Text);
                Assert.Equal(s.Length, parsed.Length);
                Assert.Equal(s.Kind, parsed.Kind);
                Assert.Equal(s.Verb, parsed.Verb);
                Assert.Equal(s.StrippedSemicolon, parsed.StrippedSemicolon);
                Assert.Equal(s.IsPlSqlBlock, parsed.IsPlSqlBlock);
                Assert.Equal(s.Danger, parsed.Danger);
            }
        }

        // ---- 선택 영역 ----

        [Fact]
        public void Parse_SelectionWithLeadingCommentsAndTrailingSemicolon()
        {
            const string text = "\n-- 사용자 목록\n/* 조건 없음 */\nSELECT * FROM users;  \n";

            var statement = SqlScript.Parse(text);

            Assert.Equal("SELECT * FROM users", statement.Text);
            Assert.Equal("SELECT", statement.Verb);
            Assert.Equal(SqlKind.Query, statement.Kind);
            Assert.True(statement.StrippedSemicolon);
            Assert.Equal(0, statement.Start);
            Assert.Equal(text.IndexOf(';') + 1, statement.Length);
        }

        [Fact]
        public void Parse_DoesNotSplitOnBlankLines()
        {
            Assert.Equal("SELECT a,\n\n  b FROM t", SqlScript.Parse("SELECT a,\n\n  b FROM t;").Text);
        }

        [Fact]
        public void Parse_PlSqlBlockWithSlash_KeepsEndSemicolon()
        {
            var statement = SqlScript.Parse("BEGIN\n  NULL;\nEND;\n/\n");

            Assert.Equal("BEGIN\n  NULL;\nEND;", statement.Text);
            Assert.True(statement.IsPlSqlBlock);
            Assert.False(statement.StrippedSemicolon);
        }

        [Fact]
        public void Parse_SemicolonThenSlashLine_StripsBoth()
        {
            const string text = "SELECT 1 FROM dual;\n/";

            var statement = SqlScript.Parse(text);

            Assert.Equal("SELECT 1 FROM dual", statement.Text);
            Assert.True(statement.StrippedSemicolon);
            Assert.Equal(text.Length, statement.Length);
        }

        [Fact]
        public void Parse_CommentAfterSemicolonIsDropped_InnerCommentKept()
        {
            Assert.Equal("UPDATE t /* 안쪽 */ SET a = 1", SqlScript.Parse("UPDATE t /* 안쪽 */ SET a = 1; -- 끝").Text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  \n\t")]
        [InlineData("-- 주석\n/* 주석 */")]
        [InlineData(" ; \n/\n")]
        public void Parse_NothingToRun_ReturnsNull(string text)
        {
            Assert.Null(SqlScript.Parse(text));
        }

        // ---- 닫히지 않은 문자열·주석, 줄바꿈 ----

        [Theory]
        [InlineData("SELECT 'abc; SELECT 2 FROM dual")]
        [InlineData("SELECT q'[abc; SELECT 2 FROM dual")]
        [InlineData("SELECT \"abc; SELECT 2 FROM dual")]
        [InlineData("SELECT 1 /* abc; SELECT 2 FROM dual")]
        [InlineData("SELECT '")]
        [InlineData("SELECT q'")]
        [InlineData("SELECT nq'")]
        [InlineData("SELECT N'")]
        [InlineData("SELECT 1 --")]
        [InlineData("SELECT 1 /*")]
        public void Split_UnterminatedStringOrComment_RunsToEndWithoutThrowing(string script)
        {
            var statement = Assert.Single(SqlScript.Split(script));

            Assert.Equal(script, statement.Text);
            Assert.Equal(script.Length, statement.Length);
            Assert.Equal(script, SqlScript.AtCaret(script, script.Length).Text);
            Assert.Equal(script, SqlScript.Parse(script).Text);
        }

        [Fact]
        public void Split_UnterminatedCommentAfterStatement_IsIgnored()
        {
            Assert.Equal(new[] { "SELECT 1" }, Texts("SELECT 1; /* abc; SELECT 2\n\n"));
        }

        [Fact]
        public void Split_UnterminatedStringAtEnd_TextIsTrimmed()
        {
            Assert.Equal(new[] { "SELECT 'abc" }, Texts("SELECT 'abc  \n\n"));
        }

        [Fact]
        public void Split_CrLfScript()
        {
            const string script = "SELECT 1 FROM dual\r\n\r\n"
                + "BEGIN\r\n  NULL;\r\n\r\nEND;\r\n/\r\n"
                + "SELECT 2 -- c\r\n;\r\n"
                + "SELECT 3 FROM dual\r\n   \r\n"
                + "SELECT 4 FROM dual\r\n/\r\n";

            var statements = SqlScript.Split(script);

            Assert.Equal(new[] { "SELECT 1 FROM dual", "BEGIN\r\n  NULL;\r\n\r\nEND;", "SELECT 2 -- c", "SELECT 3 FROM dual", "SELECT 4 FROM dual" },
                statements.Select(s => s.Text));
            Assert.Equal("BEGIN\r\n  NULL;\r\n\r\nEND;\r\n/", script.Substring(statements[1].Start, statements[1].Length));
            Assert.Equal("SELECT 2 -- c\r\n;", script.Substring(statements[2].Start, statements[2].Length));
            Assert.Equal("SELECT 4 FROM dual\r\n/", script.Substring(statements[4].Start, statements[4].Length));
            Assert.True(statements[1].IsPlSqlBlock);
        }

        [Fact]
        public void Split_CarriageReturnOnlyLineEndings()
        {
            Assert.Equal(new[] { "SELECT 1", "BEGIN NULL;\r\rEND;" }, Texts("SELECT 1\r\rBEGIN NULL;\r\rEND;\r/\r"));
        }

        // ---- 컴파일 오류를 찾을 객체 ----

        [Theory]
        [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;", null, "P", "PROCEDURE")]
        [InlineData("create or replace editionable package body scott.pkg as end;", "SCOTT", "PKG", "PACKAGE BODY")]
        [InlineData("CREATE PACKAGE \"MixedCase\" AS END;", null, "MixedCase", "PACKAGE")]
        [InlineData("CREATE OR REPLACE /* 주석 */ NONEDITIONABLE TRIGGER hr.trg BEFORE INSERT ON t BEGIN NULL; END;", "HR", "TRG", "TRIGGER")]
        [InlineData("CREATE OR REPLACE TYPE BODY t_obj AS END;", null, "T_OBJ", "TYPE BODY")]
        [InlineData("CREATE OR REPLACE TYPE t_obj AS OBJECT (a NUMBER);", null, "T_OBJ", "TYPE")]
        [InlineData("CREATE OR REPLACE FORCE EDITIONING VIEW v AS SELECT x FROM missing", null, "V", "VIEW")]
        [InlineData("CREATE OR REPLACE NO FORCE VIEW app.v AS SELECT 1 a FROM dual", "APP", "V", "VIEW")]
        [InlineData("ALTER PROCEDURE p COMPILE", null, "P", "PROCEDURE")]
        [InlineData("ALTER PACKAGE scott.pkg COMPILE BODY", "SCOTT", "PKG", "PACKAGE BODY")]
        [InlineData("ALTER PACKAGE pkg COMPILE SPECIFICATION", null, "PKG", "PACKAGE")]
        [InlineData("ALTER PACKAGE pkg COMPILE", null, "PKG", "PACKAGE,PACKAGE BODY")]
        [InlineData("alter type t compile reuse settings", null, "T", "TYPE,TYPE BODY")]
        [InlineData("ALTER VIEW v COMPILE", null, "V", "VIEW")]
        public void CompileTargetOf_FindsCompiledObject(string sql, string owner, string name, string types)
        {
            var target = SqlScript.CompileTargetOf(sql);

            Assert.NotNull(target);
            Assert.Equal(owner, target.Owner);
            Assert.Equal(name, target.Name);
            Assert.Equal(types.Split(','), target.Types);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("CREATE TABLE t (a NUMBER)")]
        [InlineData("CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"Hi\" AS public class Hi { }")]
        [InlineData("ALTER TRIGGER trg ENABLE")]
        [InlineData("ALTER TABLE t ADD b NUMBER")]
        [InlineData("DROP PROCEDURE p")]
        [InlineData("BEGIN NULL; END;")]
        [InlineData("CREATE OR REPLACE PROCEDURE")]
        public void CompileTargetOf_OtherStatements_Null(string sql)
        {
            Assert.Null(SqlScript.CompileTargetOf(sql));
        }

        // ---- LIKE 패턴 ----

        [Theory]
        [InlineData("EMP", "EMP")]
        [InlineData("EMP_NO", "EMP\\_NO")]
        [InlineData("50%", "50\\%")]
        [InlineData("C:\\TEMP", "C:\\\\TEMP")]
        [InlineData("a\\_%b", "a\\\\\\_\\%b")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void EscapeLike_EscapesBackslashPercentUnderscore(string term, string expected)
        {
            Assert.Equal(expected, SqlScript.EscapeLike(term));
        }

        [Theory]
        [InlineData("  emp_No ", "%EMP\\_NO%")]
        [InlineData("50%", "%50\\%%")]
        [InlineData("a\\b", "%A\\\\B%")]
        [InlineData(" Tmp_% \\x ", "%TMP\\_\\% \\\\X%")]
        [InlineData("사원", "%사원%")]
        [InlineData("", "%%")]
        [InlineData("   ", "%%")]
        [InlineData(null, "%%")]
        public void ContainsPattern_TrimsUppercasesAndEscapes(string term, string expected)
        {
            Assert.Equal(expected, SqlScript.ContainsPattern(term));
        }
    }
}
