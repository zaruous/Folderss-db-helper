using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>EditorLogic: 줄 주석 토글(Ctrl+/), 대·소문자 바꾸기.</summary>
    public class EditorLogicTests
    {
        private static string Apply(string text, TextEdit edit)
        {
            return text.Substring(0, edit.Start) + edit.Replacement + text.Substring(edit.Start + edit.Length);
        }

        [Fact]
        public void Toggle_CaretLine_AddsCommentAndKeepsCaretOnSameChar()
        {
            var text = "SELECT 1\nFROM DUAL";
            var caret = text.IndexOf("DUAL");

            var edit = EditorLogic.ToggleLineComment(text, caret, 0);

            Assert.Equal("SELECT 1\n-- FROM DUAL", Apply(text, edit));
            Assert.Equal(caret + 3, edit.SelectStart);
            Assert.Equal(0, edit.SelectLength);
        }

        [Fact]
        public void Toggle_Selection_AddsAtShallowestIndentAndSkipsBlankLines()
        {
            var text = "  SELECT *\n\n    FROM T\n  WHERE 1=1";

            var edit = EditorLogic.ToggleLineComment(text, 0, text.Length);

            Assert.Equal("  -- SELECT *\n\n  --   FROM T\n  -- WHERE 1=1", Apply(text, edit));
            Assert.Equal(0, edit.SelectStart);
            Assert.Equal(edit.Replacement.Length, edit.SelectLength);
        }

        [Fact]
        public void Toggle_AllCommented_Uncomments()
        {
            var text = "-- SELECT *\n  --FROM T\n\n-- WHERE 1=1";

            var edit = EditorLogic.ToggleLineComment(text, 0, text.Length);

            Assert.Equal("SELECT *\n  FROM T\n\nWHERE 1=1", Apply(text, edit));
        }

        [Fact]
        public void Toggle_MixedLines_CommentsAll()
        {
            var text = "-- SELECT *\nFROM T";

            Assert.Equal("-- -- SELECT *\n-- FROM T", Apply(text, EditorLogic.ToggleLineComment(text, 0, text.Length)));
        }

        [Fact]
        public void Toggle_SelectionEndingAtNextLineStart_ExcludesThatLine_Crlf()
        {
            var text = "A\r\nB\r\nC";
            var end = text.IndexOf("C"); // "A\r\nB\r\n"까지 선택

            var edit = EditorLogic.ToggleLineComment(text, 0, end);

            Assert.Equal("-- A\r\n-- B\r\nC", Apply(text, edit));
        }

        [Fact]
        public void Toggle_UncommentCaretInsideRemovedComment_ClampsToLineStart()
        {
            var text = "x\n-- y";
            var caret = text.IndexOf("--") + 1; // "-|-" 사이

            var edit = EditorLogic.ToggleLineComment(text, caret, 0);

            Assert.Equal("x\ny", Apply(text, edit));
            Assert.Equal(text.IndexOf("--"), edit.SelectStart);
        }

        [Fact]
        public void Toggle_BlankLineOnly_NoChange()
        {
            var text = "A\n\nB";
            var edit = EditorLogic.ToggleLineComment(text, 2, 0);

            Assert.Equal(text, Apply(text, edit));
        }

        [Fact]
        public void Toggle_EmptyText_NoChange()
        {
            var edit = EditorLogic.ToggleLineComment("", 0, 0);
            Assert.Equal("", Apply("", edit));
        }

        [Fact]
        public void ChangeCase_Invariant()
        {
            Assert.Equal("SELECT 'İ' FROM dual".ToUpperInvariant(), EditorLogic.ChangeCase("SELECT 'İ' FROM dual", true));
            Assert.Equal("select * from emp where name = '한글'", EditorLogic.ChangeCase("SELECT * FROM EMP WHERE NAME = '한글'", false));
            Assert.Equal("", EditorLogic.ChangeCase(null, true));
        }

        // ---------- F4 대상 이름 ----------

        [Theory]
        [InlineData("SELECT * FROM sample1.orders o", "sample1.orders", "SAMPLE1", "ORDERS")]   // 캐럿이 이름 가운데
        [InlineData("SELECT * FROM orders;", "orders", null, "ORDERS")]
        [InlineData("SELECT * FROM \"My_Tab\" t", "\"My_Tab\"", null, "My_Tab")]
        [InlineData("SELECT * FROM SCOTT.\"Emp\"", "SCOTT.\"Emp\"", "SCOTT", "Emp")]
        [InlineData("SELECT * FROM 고객정보", "고객정보", null, "고객정보")]
        public void ObjectNameAt_Caret(string text, string word, string owner, string name)
        {
            var caret = text.IndexOf(word) + word.Length / 2;

            var result = EditorLogic.ObjectNameAt(text, caret, 0);

            Assert.Equal(owner, result.Owner);
            Assert.Equal(name, result.Name);
        }

        [Fact]
        public void ObjectNameAt_CaretRightAfterName()
        {
            var text = "SELECT * FROM emp";
            Assert.Equal("EMP", EditorLogic.ObjectNameAt(text, text.Length, 0).Name);
        }

        [Fact]
        public void ObjectNameAt_Selection_TrimsSpacesAndSemicolon()
        {
            var text = "FROM  sample1.audit_log ;\n";
            var start = text.IndexOf("sample1") - 1;
            var result = EditorLogic.ObjectNameAt(text, start, text.IndexOf(";") + 1 - start);

            Assert.Equal("SAMPLE1", result.Owner);
            Assert.Equal("AUDIT_LOG", result.Name);
        }

        [Theory]
        [InlineData("orders o")]           // 공백이 섞인 블록
        [InlineData("a.b.c")]              // 세 부분
        [InlineData("1abc")]               // 숫자로 시작
        [InlineData("a..b")]
        [InlineData("\"\"")]
        [InlineData("   ")]
        [InlineData("emp@dblink")]
        public void ParseObjectName_RejectsNonNames(string token)
        {
            Assert.Null(EditorLogic.ParseObjectName(token));
        }

        [Fact]
        public void ObjectNameAt_CaretOnSpace_IsNull()
        {
            Assert.Null(EditorLogic.ObjectNameAt("SELECT   FROM", 7, 0));
        }
    }
}
