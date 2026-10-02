using System;
using System.Data.SqlTypes;
using System.Globalization;
using MyPlugin;
using Oracle.ManagedDataAccess.Types;
using Xunit;

namespace MyPlugin.Tests
{
    public class ValueFormatterTests
    {
        [Fact]
        public void Format_NullAndOracleNulls_ReturnNull()
        {
            Assert.Null(ValueFormatter.Format(null));
            Assert.Null(ValueFormatter.Format(DBNull.Value));
            Assert.Null(ValueFormatter.Format(OracleDecimal.Null));
            Assert.Null(ValueFormatter.Format(OracleString.Null));
            Assert.Null(ValueFormatter.Format(OracleDate.Null));
            Assert.Null(ValueFormatter.Format(OracleTimeStamp.Null));
            Assert.Null(ValueFormatter.Format(OracleTimeStampTZ.Null));
            Assert.Null(ValueFormatter.Format(OracleBinary.Null));
            Assert.Null(ValueFormatter.Format(OracleClob.Null));
            Assert.Null(ValueFormatter.Format(OracleBlob.Null));
            Assert.Null(ValueFormatter.Format(SqlInt32.Null));
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(int.MinValue, "-2147483648")]
        [InlineData(int.MaxValue, "2147483647")]
        [InlineData(long.MinValue, "-9223372036854775808")]
        [InlineData(long.MaxValue, "9223372036854775807")]
        [InlineData(ulong.MaxValue, "18446744073709551615")]
        [InlineData((short)-5, "-5")]
        [InlineData((byte)255, "255")]
        public void Format_Integers_AreExact(object value, string expected)
        {
            Assert.Equal(expected, ValueFormatter.Format(value));
        }

        [Fact]
        public void Format_Decimal_KeepsAllDigitsAndScale()
        {
            Assert.Equal("79228162514264337593543950335", ValueFormatter.Format(decimal.MaxValue));
            Assert.Equal("-0.0000000001", ValueFormatter.Format(-0.0000000001m));
            Assert.Equal("1.50", ValueFormatter.Format(1.50m));
        }

        [Theory]
        [InlineData("12345678901234567890123456789012345678")]
        [InlineData("-1234567890123456789012345678.9012345678")]
        [InlineData("0.12345678901234567890123456789012345678")]
        public void Format_OracleDecimal_38Digits_IsExact(string digits)
        {
            Assert.Equal(digits, ValueFormatter.Format(OracleDecimal.Parse(digits)));
        }

        [Fact]
        public void Format_UnderCommaDecimalCulture_StillUsesInvariantFormat()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CommaCulture();
            try
            {
                Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
                Assert.Equal("1.5", ValueFormatter.Format(1.5));
                Assert.Equal("0.30000000000000004", ValueFormatter.Format(0.1 + 0.2));
                Assert.Equal("1.25", ValueFormatter.Format(1.25f));
                Assert.Equal("-1234567.891", ValueFormatter.Format(-1234567.891m));
                Assert.Equal("1.5", ValueFormatter.Format(new OracleDecimal(1.5m)));
                Assert.Equal("2024-03-05 06:07:08.25", ValueFormatter.Format(new DateTime(2024, 3, 5, 6, 7, 8, 250)));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Format_DateTimeWithoutFraction_HasNoDecimalPoint()
        {
            Assert.Equal("2024-03-05 06:07:08", ValueFormatter.Format(new DateTime(2024, 3, 5, 6, 7, 8)));
            Assert.Equal("0001-01-01 00:00:00", ValueFormatter.Format(DateTime.MinValue));
        }

        [Fact]
        public void Format_DateTimeWithFraction_TrimsTrailingZerosUpToSevenDigits()
        {
            var baseTime = new DateTime(2024, 3, 5, 6, 7, 8);
            Assert.Equal("2024-03-05 06:07:08.12", ValueFormatter.Format(baseTime.AddMilliseconds(120)));
            Assert.Equal("2024-03-05 06:07:08.5", ValueFormatter.Format(baseTime.AddMilliseconds(500)));
            Assert.Equal("2024-03-05 06:07:08.0000001", ValueFormatter.Format(baseTime.AddTicks(1)));
            Assert.Equal("9999-12-31 23:59:59.9999999", ValueFormatter.Format(DateTime.MaxValue));
        }

        [Fact]
        public void Format_DateTimeOffset_AppendsOffset()
        {
            Assert.Equal("2024-03-05 06:07:08 +09:00", ValueFormatter.Format(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.FromHours(9))));
            Assert.Equal("2024-03-05 06:07:08.5 -05:30", ValueFormatter.Format(new DateTimeOffset(2024, 3, 5, 6, 7, 8, 500, new TimeSpan(-5, -30, 0))));
            Assert.Equal("2024-03-05 06:07:08 +00:00", ValueFormatter.Format(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero)));
        }

