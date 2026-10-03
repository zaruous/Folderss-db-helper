using System;
using System.IO;
using System.Linq;
using System.Text;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>SqlFileLogic: SQL 파일 인코딩 판별·저장(원래 인코딩·줄바꿈 유지), 최근 파일 목록, 기본 이름.</summary>
    public class SqlFileLogicTests : IDisposable
    {
        private const string Sample = "SELECT '한글' AS 이름\r\n  FROM DUAL;";
        private static readonly Encoding Cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949);
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "sql-file-test-" + Guid.NewGuid().ToString("N"));

        public SqlFileLogicTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        [Fact]
        public void Decode_Utf8WithoutBom()
        {
            var content = SqlFileLogic.Decode(Encoding.UTF8.GetBytes(Sample));

            Assert.Equal(SqlFileEncoding.Utf8, content.Encoding);
            Assert.Equal(Sample, content.Text);
            Assert.Equal("\r\n", content.Newline);
        }

        [Fact]
        public void Decode_Boms()
        {
            Assert.Equal(SqlFileEncoding.Utf8Bom, SqlFileLogic.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Sample)).ToArray()).Encoding);
            var le = SqlFileLogic.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Sample)).ToArray());
            Assert.Equal(SqlFileEncoding.Utf16Le, le.Encoding);
            Assert.Equal(Sample, le.Text);
            var be = SqlFileLogic.Decode(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(Sample)).ToArray());
            Assert.Equal(SqlFileEncoding.Utf16Be, be.Encoding);
            Assert.Equal(Sample, be.Text);
        }

        [Fact]
        public void Decode_Cp949WhenNotValidUtf8()
        {
            // 한국어 Windows 도구가 저장한 .sql(EUC-KR) — UTF-8로 읽으면 깨진다
            var content = SqlFileLogic.Decode(Cp949.GetBytes(Sample));

            Assert.Equal(SqlFileEncoding.Cp949, content.Encoding);
            Assert.Equal(Sample, content.Text);
        }

        [Fact]
        public void Decode_EmptyFile()
        {
            var content = SqlFileLogic.Decode(new byte[0]);
            Assert.Equal("", content.Text);
            Assert.Equal(SqlFileEncoding.Utf8, content.Encoding);
        }

        [Theory]
        [InlineData("a\r\nb\nc", "\r\n")]
        [InlineData("a\nb\n", "\n")]
        [InlineData("한 줄", "\r\n")]
        [InlineData("", "\r\n")]
        public void DetectNewline(string text, string expected)
        {
            Assert.Equal(expected, SqlFileLogic.DetectNewline(text));
        }

        [Theory]
        [InlineData("Utf8")]
        [InlineData("Utf8Bom")]
        [InlineData("Utf16Le")]
        [InlineData("Utf16Be")]
        [InlineData("Cp949")]
        public void WriteThenRead_KeepsTextAndEncoding(string encodingName)
        {
            // SqlFileEncoding은 internal이라 시험 매개변수로 이름을 받는다
            var encoding = (SqlFileEncoding)Enum.Parse(typeof(SqlFileEncoding), encodingName);
            var path = Path.Combine(_folder, "q.sql");

            var used = SqlFileLogic.Write(path, Sample, encoding);
            var read = SqlFileLogic.Read(path);

            Assert.Equal(encoding, used);
            Assert.Equal(encoding, read.Encoding);
            Assert.Equal(Sample, read.Text);
            Assert.Single(Directory.GetFiles(_folder)); // 임시 파일이 남지 않는다
        }

        [Fact]
        public void Write_Utf8HasNoBom()
        {
            var path = Path.Combine(_folder, "q.sql");
            SqlFileLogic.Write(path, "SELECT 1", SqlFileEncoding.Utf8);

            Assert.Equal(Encoding.UTF8.GetBytes("SELECT 1"), File.ReadAllBytes(path));
        }

        [Fact]
        public void Write_OverwritesExistingFile()
        {
            var path = Path.Combine(_folder, "q.sql");
            File.WriteAllText(path, "OLD OLD OLD OLD");

            SqlFileLogic.Write(path, "NEW", SqlFileEncoding.Utf8);

            Assert.Equal("NEW", File.ReadAllText(path));
        }

        [Fact]
        public void Encode_Cp949WithUnrepresentableChar_FallsBackToUtf8()
        {
            SqlFileEncoding used;
            var bytes = SqlFileLogic.Encode("SELECT '한글 😀' FROM DUAL", SqlFileEncoding.Cp949, out used);

            Assert.Equal(SqlFileEncoding.Utf8, used);
            Assert.Equal("SELECT '한글 😀' FROM DUAL", Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public void Encode_Cp949KeepsQuestionMarks()
        {
            SqlFileEncoding used;
            SqlFileLogic.Encode("SELECT '?' FROM DUAL WHERE X = :p", SqlFileEncoding.Cp949, out used);
            Assert.Equal(SqlFileEncoding.Cp949, used);
        }

        [Fact]
        public void Read_TooLargeFile_Throws()
        {
            var path = Path.Combine(_folder, "big.sql");
            using (var f = File.Create(path))
                f.SetLength(SqlFileLogic.MaxFileBytes + 1);

            var ex = Assert.Throws<IOException>(() => SqlFileLogic.Read(path));
            Assert.Contains("너무 큽니다", ex.Message);
        }

        [Fact]
        public void AddRecent_MovesToFrontDedupesAndCaps()
        {
            var list = Enumerable.Range(1, 10).Select(i => @"C:\sql\q" + i + ".sql").ToList();

            var added = SqlFileLogic.AddRecent(list, @"C:\SQL\Q5.sql");
            Assert.Equal(10, added.Count);
            Assert.Equal(@"C:\SQL\Q5.sql", added[0]);
            Assert.Equal(1, added.Count(p => p.EndsWith("5.sql", StringComparison.OrdinalIgnoreCase)));

            var fresh = SqlFileLogic.AddRecent(list, @"C:\sql\new.sql");
            Assert.Equal(10, fresh.Count);
            Assert.Equal(@"C:\sql\new.sql", fresh[0]);
            Assert.DoesNotContain(@"C:\sql\q10.sql", fresh);
        }

        [Fact]
        public void ParseSerializeRecent_RoundTrip()
        {
            var text = SqlFileLogic.SerializeRecent(new[] { @"C:\a.sql", @"D:\b.sql" });

            Assert.Equal(new[] { @"C:\a.sql", @"D:\b.sql" }, SqlFileLogic.ParseRecent(text).ToArray());
            Assert.Equal(new[] { @"C:\a.sql" }, SqlFileLogic.ParseRecent("C:\\a.sql\n\n  \nc:\\A.SQL\n").ToArray());
            Assert.Empty(SqlFileLogic.ParseRecent(null));
        }

        [Fact]
        public void RecentMenuText_NumbersAndEscapesUnderscores()
        {
            var text = SqlFileLogic.RecentMenuText(0, Path.Combine(_folder, "my_query.sql"));
            Assert.StartsWith("_1  my__query.sql   ", text);
            Assert.StartsWith("_0  ", SqlFileLogic.RecentMenuText(9, Path.Combine(_folder, "a.sql")));
        }

        [Theory]
        [InlineData("SQL 1", "SQL 1.sql")]
        [InlineData("orders.sql", "orders.sql")]
        [InlineData("a/b:c", "a_b_c.sql")]
        [InlineData("", "query.sql")]
        public void DefaultFileName(string title, string expected)
        {
            Assert.Equal(expected, SqlFileLogic.DefaultFileName(title));
        }

        [Fact]
        public void SamePath_IgnoresCaseAndRelativeParts()
        {
            Assert.True(SqlFileLogic.SamePath(Path.Combine(_folder, "x", "..", "A.sql"), Path.Combine(_folder, "a.SQL")));
            Assert.False(SqlFileLogic.SamePath(Path.Combine(_folder, "a.sql"), Path.Combine(_folder, "b.sql")));
            Assert.False(SqlFileLogic.SamePath(null, "a"));
        }

        [Fact]
        public void Messages()
        {
            Assert.Equal("열었습니다: q.sql (CP949(EUC-KR))", SqlFileLogic.OpenedMessage("q.sql", SqlFileEncoding.Cp949));
            Assert.Equal("저장했습니다: q.sql (UTF-8)", SqlFileLogic.SavedMessage("q.sql", SqlFileEncoding.Utf8));
            Assert.Contains("5MB까지", SqlFileLogic.TooLargeMessage(9L * 1024 * 1024));
        }
    }
}
