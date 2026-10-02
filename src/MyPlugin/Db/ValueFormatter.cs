using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Types;

namespace MyPlugin
{
    /// <summary>결과 그리드 열 하나.</summary>
    public sealed class ResultColumn
    {
        public string Name { get; set; }
        /// <summary>열 머리에 보일 형식. 예: NUMBER(7,2), VARCHAR2(10), DATE</summary>
        public string TypeLabel { get; set; }
        /// <summary>숫자 열이면 오른쪽 정렬</summary>
        public bool IsNumeric { get; set; }
    }

    /// <summary>
    /// 조회 값 → 표시 문자열(순수 로직, 테스트 대상). null·DBNull·Oracle 형식의 IsNull은 null(그리드에서 NULL로 표시).
    /// - 숫자: 문화권과 무관하게(InvariantCulture), 정밀도를 잃지 않게. OracleDecimal은 ToString() 그대로(38자리).
    /// - 날짜: DateTime "yyyy-MM-dd HH:mm:ss", 초 미만이 있으면 소수점 뒤 끝의 0을 뺀 7자리 이내. DateTimeOffset·TIMESTAMP WITH TIME ZONE은 " +09:00"처럼 오프셋을 붙인다.
    /// - 문자열·CLOB: 최대 MaxTextLength자, 넘으면 잘라서 "…(전체 n자)"를 붙인다. OracleClob은 처음 MaxTextLength자만 읽는다.
    /// - 이진(byte[]·RAW·BLOB·OracleBinary): "0x" + 대문자 16진수 최대 MaxBinaryBytes바이트, 넘으면 "…(전체 n바이트)".
    /// - 그 밖: Convert.ToString(value, InvariantCulture).
    /// 값 객체를 Dispose하지 않는다(OracleClob 등의 정리는 호출자 몫).
    /// </summary>
    public static class ValueFormatter
    {
        public const int MaxTextLength = 4000;
        public const int MaxBinaryBytes = 2000;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static readonly HashSet<string> NumericTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Oracle·ANSI
            "NUMBER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "INTEGER", "INT", "SMALLINT", "DECIMAL", "DEC", "NUMERIC",
            "REAL", "DOUBLE PRECISION", "BINARY_INTEGER", "PLS_INTEGER", "SIMPLE_INTEGER", "NATURAL", "POSITIVE",
            // ODP.NET GetDataTypeName(OracleDbType 이름)
            "BINARYFLOAT", "BINARYDOUBLE",
            // .NET 형식 이름
            "BYTE", "SBYTE", "INT16", "INT32", "INT64", "UINT16", "UINT32", "UINT64", "SINGLE", "DOUBLE",
            // 그 밖 DB
            "BIGINT", "TINYINT", "MEDIUMINT", "MONEY", "SMALLMONEY"
        };