        [Fact]
        public void Format_TimeSpan_UsesInvariantConversion()
        {
            Assert.Equal("01:02:03", ValueFormatter.Format(new TimeSpan(1, 2, 3)));
            Assert.Equal("1.02:03:04.5000000", ValueFormatter.Format(new TimeSpan(1, 2, 3, 4, 500)));
            Assert.Equal("-00:00:01", ValueFormatter.Format(TimeSpan.FromSeconds(-1)));
        }

        [Fact]
        public void Format_OtherValues_UseInvariantConversion()
        {
            Assert.Equal("True", ValueFormatter.Format(true));
            Assert.Equal("x", ValueFormatter.Format('x'));
            Assert.Equal("00000000-0000-0000-0000-000000000000", ValueFormatter.Format(Guid.Empty));
        }

        [Fact]
        public void Format_OracleDate_UsesSameFormatAsDateTime()
        {
            Assert.Equal("2024-03-05 06:07:08", ValueFormatter.Format(new OracleDate(2024, 3, 5, 6, 7, 8)));
            Assert.Equal("2024-12-31 00:00:00", ValueFormatter.Format(new OracleDate(2024, 12, 31)));
        }

        [Fact]
        public void Format_OracleTimeStamp_KeepsNanosecondsWithoutTrailingZeros()
        {
            Assert.Equal("2024-03-05 06:07:08.123456789", ValueFormatter.Format(new OracleTimeStamp(2024, 3, 5, 6, 7, 8, 123456789)));
            Assert.Equal("2024-03-05 06:07:08.12", ValueFormatter.Format(new OracleTimeStamp(2024, 3, 5, 6, 7, 8, 120000000)));
            Assert.Equal("2024-03-05 06:07:08", ValueFormatter.Format(new OracleTimeStamp(2024, 3, 5, 6, 7, 8, 0)));
        }

        [Fact]
        public void Format_OracleTimeStampTZ_AppendsOffset()
        {
            Assert.Equal("2024-03-05 06:07:08.5 +09:00", ValueFormatter.Format(new OracleTimeStampTZ(2024, 3, 5, 6, 7, 8, 500000000, "+09:00")));
            Assert.Equal("2024-03-05 06:07:08 -05:00", ValueFormatter.Format(new OracleTimeStampTZ(2024, 3, 5, 6, 7, 8, 0, "-05:00")));
            // 지역 이름도 오프셋으로 보인다(서울은 서머타임 없음)
            Assert.Equal("2024-03-05 06:07:08 +09:00", ValueFormatter.Format(new OracleTimeStampTZ(2024, 3, 5, 6, 7, 8, 0, "Asia/Seoul")));
        }

        [Fact]
        public void Format_OracleTimeStampLTZ_HasNoOffset()
        {
            Assert.Equal("2024-03-05 06:07:08.000005", ValueFormatter.Format(new OracleTimeStampLTZ(2024, 3, 5, 6, 7, 8, 5000)));
        }

        [Fact]
        public void Format_OracleString_ReturnsValueAndTruncates()
        {
            Assert.Equal("가나다 abc", ValueFormatter.Format(new OracleString("가나다 abc")));
            var longText = new string('z', ValueFormatter.MaxTextLength + 5);
            Assert.Equal(new string('z', ValueFormatter.MaxTextLength) + "…(전체 4005자)", ValueFormatter.Format(new OracleString(longText)));
        }

        [Fact]
        public void Format_OracleBinary_IsUpperHex()
        {
            Assert.Equal("0x01ABFF", ValueFormatter.Format(new OracleBinary(new byte[] { 0x01, 0xAB, 0xFF })));
        }

        [Fact]
        public void Format_OracleIntervalDS_UsesSignedDayTimeNotation()
        {
            Assert.Equal("+1 02:03:04.5", ValueFormatter.Format(new OracleIntervalDS(1, 2, 3, 4, 500000000)));
            Assert.Equal("-1 02:03:04.5", ValueFormatter.Format(new OracleIntervalDS(-1, -2, -3, -4, -500000000)));
            Assert.Equal("-0 01:30:00", ValueFormatter.Format(new OracleIntervalDS(TimeSpan.FromHours(-1.5))));
            Assert.Equal("+0 00:00:00", ValueFormatter.Format(new OracleIntervalDS(TimeSpan.Zero)));
        }

