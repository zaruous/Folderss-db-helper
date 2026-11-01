using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;

namespace MyPlugin
{
    /// <summary>
    /// 조회 결과 그리드 데이터를 엑셀(.xlsx) 파일로 내보내거나 가져온다.
    /// ClosedXML 라이브러리를 사용하며 모든 셀을 텍스트 형식으로 다루어 0으로 시작하는 코드나 날짜가 왜곡되지 않게 한다.
    /// </summary>
    internal static class ResultExcel
    {
        private const double MaxColumnWidth = 60.0;

        /// <summary>
        /// 조회 결과 컬럼과 행 목록을 엑셀 파일로 저장한다.
        /// rows가 비어 있으면 헤더만 있는 템플릿 파일이 생성된다.
        /// </summary>
        internal static void Write(string path, IList<ResultColumn> columns, IEnumerable<string[]> rows)
        {
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Data");

                // 첫 행: 컬럼 헤더
                for (int c = 0; c < columns.Count; c++)
                {
                    var cell = sheet.Cell(1, c + 1);
                    cell.Value = columns[c].Name;
                    cell.Style.Font.Bold = true;
                }

                // 데이터 행
                int rowNumber = 2;
                foreach (var row in rows)
                {
                    for (int c = 0; c < columns.Count; c++)
                    {
                        if (c < row.Length && row[c] != null)
                        {
                            var cell = sheet.Cell(rowNumber, c + 1);
                            cell.Style.NumberFormat.Format = "@";
                            cell.Value = row[c];
                        }
                    }
                    rowNumber++;
                }

                sheet.SheetView.FreezeRows(1);
                sheet.Columns().AdjustToContents();

                // 너무 넓어진 컬럼은 보기 좋게 너비를 제한한다
                foreach (var col in sheet.Columns(1, columns.Count))
                {
                    if (col.Width > MaxColumnWidth)
                        col.Width = MaxColumnWidth;
                }

                book.SaveAs(path);
            }
        }

        /// <summary>
        /// 엑셀 파일에서 데이터를 읽어와 각 행의 컬럼 값 배열 목록으로 반환한다.
        /// 첫 행의 컬럼명을 기준으로 대상 컬럼 위치를 매핑한다.
        /// </summary>
        internal static List<string[]> Read(string path, IList<ResultColumn> columns)
        {
            using (var book = new XLWorkbook(path))
            {
                var sheet = book.Worksheet(1);
                var used = sheet.RangeUsed();
                if (used == null)
                    throw new InvalidOperationException("엑셀 파일에 데이터가 없습니다.");

                int lastCol = used.LastColumn().ColumnNumber();
                var headers = new List<string>();
                for (int c = 1; c <= lastCol; c++)
                    headers.Add(CellString(sheet.Cell(1, c))?.Trim() ?? "");

                // 필수 컬럼 확인
                var missing = columns.Where(c => !headers.Any(h => string.Equals(h, c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0)
                {
                    throw new InvalidOperationException("엑셀 첫 행에 필요한 컬럼이 없습니다: "
                        + string.Join(", ", missing.Select(m => m.Name)));
                }

                // 컬럼별 엑셀 열 인덱스(1-based) 매핑
                var map = new int[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    var targetName = columns[i].Name;
                    var matchIndex = headers.FindIndex(h => string.Equals(h, targetName, StringComparison.OrdinalIgnoreCase));
                    if (matchIndex < 0)
                        throw new InvalidOperationException("컬럼을 찾을 수 없습니다: " + targetName);

                    // 중복 여부 확인
                    if (headers.FindLastIndex(h => string.Equals(h, targetName, StringComparison.OrdinalIgnoreCase)) != matchIndex)
                        throw new InvalidOperationException("엑셀 첫 행에 컬럼명이 중복되었습니다: " + targetName);

                    map[i] = matchIndex + 1;
                }

                var rows = new List<string[]>();
                int lastRow = used.LastRow().RowNumber();
                for (int r = 2; r <= lastRow; r++)
                {
                    var row = new string[columns.Count];
                    bool hasValue = false;
                    for (int c = 0; c < columns.Count; c++)
                    {
                        var cell = sheet.Cell(r, map[c]);
                        if (cell.HasFormula)
                            throw new InvalidOperationException(string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "수식 셀은 임포트할 수 없습니다 ({0}행, {1}열).", r, columns[c].Name));

                        if (!cell.IsEmpty())
                        {
                            var text = CellString(cell);
                            if (!string.IsNullOrEmpty(text))
                            {
                                row[c] = text;
                                hasValue = true;
                            }
                        }
                    }

                    if (hasValue)
                        rows.Add(row);
                }

                return rows;
            }
        }

        private static string CellString(IXLCell cell)
        {
            if (cell.IsEmpty())
                return null;

            if (cell.DataType == XLDataType.Text)
                return cell.GetString();

            return cell.Value.ToString();
        }
    }
}
