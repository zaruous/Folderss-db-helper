using System;

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

        public static string Format(object value) { throw new NotImplementedException(); }

        /// <summary>NUMBER, FLOAT, BINARY_FLOAT, BINARY_DOUBLE, INTEGER, DECIMAL 등(대소문자 무시)과 .NET 숫자 형식 이름(INT32, DECIMAL …)이면 true.</summary>
        public static bool IsNumericTypeName(string dataTypeName) { throw new NotImplementedException(); }

        /// <summary>
        /// 리더의 열 정보로 ResultColumn을 만든다. 형식 표시: NUMBER는 precision·scale이 있으면 "NUMBER(p,s)"/"NUMBER(p)", 없으면 "NUMBER";
        /// VARCHAR2·CHAR·NVARCHAR2·NCHAR·RAW는 size가 있으면 "(size)"; 그 밖은 dataTypeName 그대로(비어 있으면 fieldType.Name).
        /// IsNumeric은 IsNumericTypeName(dataTypeName) 또는 fieldType이 숫자 형식이면 true.
        /// </summary>
        public static ResultColumn DescribeColumn(string name, string dataTypeName, Type fieldType, int? size, int? precision, int? scale)
        {
            throw new NotImplementedException();
        }
    }
}