        [Fact]
        public void Format_OracleIntervalYM_UsesSignedYearMonthNotation()
        {
            Assert.Equal("+1-02", ValueFormatter.Format(new OracleIntervalYM(1, 2)));
            Assert.Equal("-1-02", ValueFormatter.Format(new OracleIntervalYM(-1, -2)));
        }

        [Fact]
        public void Format_OracleBoolean_IsTrueOrFalse()
        {
            Assert.Equal("TRUE", ValueFormatter.Format(OracleBoolean.True));
            Assert.Equal("FALSE", ValueFormatter.Format(OracleBoolean.False));
            Assert.Null(ValueFormatter.Format(OracleBoolean.Null));
        }

        [Fact]
        public void Format_StringAtLimit_IsUnchanged()
        {
            var text = new string('a', ValueFormatter.MaxTextLength);
            Assert.Equal(text, ValueFormatter.Format(text));
            Assert.Equal("", ValueFormatter.Format(""));
        }

        [Fact]
        public void Format_StringOverLimit_TruncatesAndAddsTotalLength()
        {
            var text = new string('a', ValueFormatter.MaxTextLength + 1);
            Assert.Equal(new string('a', ValueFormatter.MaxTextLength) + "…(전체 4001자)", ValueFormatter.Format(text));
        }

        [Fact]
        public void Format_StringOverLimit_DoesNotSplitSurrogatePair()
        {
            // 4000번째 글자가 서로게이트 쌍의 앞 절반이면 그 앞에서 자른다
            var text = new string('a', ValueFormatter.MaxTextLength - 1) + "😀" + "b";

            var formatted = ValueFormatter.Format(text);

            Assert.Equal(new string('a', ValueFormatter.MaxTextLength - 1) + "…(전체 4002자)", formatted);
        }

        [Fact]
        public void Format_Bytes_IsUpperHexWithPrefix()
        {
            Assert.Equal("0x000FA0", ValueFormatter.Format(new byte[] { 0x00, 0x0F, 0xA0 }));
            Assert.Equal("0x", ValueFormatter.Format(new byte[0]));
        }

        [Fact]
        public void Format_BytesAtLimit_IsNotTruncated()
        {
            var bytes = Filled(ValueFormatter.MaxBinaryBytes, 0xAB);
            Assert.Equal("0x" + Repeat("AB", ValueFormatter.MaxBinaryBytes), ValueFormatter.Format(bytes));
        }

        [Fact]
        public void Format_BytesOverLimit_TruncatesAndAddsTotalLength()
        {
            var bytes = Filled(ValueFormatter.MaxBinaryBytes + 1, 0x5C);
            Assert.Equal("0x" + Repeat("5C", ValueFormatter.MaxBinaryBytes) + "…(전체 2001바이트)", ValueFormatter.Format(bytes));
        }

        [Fact]
        public void DescribeColumn_NumberWithPrecisionAndScale()
        {
            var column = ValueFormatter.DescribeColumn("PRICE", "NUMBER", typeof(decimal), 22, 7, 2);

            Assert.Equal("PRICE", column.Name);
            Assert.Equal("NUMBER(7,2)", column.TypeLabel);
            Assert.True(column.IsNumeric);
        }

        [Fact]
        public void DescribeColumn_NumberWithPrecisionOnly()
        {
            Assert.Equal("NUMBER(4)", ValueFormatter.DescribeColumn("QTY", "NUMBER", typeof(short), 22, 4, 0).TypeLabel);
            Assert.Equal("NUMBER(4)", ValueFormatter.DescribeColumn("QTY", "NUMBER", typeof(short), 22, 4, null).TypeLabel);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData(0, 0)]
        [InlineData(0, null)]
        public void DescribeColumn_NumberWithoutPrecision(int? precision, int? scale)
        {
            var column = ValueFormatter.DescribeColumn("N", "NUMBER", typeof(decimal), 22, precision, scale);

            Assert.Equal("NUMBER", column.TypeLabel);
            Assert.True(column.IsNumeric);
        }

        [Fact]
        public void DescribeColumn_NumberWithScaleOnly_ShowsStarPrecision()
        {
            Assert.Equal("NUMBER(*,2)", ValueFormatter.DescribeColumn("N", "NUMBER", typeof(decimal), 22, null, 2).TypeLabel);
        }

