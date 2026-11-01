using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    public class ResultExcelTests
    {
        /// <summary>컬럼명 행을 이름 정의 DBH_COLUMNS로 지정(가져오기·서식 채우기는 이 이름이 있는 파일만 읽는다).</summary>
        static void Name(IXLWorksheet sheet, int row, int first, int last)
            => sheet.Workbook.DefinedNames.Add(ResultExcel.HeaderName, sheet.Range(row, first, row, last));

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
                    Name(sheet, 1, 1, 3);
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
                    Name(sheet, 1, 1, 2);
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
                    Name(sheet, 1, 1, columns.Length);
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
                    // 기본 서식은 컬럼명 행을 이름 정의 DBH_COLUMNS(A1:B1)로 지정해 둔다 — 범위 밖 C열은 메모 열(읽지 않음)
                    Assert.Equal("Data!$A$1:$B$1", book.DefinedNames.Single(n => n.Name == ResultExcel.HeaderName).RefersTo);
                    sheet.Cell(1, 3).Value = "메모";
                    sheet.Cell(2, 3).Value = "아무 글";
                    book.Save();
                }
                Assert.Empty(ResultExcel.Read(path, columns));
                // 범위 안에 결과에 없는 컬럼명이 있으면 거부
                using (var book = new XLWorkbook(path))
                {
                    book.Worksheet(1).Cell(1, 2).Value = "EXTRA";
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
                    Name(sheet, 1, 1, 2);
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
                    Name(sheet, 1, 1, 1);
                    book.SaveAs(path);
                }
                var row = Assert.Single(ResultExcel.Read(path, Typed, out var missing));
                Assert.Equal(new string[] { null, "X1", null }, row);
                Assert.Equal(new[] { "ID", "CREATED" }, missing);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>
        /// 로고(그림)·제목·설명 행을 컬럼명 행 위에 둔 서식: 가져오기는 컬럼명 행을 찾아 그 아래만 읽고,
        /// 등록 서식 채우기는 그림·제목을 그대로 두고 컬럼명 행 아래에 값을 쓴다. 30행 안에 컬럼명 행이 없으면 거부.
        /// </summary>
        [Fact]
        public void LogoAndTitleAboveHeaderRow_AreKept_AndSkipped()
        {
            var template = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            try
            {
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("사원");
                    using (var stream = new MemoryStream(png))
                        sheet.AddPicture(stream).MoveTo(sheet.Cell(1, 1)).WithSize(60, 30); // 로고
                    sheet.Cell(1, 2).Value = "사원 명부";                              // 제목
                    sheet.Range(1, 2, 1, 3).Merge();
                    sheet.Cell(2, 2).Value = "작성: 인사팀 (2026-11)";                  // 설명
                    sheet.Cell(4, 1).Value = "CODE"; sheet.Cell(4, 2).Value = "ID";      // 컬럼명 행(4행)
                    sheet.Cell(5, 1).Value = "X1"; sheet.Cell(5, 2).Value = 7;
                    Name(sheet, 4, 1, 2);
                    book.SaveAs(template);
                }
                // 가져오기: 컬럼명 행(이름 정의 4행) 아래만
                var row = Assert.Single(ResultExcel.Read(template, Typed, out var missing));
                Assert.Equal(new string[] { "7", "X1", null }, row);
                Assert.Equal(new[] { "CREATED" }, missing);

                // 등록 서식 채우기: 그림·제목·설명은 그대로, 5행부터 값
                ResultExcel.WriteInto(template, output, Typed, new[] { new[] { "1", "A", null }, new[] { "2", "B", null } });
                using (var book = new XLWorkbook(output))
                {
                    var sheet = book.Worksheet("사원");
                    Assert.Single(sheet.Pictures);
                    Assert.Equal("사원 명부", sheet.Cell(1, 2).GetString());
                    Assert.Equal("작성: 인사팀 (2026-11)", sheet.Cell(2, 2).GetString());
                    Assert.Equal("CODE", sheet.Cell(4, 1).GetString());
                    Assert.Equal("A", sheet.Cell(5, 1).GetString());
                    Assert.Equal(2, sheet.Cell(6, 2).GetDouble());
                }
                Assert.Equal(new[] { new string[] { "1", "A", null }, new string[] { "2", "B", null } }, ResultExcel.Read(output, Typed));

                // 빈 서식 저장(값 없이): 그림·제목·컬럼명 행은 두고 데이터 칸만 비운다
                var blank = output + ".blank.xlsx";
                ResultExcel.WriteInto(output, blank, Typed, new List<string[]>());
                using (var book = new XLWorkbook(blank))
                {
                    var sheet = book.Worksheet("사원");
                    Assert.Single(sheet.Pictures);
                    Assert.Equal("사원 명부", sheet.Cell(1, 2).GetString());
                    Assert.Equal("CODE", sheet.Cell(4, 1).GetString());
                    Assert.True(sheet.Cell(5, 1).IsEmpty() && sheet.Cell(6, 2).IsEmpty());
                }
                Assert.Empty(ResultExcel.Read(blank, Typed));
                File.Delete(blank);

                // 이름 정의가 없으면 컬럼명 행을 짐작하지 않고 거부한다 — 설명 행의 "ID" 같은 글을 머리로 잡지 않게
                using (var book = new XLWorkbook())
                {
                    var sheet = book.AddWorksheet("Data");
                    sheet.Cell(1, 1).Value = "ID";                 // 설명 행에 우연히 컬럼명과 같은 글
                    sheet.Cell(3, 1).Value = "CODE"; sheet.Cell(3, 2).Value = "ID";
                    sheet.Cell(4, 1).Value = "X1"; sheet.Cell(4, 2).Value = 1;
                    book.SaveAs(output);
                }
                Assert.Contains(ResultExcel.HeaderName, Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(output, Typed)).Message);
                Assert.Throws<InvalidOperationException>(() => ResultExcel.Inspect(output));
            }
            finally
            {
                if (File.Exists(template)) File.Delete(template);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// 이름 정의 DBH_COLUMNS로 컬럼명 행을 지정한 서식: 그 범위의 첫 행이 컬럼명 행, 그 범위의 열만 데이터 열.
        /// 위쪽 30행을 넘는 곳·다른 시트에 있어도 되고, 범위 밖 열(메모·계산 열)은 가져오기·채우기 모두 건드리지 않는다.
        /// </summary>
        [Fact]
        public void NamedRange_DBH_COLUMNS_SetsHeaderRowAndDataColumns()
        {
            var template = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                using (var book = new XLWorkbook())
                {
                    book.AddWorksheet("표지").Cell(1, 1).Value = "표지 시트(첫 시트지만 데이터 시트 아님)";
                    var sheet = book.AddWorksheet("사원");
                    sheet.Cell(1, 1).Value = "ID";                          // 위에 있는 같은 글자(머리 아님)
                    sheet.Cell(40, 2).Value = "CODE"; sheet.Cell(40, 3).Value = "ID"; // 40행 B:C가 컬럼명 행
                    sheet.Cell(40, 4).Value = "비고";                         // 범위 밖 메모 열
                    sheet.Cell(41, 2).Value = "X1"; sheet.Cell(41, 3).Value = 7; sheet.Cell(41, 4).Value = "메모는 읽지 않음";
                    book.DefinedNames.Add(ResultExcel.HeaderName, sheet.Range(40, 2, 40, 3));
                    book.SaveAs(template);
                }
                ResultExcel.Inspect(template);
                var row = Assert.Single(ResultExcel.Read(template, Typed, out var missing));
                Assert.Equal(new string[] { "7", "X1", null }, row);
                Assert.Equal(new[] { "CREATED" }, missing);

                ResultExcel.WriteInto(template, output, Typed, new[] { new[] { "1", "A", null }, new[] { "2", "B", null } });
                using (var book = new XLWorkbook(output))
                {
                    var sheet = book.Worksheet("사원");
                    Assert.Equal("ID", sheet.Cell(1, 1).GetString());         // 위쪽 글은 그대로
                    Assert.Equal("A", sheet.Cell(41, 2).GetString());
                    Assert.Equal(2, sheet.Cell(42, 3).GetDouble());
                    Assert.Equal("메모는 읽지 않음", sheet.Cell(41, 4).GetString()); // 범위 밖 열은 지우지 않는다
                }

                // 범위 첫 행에 결과 컬럼명이 하나도 없으면 거부(이름이 잘못 걸림)
                using (var book = new XLWorkbook(template))
                {
                    book.DefinedNames.Delete(ResultExcel.HeaderName);
                    book.DefinedNames.Add(ResultExcel.HeaderName, book.Worksheet("사원").Range(41, 2, 41, 3));
                    book.Save();
                }
                Assert.Throws<InvalidOperationException>(() => ResultExcel.Read(template, Typed));
            }
            finally
            {
                if (File.Exists(template)) File.Delete(template);
                if (File.Exists(output)) File.Delete(output);
            }
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
