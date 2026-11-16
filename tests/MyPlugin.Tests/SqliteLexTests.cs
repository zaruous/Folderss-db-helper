using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>SQLite 구문 규칙: 트리거 본문(BEGIN … END)의 ';'·빈 줄에서 문장을 자르지 않고, [ident]·`ident`를 식별자로 본다.</summary>
    public class SqliteLexTests
    {
        private static readonly SqlLexRules Lite = SqlLexRules.Sqlite;

        private static string[] Texts(string script)
        {
            return SqlScript.Split(script, Lite).Select(s => s.Text).ToArray();
        }

        [Fact]
        public void Trigger_BodyWithSemicolonsAndBlankLine_IsOneStatement()
        {
            const string script = "CREATE TRIGGER trg AFTER INSERT ON t\nBEGIN\n  UPDATE t SET a = 1 WHERE id = new.id;\n\n  INSERT INTO log VALUES (new.id);\nEND;\nSELECT 1;";
            var parts = SqlScript.Split(script, Lite);
            Assert.Equal(2, parts.Count);
            Assert.StartsWith("CREATE TRIGGER", parts[0].Text);
            Assert.EndsWith("END", parts[0].Text);
            Assert.Equal(SqlKind.Ddl, parts[0].Kind);
            Assert.False(parts[0].IsPlSqlBlock);
            Assert.Equal("SELECT 1", parts[1].Text);
        }

        [Fact]
        public void Trigger_CaseEndInsideBody_DoesNotEndTrigger()
        {
            const string script = "CREATE TEMP TRIGGER IF NOT EXISTS trg BEFORE UPDATE ON t BEGIN\n"
                + "  SELECT CASE WHEN new.a < 0 THEN RAISE(ABORT, 'neg; no') END;\n"
                + "  UPDATE t SET b = CASE a WHEN 1 THEN 'x' ELSE 'y' END WHERE id = new.id;\nEND;\nSELECT 2;";
            var parts = Texts(script);
            Assert.Equal(2, parts.Length);
            Assert.EndsWith("WHERE id = new.id;\nEND", parts[0]);
            Assert.Equal("SELECT 2", parts[1]);
        }

        [Fact]
        public void Trigger_WithoutTerminator_RunsToScriptEnd()
        {
            const string script = "CREATE TRIGGER trg AFTER DELETE ON t BEGIN DELETE FROM c WHERE pid = old.id; END";
            var parts = Texts(script);
            Assert.Single(parts);
            Assert.EndsWith("END", parts[0]);
        }

        [Fact]
        public void Trigger_AtCaretInsideBody_ReturnsWholeTrigger()
        {
            const string script = "SELECT 0;\nCREATE TRIGGER trg AFTER INSERT ON t BEGIN\n  DELETE FROM x;\n  DELETE FROM y;\nEND;";
            var at = SqlScript.AtCaret(script, script.IndexOf("FROM y"), Lite);
            Assert.StartsWith("CREATE TRIGGER", at.Text);
        }

        [Fact]
        public void Begin_IsTransactionStatement_NotBlock()
        {
            var parts = SqlScript.Split("BEGIN;\nINSERT INTO t VALUES (1);\nCOMMIT;", Lite);
            Assert.Equal(new[] { "BEGIN", "INSERT INTO t VALUES (1)", "COMMIT" }, parts.Select(p => p.Text).ToArray());
            Assert.All(parts, p => Assert.False(p.IsPlSqlBlock));
        }

        [Fact]
        public void BracketAndBacktickIdentifiers_HideSemicolons()
        {
            var parts = Texts("SELECT [a;b], `c;d`, \"e;f\" FROM t;\nSELECT 2;");
            Assert.Equal(new[] { "SELECT [a;b], `c;d`, \"e;f\" FROM t", "SELECT 2" }, parts);
            var spans = SqlScript.EditorSpans("SELECT `x``y` FROM t", Lite);
            Assert.Contains(spans, s => s.Kind == SqlSpanKind.Identifier && s.Start == 7 && s.Length == 6);
        }

        [Fact]
        public void SlashLine_IsNotSeparator()
        {
            // SQLite에는 '/' 줄 구분자가 없다: 나눗셈 줄은 문장 일부
            var parts = Texts("SELECT 4\n/\n2;");
            Assert.Single(parts);
        }

        [Fact]
        public void Keywords_IncludeSqliteWords()
        {
            Assert.True(Lite.Keywords.Contains("PRAGMA"));
            Assert.True(Lite.Keywords.Contains("VACUUM"));
            Assert.True(Lite.Keywords.Contains("ATTACH"));
            Assert.True(Lite.Types.Contains("TEXT"));
        }
    }
}