        [Theory]
        [InlineData("VARCHAR2", 10, "VARCHAR2(10)")]
        [InlineData("NVARCHAR2", 20, "NVARCHAR2(20)")]
        [InlineData("CHAR", 1, "CHAR(1)")]
        [InlineData("NCHAR", 3, "NCHAR(3)")]
        [InlineData("RAW", 16, "RAW(16)")]
        [InlineData("VARCHAR2", null, "VARCHAR2")]
        [InlineData("VARCHAR2", 0, "VARCHAR2")]
        public void DescribeColumn_SizedTypes(string typeName, int? size, string expected)
        {
            var column = ValueFormatter.DescribeColumn("C", typeName, typeName == "RAW" ? typeof(byte[]) : typeof(string), size, null, null);

            Assert.Equal(expected, column.TypeLabel);
            Assert.False(column.IsNumeric);
        }

        [Fact]
        public void DescribeColumn_OtherTypes_KeepTypeName()
        {
            var date = ValueFormatter.DescribeColumn("HIRED", "DATE", typeof(DateTime), 7, null, null);
            Assert.Equal("DATE", date.TypeLabel);
            Assert.False(date.IsNumeric);

            Assert.Equal("TIMESTAMP WITH TIME ZONE", ValueFormatter.DescribeColumn("T", "TIMESTAMP WITH TIME ZONE", typeof(DateTimeOffset), 13, null, null).TypeLabel);
            // DATE·CLOB 등은 크기가 있어도 붙이지 않는다
            Assert.Equal("CLOB", ValueFormatter.DescribeColumn("C", "CLOB", typeof(string), 4000, null, null).TypeLabel);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void DescribeColumn_NoTypeName_FallsBackToFieldTypeName(string typeName)
        {
            var column = ValueFormatter.DescribeColumn("X", typeName, typeof(long), null, null, null);

            Assert.Equal("Int64", column.TypeLabel);
            Assert.True(column.IsNumeric);
            Assert.Equal("String", ValueFormatter.DescribeColumn("S", typeName, typeof(string), null, null, null).TypeLabel);
        }

        [Fact]
        public void DescribeColumn_NumericFieldType_IsNumericEvenWithUnknownTypeName()
        {
            Assert.True(ValueFormatter.DescribeColumn("A", "MYTYPE", typeof(double), null, null, null).IsNumeric);
            Assert.True(ValueFormatter.DescribeColumn("A", "MYTYPE", typeof(int?), null, null, null).IsNumeric);
            Assert.False(ValueFormatter.DescribeColumn("A", "MYTYPE", typeof(string), null, null, null).IsNumeric);
            Assert.False(ValueFormatter.DescribeColumn("A", "MYTYPE", null, null, null, null).IsNumeric);
        }

        [Theory]
        [InlineData("NUMBER")]
        [InlineData("number")]
        [InlineData("NUMBER(7,2)")]
        [InlineData("FLOAT")]
        [InlineData("BINARY_FLOAT")]
        [InlineData("BINARY_DOUBLE")]
        [InlineData("INTEGER")]
        [InlineData("DECIMAL")]
        [InlineData("NUMERIC")]
        [InlineData("SMALLINT")]
        [InlineData("REAL")]
        [InlineData(" numeric ")]
        [InlineData("Int16")]
        [InlineData("INT32")]
        [InlineData("Int64")]
        [InlineData("Decimal")]
        [InlineData("Double")]
        [InlineData("Single")]
        [InlineData("System.Int32")]
        public void IsNumericTypeName_NumericNames_True(string name)
        {
            Assert.True(ValueFormatter.IsNumericTypeName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("VARCHAR2")]
        [InlineData("CHAR")]
        [InlineData("DATE")]
        [InlineData("TIMESTAMP")]
        [InlineData("CLOB")]
        [InlineData("RAW")]
        [InlineData("BOOLEAN")]
        [InlineData("String")]
        [InlineData("DateTime")]
        [InlineData("INTERVAL DAY TO SECOND")]
        public void IsNumericTypeName_OtherNames_False(string name)
        {
            Assert.False(ValueFormatter.IsNumericTypeName(name));
        }

        /// <summary>소수점이 ','인 문화권. 실제 de-DE를 쓰고, 문화권 데이터가 없는 환경이면 같은 구분자의 사본을 만든다.</summary>
        private static CultureInfo CommaCulture()
        {
            try
            {
                var german = CultureInfo.GetCultureInfo("de-DE");
                if (german.NumberFormat.NumberDecimalSeparator == ",")
                    return german;
            }
            catch (CultureNotFoundException)
            {
            }
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NumberDecimalSeparator = ",";
            culture.NumberFormat.NumberGroupSeparator = ".";
            return culture;
        }

        private static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = value;
            return bytes;
        }

        private static string Repeat(string text, int count)
        {
            return string.Concat(System.Linq.Enumerable.Repeat(text, count));
        }
    }
}
