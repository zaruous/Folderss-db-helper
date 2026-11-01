using System;
using System.IO;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class ResultExcelTests
    {
        [Fact]
        public void RoundTripAndReorderedHeaders()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                var columns = new[] { new ResultColumn { Name = "ID" }, new ResultColumn { Name = "NOTE" } };
                ResultExcel.Write(path, columns, new[] { new[] { "00012345678901234567890", "=literal" }, new string[] { "2", null } });
                var rows = ResultExcel.Read(path, columns);
                Assert.Equal("00012345678901234567890", rows[0][0]);
                Assert.Equal("=literal", rows[0][1]);
                Assert.Null(rows[1][1]);

                // 헤더 순서가 바뀌었을 때
                using (var book = new XLWorkbook(path))
                {
                    var sheet = book.Worksheet(1);
                    sheet.Cell(1, 1).Value = "NOTE";
                    sheet.Cell(1, 2).Value = "ID";
                    book.Save();
                }
                var reordered = ResultExcel.Read(path, columns);
                Assert.Equal("=literal", reordered[0][0]); // columns[0]은 ID인데 엑셀의 ID 열(2열)의 값은 "=literal"

                // 수식이 있는 경우 에러
                using (var book = new XLWorkbook(path))
                {
                    book.Worksheet(1).Cell(2, 1).FormulaA1 = "1+1";
                    book.Save();
                }
                Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(path, columns));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ReadsNumericCellsAndExtraColumns()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("Data");
                    // 1열: EXTRA (DB에 없는 열), 2열: id (소문자), 3열: AMOUNT (숫자 데이터)
                    sheet.Cell(1, 1).Value = "EXTRA";
                    sheet.Cell(1, 2).Value = "id";
                    sheet.Cell(1, 3).Value = "AMOUNT";

                    sheet.Cell(2, 1).Value = "ignore_me";
                    sheet.Cell(2, 2).Value = "EMP_101";
                    sheet.Cell(2, 3).Value = 12500; // Number 타입
                    book.SaveAs(path);
                }

                var columns = new[]
                {
                    new ResultColumn { Name = "ID" },
                    new ResultColumn { Name = "AMOUNT" }
                };

                var rows = ResultExcel.Read(path, columns);
                Assert.Single(rows);
                Assert.Equal("EMP_101", rows[0][0]);
                Assert.Equal("12500", rows[0][1]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ThrowsOnMissingOrDuplicateColumns()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("Data");
                    sheet.Cell(1, 1).Value = "COL1";
                    sheet.Cell(1, 2).Value = "COL1"; // 중복
                    book.SaveAs(path);
                }

                var columns = new[] { new ResultColumn { Name = "COL1" } };
                Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(path, columns));

                var missingColumns = new[] { new ResultColumn { Name = "COL_MISSING" } };
                Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(path, missingColumns));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
