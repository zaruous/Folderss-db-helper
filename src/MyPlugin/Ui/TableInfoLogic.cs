using System;
using System.Collections.Generic;
using System.Globalization;

namespace MyPlugin
{
    /// <summary>F4 테이블 정보 창의 글(순수 로직, 테스트 대상).</summary>
    internal static class TableInfoText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// 이름 아래 한 줄: "행 수 1,000 (통계 2026-10-03 11:02) · 만든 날 2026-10-01 09:00 · 마지막 DDL 2026-10-03 11:05 · 동의어 PUBLIC.X로 찾음".
        /// 뷰는 행 수를 빼고, 통계가 없으면 "행 수 통계 없음".
        /// </summary>
        public static string Facts(TableDescription info)
        {
            var parts = new List<string>();
            if (!string.Equals(info.Type, "VIEW", StringComparison.Ordinal))
            {
                parts.Add(info.NumRows.HasValue
                    ? "행 수 " + info.NumRows.Value.ToString("N0", Inv) + (info.LastAnalyzed.HasValue ? " (통계 " + Time(info.LastAnalyzed.Value) + ")" : "")
                    : "행 수 통계 없음");
            }
            if (info.Created.HasValue)
                parts.Add("만든 날 " + Time(info.Created.Value));
            if (info.LastDdl.HasValue)
                parts.Add("마지막 DDL " + Time(info.LastDdl.Value));
            if (!string.IsNullOrEmpty(info.Via))
                parts.Add("동의어 " + info.Via + "로 찾음");
            parts.Add("열 " + info.Columns.Count.ToString(Inv) + "개");
            return string.Join(" · ", parts);
        }

        /// <summary>F4를 눌렀는데 이름을 고를 수 없을 때.</summary>
        public const string NoNameMessage = "테이블 이름 위에 커서를 두거나 이름(스키마.이름)을 선택한 뒤 F4를 누르세요.";

        public static string BusyMessage(string dbName)
        {
            return "'" + dbName + "'에서 실행 중이라 테이블 정보를 가져오지 않았습니다. 끝난 뒤 다시 F4를 누르세요.";
        }

        public static string LoadingMessage(string name)
        {
            return name + " 정보를 가져오는 중…";
        }

        public static string FailedMessage(string reason)
        {
            return "테이블 정보를 가져오지 못했습니다: " + reason;
        }

        private static string Time(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm", Inv);
        }
    }
}
