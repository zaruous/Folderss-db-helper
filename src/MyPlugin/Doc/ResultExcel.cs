using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 조회 결과 ↔ 엑셀(.xlsx). 규칙은 하나다 — 데이터 시트(이름이 '_'로 시작하지 않는 첫 시트)의 첫 행이 DB 컬럼명이고, 그 아래가 데이터다.
    /// 열 순서·서식·너비·색·다른 시트('_규칙' 등)는 자유다. 그래서 등록한 엑셀 서식(설정 창 "엑셀 서식")으로 내보낸 파일을 고쳐 그대로 가져올 수 있다.
    /// - 내보내기: 기본 서식(새 파일) 또는 등록 서식 파일에 채우기(그 파일의 열 순서·서식은 그대로, 첫 행 컬럼명에 맞춰 값만).
    /// - 서식 만들기: 컬럼명 머리 + 자료형별 열 서식(문자 @, 숫자, 날짜 yyyy-mm-dd hh:mm:ss) + '_규칙' 안내 시트.
    /// - 가져오기: 첫 행 컬럼명으로 맞춘다(대소문자 무시). 서식에서 뺀 열은 비워 둔다(INSERT에서 빠져 DB 기본값).
    ///   결과에 없는 열·중복 열·수식·오류 셀은 거부.
    ///   날짜·숫자 셀은 그리드 표시 형식(PC 지역 설정과 무관)으로 읽는다.
    /// </summary>
    internal static class ResultExcel
    {
        private const double MaxColumnWidth = 60.0;
        public const string DateFormat = "yyyy-mm-dd hh:mm:ss";
        public const string RulesSheet = "_규칙";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] GridDateFormats =
        {
            "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"
        };

        // ───────── 쓰기 ─────────

        /// <summary>기본 서식으로 새 파일: 컬럼명 머리(굵게·틀 고정) + 자료형별 열 서식 + 값(숫자·날짜는 엑셀 숫자·날짜로, 그 밖은 텍스트).</summary>
        internal static void Write(string path, IList<ResultColumn> columns, IEnumerable<string[]> rows)
        {
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Data");
                var kinds = Kinds(columns);
                Header(sheet, columns, kinds);
                var map = Enumerable.Range(1, columns.Count).ToArray();
                Fill(sheet, map, kinds, rows, null);
                Fit(sheet, columns.Count);
                book.SaveAs(path);
            }
        }

        /// <summary>서식 파일(값 없음): 컬럼명 머리 + 자료형별 열 서식 + '_규칙' 시트. 사람이 엑셀에서 값을 넣어 [엑셀 임포트]한다.</summary>
        internal static void WriteTemplate(string path, IList<ResultColumn> columns, string target)
        {
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Data");
                var kinds = Kinds(columns);
                Header(sheet, columns, kinds);
                Fit(sheet, columns.Count);
                for (int c = 1; c <= columns.Count; c++)
                    sheet.Column(c).Width = Math.Max(sheet.Column(c).Width, 14);
                Rules(book.AddWorksheet(RulesSheet), columns, kinds, target);
                book.SaveAs(path);
            }
        }

        /// <summary>
        /// 등록 서식 파일에 채워 outPath로 저장한다(서식 파일 자체는 바꾸지 않음). 데이터 시트의 첫 행 컬럼명에 맞춰 값만 쓰고,
        /// 2행 아래의 이전 값은 지운다(서식은 둠). 3행부터는 2행 셀 서식을 따른다. 서식에 없는 결과 열은 쓰지 않고 이름을 돌려준다.
        /// </summary>
        internal static List<string> WriteInto(string templatePath, string outPath, IList<ResultColumn> columns, IEnumerable<string[]> rows)
        {
            using (var book = new XLWorkbook(templatePath))
            {
                var sheet = DataSheet(book);
                var headers = Headers(sheet);
                var kinds = Kinds(columns);
                var map = new int[columns.Count];
                var skipped = new List<string>();
                for (int i = 0; i < columns.Count; i++)
                {
                    var at = headers.FindIndex(h => string.Equals(h, columns[i].Name, StringComparison.OrdinalIgnoreCase));
                    map[i] = at < 0 ? 0 : at + 1;
                    if (at < 0) skipped.Add(columns[i].Name);
                }
                if (map.All(m => m == 0))
                    throw new InvalidOperationException("서식 파일 첫 행에 이 결과의 컬럼명이 하나도 없습니다(데이터 시트: " + sheet.Name + ").");
                // 이전 값 지우기(서식은 둠)
                var used = sheet.LastRowUsed();
                if (used != null && used.RowNumber() >= 2)
                    sheet.Range(2, 1, used.RowNumber(), Math.Max(headers.Count, 1)).Clear(XLClearOptions.Contents);
                Fill(sheet, map, kinds, rows, 2);
                book.SaveAs(outPath);
                return skipped;
            }
        }

        static void Header(IXLWorksheet sheet, IList<ResultColumn> columns, ColumnValueKind[] kinds)
        {
            for (int c = 0; c < columns.Count; c++)
            {
                // 열 서식: 문자(@)는 "00123"·"1-2"를 그대로 두고, 숫자·날짜는 엑셀에서 계산·정렬되게
                sheet.Column(c + 1).Style.NumberFormat.Format = FormatOf(kinds[c]);
                var cell = sheet.Cell(1, c + 1);
                cell.Style.NumberFormat.Format = "@";
                cell.Value = columns[c].Name;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
            }
            sheet.SheetView.FreezeRows(1);
        }

        static string FormatOf(ColumnValueKind kind)
        {
            switch (kind)
            {
                case ColumnValueKind.Number: return "General";
                case ColumnValueKind.DateTime: return DateFormat;
                default: return "@";
            }
        }

        /// <summary>
        /// 값 쓰기. 셀(또는 열) 서식이 텍스트(@)면 글자로, 아니면 숫자 열은 엑셀 숫자(15자리를 넘으면 글자 — 엑셀 정밀도), 날짜 열은 엑셀 날짜로.
        /// styleRow가 있으면 그 행의 셀 서식을 다음 행들에 이어 쓴다(등록 서식의 2행 꾸밈).
        /// </summary>
        static void Fill(IXLWorksheet sheet, int[] map, ColumnValueKind[] kinds, IEnumerable<string[]> rows, int? styleRow)
        {
            int r = 2;
            foreach (var row in rows)
            {
                for (int c = 0; c < map.Length; c++)
                {
                    if (map[c] == 0) continue;
                    var cell = sheet.Cell(r, map[c]);
                    if (styleRow.HasValue && r != styleRow.Value)
                        cell.Style = sheet.Cell(styleRow.Value, map[c]).Style;
                    var text = c < row.Length ? row[c] : null;
                    if (text == null) continue;
                    if (cell.Style.NumberFormat.Format == "@") { cell.Value = text; continue; }
                    if (kinds[c] == ColumnValueKind.Number && decimal.TryParse(text, NumberStyles.Float, Inv, out var number) && Significant(text) <= 15)
                        cell.Value = (double)number;
                    else if (kinds[c] == ColumnValueKind.DateTime && DateTime.TryParseExact(text.Trim(), GridDateFormats, Inv, DateTimeStyles.None, out var date))
                    {
                        cell.Value = date;
                        if (string.IsNullOrEmpty(cell.Style.NumberFormat.Format) || cell.Style.NumberFormat.Format == "General")
                            cell.Style.NumberFormat.Format = DateFormat;
                    }
                    else
                    {
                        cell.Style.NumberFormat.Format = "@";
                        cell.Value = text;
                    }
                }
                r++;
            }
        }

        static int Significant(string number) => number.TrimStart('-', '+', '0').Replace(".", "").TrimEnd('0').Length;

        static void Fit(IXLWorksheet sheet, int count)
        {
            if (count == 0) return;
            sheet.Columns(1, count).AdjustToContents();
            foreach (var col in sheet.Columns(1, count))
                if (col.Width > MaxColumnWidth)
                    col.Width = MaxColumnWidth;
        }

        static void Rules(IXLWorksheet sheet, IList<ResultColumn> columns, ColumnValueKind[] kinds, string target)
        {
            var lines = new List<string>
            {
                "엑셀 서식 규칙 (DB Helper 결과 편집 [엑셀 임포트]·[엑셀 내보내기])",
                "대상: " + (string.IsNullOrWhiteSpace(target) ? "(모든 결과)" : target),
                "",
                "1. 데이터는 이름이 '_'로 시작하지 않는 첫 시트에 둡니다. 첫 행은 DB 컬럼명이고 그 아래가 데이터입니다.",
                "2. 열 순서·너비·색·글꼴은 바꿔도 됩니다. 컬럼명(첫 행)은 바꾸지 마세요(대소문자는 무시).",
                "3. 결과에 없는 컬럼명이 있거나 같은 컬럼명이 두 번 있으면 가져오지 않습니다. 열을 빼면 그 열은 비워 두어 DB 기본값이 됩니다.",
                "4. 수식·오류 값(#N/A 등)은 가져오지 않습니다. 값을 붙여 넣으세요. 빈 셀은 NULL입니다.",
                "5. 날짜는 엑셀 날짜 또는 'yyyy-MM-dd HH:mm:ss' 글자, 숫자는 엑셀 숫자 또는 '.' 소수점 글자로 넣습니다.",
                "6. 이름이 '_'로 시작하는 시트(이 시트 등)는 읽지 않습니다.",
                "",
                "컬럼\t형식\t엑셀 열 서식"
            };
            for (int c = 0; c < columns.Count; c++)
                lines.Add(columns[c].Name + "\t" + (columns[c].TypeLabel ?? "") + "\t" + FormatOf(kinds[c]));
            for (int i = 0; i < lines.Count; i++)
            {
                var parts = lines[i].Split('\t');
                for (int p = 0; p < parts.Length; p++)
                {
                    var cell = sheet.Cell(i + 1, p + 1);
                    cell.Style.NumberFormat.Format = "@";
                    cell.Value = parts[p];
                }
            }
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Column(1).Width = 30;
            sheet.Column(2).Width = 22;
            sheet.Column(3).Width = 22;
        }

        static ColumnValueKind[] Kinds(IList<ResultColumn> columns) => columns.Select(c => RowEditLogic.KindOf(c.TypeLabel)).ToArray();

        // ───────── 읽기 ─────────

        /// <summary>데이터 시트의 행들을 결과 열 순서의 값 배열로(값이 하나도 없는 행은 뺀다).</summary>
        internal static List<string[]> Read(string path, IList<ResultColumn> columns) => Read(path, columns, out _);

        /// <summary>
        /// 데이터 시트의 행들을 결과 열 순서의 값 배열로(값이 하나도 없는 행은 뺀다). 서식에 없는 결과 열은 null(INSERT에서 빠져 DB 기본값)이고
        /// 그 이름을 missing으로 돌려준다. 첫 행에 결과 열이 하나도 없으면 거부한다.
        /// </summary>
        internal static List<string[]> Read(string path, IList<ResultColumn> columns, out List<string> missing)
        {
            using (var book = new XLWorkbook(path))
            {
                var sheet = DataSheet(book);
                var used = sheet.RangeUsed();
                if (used == null)
                    throw new InvalidOperationException("엑셀 파일에 데이터가 없습니다.");
                var headers = Headers(sheet);

                // 알 수 없는 열은 거부한다(오타·다른 테이블 서식을 조용히 무시하지 않게). 머리가 빈 열은 본다고 치지 않는다.
                var unknown = headers.Where(h => h.Length > 0 && !columns.Any(c => string.Equals(c.Name, h, StringComparison.OrdinalIgnoreCase))).ToList();
                if (unknown.Count > 0)
                    throw new InvalidOperationException("엑셀 첫 행에 이 결과에 없는 컬럼이 있습니다: " + string.Join(", ", unknown));

                missing = columns.Where(c => !headers.Any(h => string.Equals(h, c.Name, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name).ToList();
                if (missing.Count == columns.Count)
                    throw new InvalidOperationException("엑셀 첫 행에 이 결과의 컬럼명이 하나도 없습니다(데이터 시트: " + sheet.Name + ").");

                var map = new int[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    var name = columns[i].Name;
                    var at = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0 && headers.FindLastIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase)) != at)
                        throw new InvalidOperationException("엑셀 첫 행에 컬럼명이 중복되었습니다: " + name);
                    map[i] = at + 1; // 0 = 서식에 없는 열
                }

                var rows = new List<string[]>();
                int lastRow = used.LastRow().RowNumber();
                for (int r = 2; r <= lastRow; r++)
                {
                    var row = new string[columns.Count];
                    bool hasValue = false;
                    for (int c = 0; c < columns.Count; c++)
                    {
                        if (map[c] == 0)
                            continue;
                        var cell = sheet.Cell(r, map[c]);
                        if (cell.HasFormula)
                            throw new InvalidOperationException(string.Format(Inv, "수식 셀은 임포트할 수 없습니다 ({0}행, {1}열).", r, columns[c].Name));
                        var text = CellString(cell);
                        if (!string.IsNullOrEmpty(text))
                        {
                            row[c] = text;
                            hasValue = true;
                        }
                    }
                    if (hasValue)
                        rows.Add(row);
                }
                return rows;
            }
        }

        /// <summary>등록할 서식 파일인지 확인한다: 데이터 시트와 첫 행 컬럼명이 있어야 한다. 그 컬럼명들을 돌려준다.</summary>
        internal static List<string> Inspect(string path)
        {
            using (var book = new XLWorkbook(path))
            {
                var headers = Headers(DataSheet(book)).Where(h => h.Length > 0).ToList();
                if (headers.Count == 0)
                    throw new InvalidOperationException("데이터 시트 첫 행에 컬럼명이 없습니다.");
                return headers;
            }
        }

        static IXLWorksheet DataSheet(XLWorkbook book)
        {
            var sheet = book.Worksheets.FirstOrDefault(s => !s.Name.StartsWith("_", StringComparison.Ordinal));
            if (sheet == null)
                throw new InvalidOperationException("데이터 시트가 없습니다(이름이 '_'로 시작하지 않는 시트).");
            return sheet;
        }

        static List<string> Headers(IXLWorksheet sheet)
        {
            var last = sheet.Row(1).LastCellUsed();
            var count = last == null ? 0 : last.Address.ColumnNumber;
            var headers = new List<string>();
            for (int c = 1; c <= count; c++)
                headers.Add(CellString(sheet.Cell(1, c))?.Trim() ?? "");
            return headers;
        }

        /// <summary>
        /// 셀 값을 그리드 표시 형식(저장 검사·INSERT가 읽는 형식)으로: 글자는 그대로, 날짜 "yyyy-MM-dd HH:mm:ss[.fffffff]", 시각 "HH:mm:ss",
        /// 숫자는 '.' 소수점·지수 없이(PC 지역 설정과 무관), 참거짓은 1/0. 오류 셀(#N/A 등)은 거부한다.
        /// </summary>
        private static string CellString(IXLCell cell)
        {
            if (cell.IsEmpty())
                return null;
            switch (cell.DataType)
            {
                case XLDataType.Text:
                    return cell.GetString();
                case XLDataType.DateTime:
                    var date = cell.GetDateTime();
                    return date.ToString(date.Ticks % TimeSpan.TicksPerSecond != 0 ? "yyyy-MM-dd HH:mm:ss.FFFFFFF" : "yyyy-MM-dd HH:mm:ss", Inv);
                case XLDataType.TimeSpan:
                    return cell.GetTimeSpan().ToString(@"hh\:mm\:ss", Inv);
                case XLDataType.Number:
                    var number = cell.GetDouble();
                    // 엑셀 숫자는 double — decimal로 바꿔 지수 표기 없이 쓴다(범위를 넘으면 왕복 가능한 double 표기)
                    if (Math.Abs(number) < 7.9e28)
                        return ((decimal)number).ToString(Inv);
                    return number.ToString("R", Inv);
                case XLDataType.Boolean:
                    return cell.GetBoolean() ? "1" : "0";
                case XLDataType.Error:
                    throw new InvalidOperationException("오류 값이 든 셀은 가져올 수 없습니다(" + cell.Address.ToStringRelative() + ").");
                default:
                    return cell.Value.ToString();
            }
        }
    }
}
