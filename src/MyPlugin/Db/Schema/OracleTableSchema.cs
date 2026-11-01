using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>Oracle: 조회는 ALL_* 데이터 사전(<see cref="TableInfo"/>), DDL은 <see cref="OracleSchemaDdl"/>.</summary>
    internal sealed class OracleTableSchema : ITableSchemaProvider
    {
        public static readonly OracleTableSchema Instance = new OracleTableSchema();
        public ISchemaDdl Ddl { get; } = new OracleSchemaDdl();

        public Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error)
            => TableInfo.DescribeAsync(session, owner, name, token, error);
    }

    /// <summary>
    /// Oracle DDL. 컬럼 변경은 ALTER TABLE … MODIFY (…), 주석은 COMMENT ON, 인덱스는 스키마를 붙인 이름.
    /// 제약은 종류와 무관하게 ENABLE VALIDATE / DISABLE. DDL은 실행 전후 자동 커밋된다.
    /// </summary>
    internal sealed class OracleSchemaDdl : DoubleQuoteDdl
    {
        static readonly DataTypeChoice[] Types =
        {
            new DataTypeChoice("VARCHAR2(100 CHAR)", "가변 문자열 100자"), new DataTypeChoice("VARCHAR2(4000 CHAR)", "가변 문자열 최대(4000자)"),
            new DataTypeChoice("NVARCHAR2(100)", "유니코드 가변 문자열"), new DataTypeChoice("CHAR(1)", "고정 1자 (Y/N 플래그 등)"),
            new DataTypeChoice("NUMBER", "숫자(정밀도 제한 없음)"), new DataTypeChoice("NUMBER(10)", "정수 10자리"), new DataTypeChoice("NUMBER(18,2)", "금액·소수 2자리"),
            new DataTypeChoice("INTEGER", "정수"), new DataTypeChoice("DATE", "날짜·시각(초)"), new DataTypeChoice("TIMESTAMP(6)", "날짜·시각(마이크로초)"),
            new DataTypeChoice("CLOB", "긴 문자열"), new DataTypeChoice("BLOB", "이진 데이터")
        };

        public override int MaxNameLength => 128;
        public override IReadOnlyList<DataTypeChoice> DataTypes => Types;

        public override string AddColumn(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " ADD (" + Quote(column.Name) + " " + Fragment(column.DataType)
               + (string.IsNullOrWhiteSpace(column.Default) ? "" : " DEFAULT " + Fragment(column.Default)) + (column.NotNull ? " NOT NULL" : "") + ")";
        public override string ChangeType(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " MODIFY (" + Quote(column.Name) + " " + Fragment(column.DataType) + ")";
        public override string ChangeNullability(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " MODIFY (" + Quote(column.Name) + (column.NotNull ? " NOT NULL" : " NULL") + ")";
        public override string ChangeDefault(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " MODIFY (" + Quote(column.Name) + " DEFAULT " + (string.IsNullOrWhiteSpace(column.Default) ? "NULL" : Fragment(column.Default)) + ")";
        public override string SetConstraintEnabled(SchemaTarget target, string name, string constraintType, bool enabled)
            => "ALTER TABLE " + Table(target) + (enabled ? " ENABLE VALIDATE" : " DISABLE") + " CONSTRAINT " + Quote(name);
    }
}