        // 크기를 괄호로 붙여 보이는 형식
        private static readonly HashSet<string> SizedTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "VARCHAR2", "CHAR", "NVARCHAR2", "NCHAR", "RAW"
        };

        public static string Format(object value)
        {
            if (value == null || value is DBNull)
                return null;
            if (value is INullable oracleNullable && oracleNullable.IsNull)
                return null;
            if (value is System.Data.SqlTypes.INullable sqlNullable && sqlNullable.IsNull)
                return null;

            switch (value)
            {
                case string text:
                    return Text(text);
                case byte[] bytes:
                    return Hex(bytes, bytes.Length, bytes.Length);
                case DateTime dateTime:
                    return DateTimeText(dateTime);
                case DateTimeOffset offsetTime:
                    return DateTimeText(offsetTime.DateTime) + " " + OffsetText(offsetTime.Offset);
                case OracleDecimal number:
                    return number.ToString();
                case OracleString oracleString:
                    return Text(oracleString.Value);
                case OracleDate date:
                    return DateText(date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second, 0);
                case OracleTimeStamp stamp:
                    return DateText(stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second, stamp.Nanosecond);
                case OracleTimeStampTZ stampTz:
                    return DateText(stampTz.Year, stampTz.Month, stampTz.Day, stampTz.Hour, stampTz.Minute, stampTz.Second, stampTz.Nanosecond)
                        + " " + ZoneText(stampTz);
                case OracleTimeStampLTZ stampLtz:
                    return DateText(stampLtz.Year, stampLtz.Month, stampLtz.Day, stampLtz.Hour, stampLtz.Minute, stampLtz.Second, stampLtz.Nanosecond);
                case OracleIntervalDS daySecond:
                    return IntervalText(daySecond);
                case OracleIntervalYM yearMonth:
                    return IntervalText(yearMonth);
                case OracleBinary binary:
                    var binaryBytes = binary.Value;
                    return Hex(binaryBytes, binaryBytes.Length, binaryBytes.Length);
                case OracleBoolean flag:
                    return flag.IsTrue ? "TRUE" : "FALSE";
                case OracleClob clob:
                    return ClobText(clob);
                case OracleBlob blob:
                    return BlobText(blob);
                case OracleBFile _:
                    // 파일 내용은 서버의 디렉터리 권한이 있어야 읽을 수 있어 자리만 표시한다
                    return "(BFILE)";
                case OracleRefCursor _:
                    // CURSOR(…) 식 결과. ToString()은 형식 이름뿐이다
                    return "(CURSOR)";
                case OracleXmlType xml:
                    return Text(xml.Value);
                default:
                    return Convert.ToString(value, Invariant);
            }
        }

        /// <summary>NUMBER, FLOAT, BINARY_FLOAT, BINARY_DOUBLE, INTEGER, DECIMAL 등(대소문자 무시)과 .NET 숫자 형식 이름(INT32, DECIMAL …)이면 true.</summary>
        public static bool IsNumericTypeName(string dataTypeName)
        {
            if (string.IsNullOrWhiteSpace(dataTypeName))
                return false;
            var name = dataTypeName.Trim();
            var paren = name.IndexOf('(');
            if (paren >= 0)
                name = name.Substring(0, paren).TrimEnd();
            if (name.StartsWith("System.", StringComparison.Ordinal))
                name = name.Substring("System.".Length);
            return NumericTypeNames.Contains(name);
        }

        /// <summary>
        /// 리더의 열 정보로 ResultColumn을 만든다. 형식 표시: NUMBER는 precision·scale이 있으면 "NUMBER(p,s)"/"NUMBER(p)", 없으면 "NUMBER";
        /// VARCHAR2·CHAR·NVARCHAR2·NCHAR·RAW는 size가 있으면 "(size)"; 그 밖은 dataTypeName 그대로(비어 있으면 fieldType.Name).
        /// IsNumeric은 IsNumericTypeName(dataTypeName) 또는 fieldType이 숫자 형식이면 true.
        /// </summary>
        public static ResultColumn DescribeColumn(string name, string dataTypeName, Type fieldType, int? size, int? precision, int? scale)
        {
            var typeName = (dataTypeName ?? "").Trim();
            var upper = typeName.ToUpperInvariant();
            string label;
            if (upper == "NUMBER")
                label = NumberLabel(precision, scale);
            else if (SizedTypeNames.Contains(upper))
                label = size.HasValue && size.Value > 0 ? upper + "(" + size.Value.ToString(Invariant) + ")" : upper;
            else if (typeName.Length > 0)
                label = typeName;
            else
                label = fieldType != null ? fieldType.Name : "";

            return new ResultColumn
            {
                Name = name ?? "",
                TypeLabel = label,
                IsNumeric = IsNumericTypeName(typeName) || IsNumericType(fieldType)
            };
        }

        private static string NumberLabel(int? precision, int? scale)
        {
            var hasScale = scale.HasValue && scale.Value != 0;
            if (precision.HasValue && precision.Value > 0)
            {
                var p = precision.Value.ToString(Invariant);
                return hasScale ? "NUMBER(" + p + "," + scale.Value.ToString(Invariant) + ")" : "NUMBER(" + p + ")";
            }
            // NUMBER(*,2)처럼 정밀도 없이 소수 자릿수만 정한 열
            return hasScale ? "NUMBER(*," + scale.Value.ToString(Invariant) + ")" : "NUMBER";
        }

        private static bool IsNumericType(Type type)
        {
            if (type == null)
                return false;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum)
                return false;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
                default:
                    return false;
            }
        }

        private static string Text(string text)
        {
            if (text.Length <= MaxTextLength)
                return text;
            return Truncate(text, MaxTextLength) + TextSuffix(text.Length);
        }

        private static string Truncate(string text, int length)
        {
            if (length > text.Length)
                length = text.Length;
            // 서로게이트 쌍의 앞 절반에서 자르면 깨진 글자가 남는다
            if (length > 0 && char.IsHighSurrogate(text[length - 1]))
                length--;
            return text.Substring(0, length);
        }

        private static string TextSuffix(long totalChars)
        {
            return "…(전체 " + totalChars.ToString(Invariant) + "자)";
        }

        private static string Hex(byte[] bytes, int available, long total)
        {
            var count = Math.Min(available, MaxBinaryBytes);
            var text = "0x" + Convert.ToHexString(bytes, 0, count);
            if (total > MaxBinaryBytes)
                text += "…(전체 " + total.ToString(Invariant) + "바이트)";
            return text;
        }

        private static string ClobText(OracleClob clob)
        {
            // Length는 바이트(UCS-2) 단위라 글자 수는 절반이다
            var totalChars = clob.Length / 2;
            var want = (int)Math.Min(totalChars, MaxTextLength);
            var buffer = new char[want];
            var read = 0;
            while (read < want)
            {
                var n = clob.Read(buffer, read, want - read);
                if (n <= 0)
                    break;
                read += n;
            }
            var text = new string(buffer, 0, read);
            if (totalChars <= MaxTextLength)
                return text;
            return Truncate(text, read) + TextSuffix(totalChars);
        }

        private static string BlobText(OracleBlob blob)
        {
            var total = blob.Length;
            var want = (int)Math.Min(total, MaxBinaryBytes);
            var buffer = new byte[want];
            var read = 0;
            while (read < want)
            {
                var n = blob.Read(buffer, read, want - read);
                if (n <= 0)
                    break;
                read += n;
            }
            return Hex(buffer, read, total);
        }

        private static string DateTimeText(DateTime value)
        {
            var text = value.ToString("yyyy-MM-dd HH:mm:ss", Invariant);
            var ticks = value.Ticks % TimeSpan.TicksPerSecond;
            if (ticks == 0)
                return text;
            return text + "." + ticks.ToString("0000000", Invariant).TrimEnd('0');
        }

        /// <summary>Oracle 날짜·시각 형식(기원전 연도·나노초까지)을 DateTime을 거치지 않고 그대로 쓴다.</summary>
        private static string DateText(int year, int month, int day, int hour, int minute, int second, int nanosecond)
        {
            var builder = new StringBuilder(32);
            if (year < 0)
                builder.Append('-');
            builder.Append(Math.Abs(year).ToString("0000", Invariant)).Append('-')
                   .Append(month.ToString("00", Invariant)).Append('-')
                   .Append(day.ToString("00", Invariant)).Append(' ')
                   .Append(hour.ToString("00", Invariant)).Append(':')
                   .Append(minute.ToString("00", Invariant)).Append(':')
                   .Append(second.ToString("00", Invariant));
            AppendNanoseconds(builder, nanosecond);
            return builder.ToString();
        }

        private static void AppendNanoseconds(StringBuilder builder, int nanosecond)
        {
            nanosecond = Math.Abs(nanosecond);
            if (nanosecond > 0)
                builder.Append('.').Append(nanosecond.ToString("000000000", Invariant).TrimEnd('0'));
        }

        private static string OffsetText(TimeSpan offset)
        {
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var abs = offset.Duration();
            return sign + abs.Hours.ToString("00", Invariant) + ":" + abs.Minutes.ToString("00", Invariant);
        }

        private static string ZoneText(OracleTimeStampTZ value)
        {
            try
            {
                return OffsetText(value.GetTimeZoneOffset());
            }
            catch (Exception)
            {
                // 지역 이름(Asia/Seoul 등)을 오프셋으로 바꾸지 못하면 이름을 그대로 보인다
                return value.TimeZone;
            }
        }

        /// <summary>INTERVAL DAY TO SECOND를 Oracle 표기처럼 "+1 02:03:04.5"로. 구성 요소는 모두 같은 부호다.</summary>
        private static string IntervalText(OracleIntervalDS value)
        {
            var negative = value.Days < 0 || value.Hours < 0 || value.Minutes < 0 || value.Seconds < 0 || value.Nanoseconds < 0;
            var builder = new StringBuilder(32);
            builder.Append(negative ? '-' : '+')
                   .Append(Math.Abs(value.Days).ToString(Invariant)).Append(' ')
                   .Append(Math.Abs(value.Hours).ToString("00", Invariant)).Append(':')
                   .Append(Math.Abs(value.Minutes).ToString("00", Invariant)).Append(':')
                   .Append(Math.Abs(value.Seconds).ToString("00", Invariant));
            AppendNanoseconds(builder, value.Nanoseconds);
            return builder.ToString();
        }

        /// <summary>INTERVAL YEAR TO MONTH를 Oracle 표기처럼 "+1-02"로.</summary>
        private static string IntervalText(OracleIntervalYM value)
        {
            var negative = value.Years < 0 || value.Months < 0;
            return (negative ? "-" : "+") + Math.Abs(value.Years).ToString(Invariant) + "-" + Math.Abs(value.Months).ToString("00", Invariant);
        }
    }
}
