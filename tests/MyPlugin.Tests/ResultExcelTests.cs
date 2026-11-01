using System;
using System.IO;
using System.Linq;
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
        public void ReadsNumericCells_RejectsUnknownColumns()
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

                // 결과에 없는 열(EXTRA)은 거부한다 — 오타·다른 테이블 서식을 조용히 무시하지 않게
                var error = Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(path, columns));
                Assert.Contains("EXTRA", error.Message);

                // EXTRA 열을 지우면 소문자 머리(id)도 맞추고 숫자 셀은 그리드 형식으로 읽는다
                using (var book = new XLWorkbook(path))
                {
                    book.Worksheet(1).Column(1).Delete();
                    book.Save();
                }
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

        /// <summary>
        /// 사람이 엑셀에서 직접 넣은 날짜·숫자·참거짓 셀도 그리드 표시 형식(저장 검사가 읽는 형식)으로 읽는다 —
        /// 날짜 "yyyy-MM-dd HH:mm:ss", 숫자는 문화권과 무관하게 '.' 소수점·지수 표기 없음. PC 지역 설정(ko-KR·de-DE)에 따라 바뀌지 않는다.
        /// </summary>
        [Theory]
        [InlineData("ko-KR")]
        [InlineData("de-DE")]
        [InlineData("en-US")]
        public void TypedCellsReadInGridFormat(string culture)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            var saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
                var columns = new[] { new ResultColumn { Name = "D" }, new ResultColumn { Name = "T" }, new ResultColumn { Name = "N" },
                    new ResultColumn { Name = "BIG" }, new ResultColumn { Name = "SMALL" }, new ResultColumn { Name = "B" } };
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("Data");
                    for (int c = 0; c < columns.Length; c++) sheet.Cell(1, c + 1).Value = columns[c].Name;
                    sheet.Cell(2, 1).Value = new DateTime(2026, 11, 1);
                    sheet.Cell(2, 2).Value = new DateTime(2026, 11, 1, 13, 5, 9);
                    sheet.Cell(2, 3).Value = 1234.5;
                    sheet.Cell(2, 4).Value = 12345678901234d;
                    sheet.Cell(2, 5).Value = 0.000125;
                    sheet.Cell(2, 6).Value = true;
                    book.SaveAs(path);
                }
                var row = Assert.Single(ResultExcel.Read(path, columns));
                Assert.Equal("2026-11-01 00:00:00", row[0]);
                Assert.Equal("2026-11-01 13:05:09", row[1]);
                Assert.Equal("1234.5", row[2]);
                Assert.Equal("12345678901234", row[3]);
                Assert.Equal("0.000125", row[4]);
                Assert.Equal("1", row[5]);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = saved;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>빈 템플릿도 입력 칸이 텍스트 서식이라 "00123"·"1-2" 같은 값이 숫자·날짜로 바뀌지 않는다. 알 수 없는 열은 거부한다.</summary>
        [Fact]
        public void TemplateCellsAreTextAndUnknownHeadersRejected()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                var columns = new[] { new ResultColumn { Name = "CODE" }, new ResultColumn { Name = "NAME" } };
                ResultExcel.Write(path, columns, Array.Empty<string[]>());
                using (var book = new XLWorkbook(path))
                {
                    var sheet = book.Worksheet(1);
                    Assert.Equal("@", sheet.Cell(2, 1).Style.NumberFormat.Format);
                    Assert.Equal("@", sheet.Cell(500, 2).Style.NumberFormat.Format);
                    sheet.Cell(1, 3).Value = "EXTRA";
                    book.Save();
                }
                var error = Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(path, columns));
                Assert.Contains("EXTRA", error.Message);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        static readonly ResultColumn[] Typed =
        {
            new ResultColumn { Name = "ID", TypeLabel = "NUMBER(10)", IsNumeric = true },
            new ResultColumn { Name = "CODE", TypeLabel = "VARCHAR2(10)" },
            new ResultColumn { Name = "CREATED", TypeLabel = "DATE" }
        };

        [Fact]
        public void TemplateHasTypedColumnFormatsAndRulesSheet_AndRoundTrips()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                ResultExcel.WriteTemplate(path, Typed, "SCOTT.EMP");
                using (var book = new XLWorkbook(path))
                {
                    var data = book.Worksheet(1);
                    Assert.Equal("Data", data.Name);
                    Assert.Equal("General", data.Cell(2, 1).Style.NumberFormat.Format);
                    Assert.Equal("@", data.Cell(2, 2).Style.NumberFormat.Format);
                    Assert.Equal(ResultExcel.DateFormat, data.Cell(2, 3).Style.NumberFormat.Format);
                    Assert.Equal(ResultExcel.RulesSheet, book.Worksheet(2).Name);
                    // 사람이 서식대로 값을 넣는다(숫자·코드·날짜)
                    data.Cell(2, 1).Value = 7;
                    data.Cell(2, 2).Value = "007";
                    data.Cell(2, 3).Value = new DateTime(2026, 11, 1, 9, 30, 0);
                    book.Save();
                }
                var row = Assert.Single(ResultExcel.Read(path, Typed));
                Assert.Equal(new[] { "7", "007", "2026-11-01 09:30:00" }, row);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void DefaultExportWritesNumbersAndDatesAsExcelValues()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                ResultExcel.Write(path, Typed, new[] { new[] { "12", "00A", "2026-11-01 13:05:09" }, new[] { "1234567890123456789", "B", null } });
                using (var book = new XLWorkbook(path))
                {
                    var sheet = book.Worksheet(1);
                    Assert.Equal(XLDataType.Number, sheet.Cell(2, 1).DataType);
                    Assert.Equal(XLDataType.Text, sheet.Cell(2, 2).DataType);
                    Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 3).DataType);
                    Assert.Equal(XLDataType.Text, sheet.Cell(3, 1).DataType); // 15자리를 넘는 숫자는 글자로(엑셀 정밀도)
                }
                Assert.Equal(new[] { new[] { "12", "00A", "2026-11-01 13:05:09" }, new[] { "1234567890123456789", "B", null } }, ResultExcel.Read(path, Typed));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void WriteInto_KeepsTemplateLayoutAndFormats_ClearsOldRows_ReportsMissingColumns()
        {
            var template = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                using (var book = new XLWorkbook())
                {
                    book.AddWorksheet("_안내").Cell(1, 1).Value = "설명 시트(읽지 않음)";
                    var sheet = book.AddWorksheet("사원");
                    // 사용자가 다듬은 서식: 순서 바꿈(CREATED, ID), CODE 열은 뺌, 날짜 서식·색, 예전 값 한 줄
                    sheet.Cell(1, 1).Value = "CREATED";
                    sheet.Cell(1, 2).Value = "ID";
                    sheet.Column(1).Width = 25;
                    sheet.Cell(2, 1).Style.NumberFormat.Format = "yyyy\"년\" m\"월\" d\"일\"";
                    sheet.Cell(2, 1).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    sheet.Cell(2, 2).Style.NumberFormat.Format = "#,##0";
                    sheet.Cell(2, 1).Value = new DateTime(2000, 1, 1);
                    sheet.Cell(2, 2).Value = 999;
                    sheet.Cell(5, 2).Value = 888;
                    book.SaveAs(template);
                }
                var skipped = ResultExcel.WriteInto(template, output, Typed, new[] { new[] { "1", "A", "2026-11-01 00:00:00" }, new[] { "2", "B", null } });
                Assert.Equal(new[] { "CODE" }, skipped);
                using (var book = new XLWorkbook(output))
                {
                    Assert.Equal("_안내", book.Worksheet(1).Name);
                    var sheet = book.Worksheet("사원");
                    Assert.Equal(25, sheet.Column(1).Width, 1);
                    Assert.Equal(new DateTime(2026, 11, 1), sheet.Cell(2, 1).GetDateTime());
                    Assert.Equal(1, sheet.Cell(2, 2).GetDouble());
                    Assert.Equal(2, sheet.Cell(3, 2).GetDouble());
                    // 3행은 2행 서식을 이어 받는다
                    Assert.Equal("#,##0", sheet.Cell(3, 2).Style.NumberFormat.Format);
                    Assert.Equal(XLColor.LightYellow, sheet.Cell(3, 1).Style.Fill.BackgroundColor);
                    Assert.True(sheet.Cell(3, 1).IsEmpty());
                    Assert.True(sheet.Cell(5, 2).IsEmpty()); // 예전 값은 지웠다
                }
                // 서식 파일은 그대로
                using (var book = new XLWorkbook(template))
                    Assert.Equal(999, book.Worksheet("사원").Cell(2, 2).GetDouble());
            }
            finally
            {
                if (File.Exists(template)) File.Delete(template);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void ImportWithColumnsLeftOutOfTheFormat_LeavesThemEmpty()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("Data");
                    sheet.Cell(1, 1).Value = "code"; // 소문자·ID와 CREATED는 서식에서 뺐다
                    sheet.Cell(2, 1).Value = "X1";
                    book.SaveAs(path);
                }
                var row = Assert.Single(ResultExcel.Read(path, Typed, out var missing));
                Assert.Equal(new string[] { null, "X1", null }, row);
                Assert.Equal(new[] { "ID", "CREATED" }, missing);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void FormatsRegistry_MatchesTables_AndSerializes()
        {
            var all = new[]
            {
                new ExcelFormatEntry { Name = "모두", File = "a.xlsx", Table = "" },
                new ExcelFormatEntry { Name = "사원", File = "b.xlsx", Table = "scott.emp" },
                new ExcelFormatEntry { Name = "이름만", File = "c.xlsx", Table = "EMP" },
                new ExcelFormatEntry { Name = "다른", File = "d.xlsx", Table = "HR.EMP" }
            };
            var names = ExcelFormats.For("SCOTT", "EMP", all).Select(f => f.Name).ToArray();
            Assert.Equal(new[] { ExcelFormats.Default.Name, "모두", "사원", "이름만" }, names);
            Assert.True(ExcelFormats.Default.BuiltIn);
            var parsed = ExcelFormats.Parse(ExcelFormats.Serialize(all));
            Assert.Equal(all.Select(a => a.Name + "|" + a.File + "|" + a.Table), parsed.Select(a => a.Name + "|" + a.File + "|" + a.Table));
            Assert.Empty(ExcelFormats.Parse("{깨진"));
        }
    }
}
