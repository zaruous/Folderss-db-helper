using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>
    /// PostgreSQL: pg_catalog로 조회한다. 이름은 대소문자를 구분하므로(따옴표 없는 이름은 소문자로 저장) 찾을 때는
    /// 정확히 같은 이름을 먼저, 대소문자만 다른 이름을 그다음으로 본다. 동의어는 없다.
    /// </summary>
    internal sealed class PostgresTableSchema : ITableSchemaProvider
    {
        public static readonly PostgresTableSchema Instance = new PostgresTableSchema();
        public ISchemaDdl Ddl { get; } = new PostgresSchemaDdl();

        const string Relation = "c.relkind IN ('r','p','v','m','f')";
        const string ObjectType = "CASE WHEN c.relkind IN ('r','p') THEN 'TABLE' ELSE 'VIEW' END";
        const string Visible = "n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg\\_toast%'";

        public Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error)
            => SchemaRead.DescribeAsync(session, owner, name, token, error, Resolve, Summary, Columns, ReadColumn, IndexesAsync, ConstraintsAsync);

        /// <summary>
        /// 후보: PRI 1 지정·현재 스키마의 같은 이름, 2 그 스키마의 대소문자만 다른 이름, 4 다른 스키마의 같은 이름, 5 다른 스키마의 대소문자만 다른 이름.
        /// </summary>
        public static SqlQuery Resolve(string owner, string name)
        {
            var schema = owner == null ? "current_schema()" : ":owner";
            var sql = "SELECT n.nspname AS \"OWNER\", c.relname AS \"OBJECT_NAME\", " + ObjectType + " AS \"OBJECT_TYPE\", CAST(NULL AS text) AS \"VIA\","
                + " (CASE WHEN n.nspname = " + schema + " THEN 0 ELSE 3 END) + (CASE WHEN c.relname = :name THEN 1 ELSE 2 END) AS \"PRI\""
                + " FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                + " WHERE " + Relation + " AND lower(c.relname) = lower(:name) AND " + Visible
                + (owner == null ? "" : " AND lower(n.nspname) = lower(:owner)")
                + " ORDER BY 5, 1";
            var query = new SqlQuery { Sql = sql };
            if (owner != null) SchemaRead.Bind(query, "owner", owner);
            SchemaRead.Bind(query, "name", name);
            return query;
        }

        public static SqlQuery Summary(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT " + ObjectType + " AS \"OBJECT_TYPE\", CAST(NULL AS text) AS \"STATUS\", CAST(NULL AS timestamp) AS \"CREATED\", CAST(NULL AS timestamp) AS \"LAST_DDL_TIME\","
                    + " CASE WHEN c.relkind IN ('r','p') AND c.reltuples >= 0 THEN CAST(c.reltuples AS bigint) END AS \"NUM_ROWS\","
                    + " CAST(GREATEST(s.last_analyze, s.last_autoanalyze) AS timestamp) AS \"LAST_ANALYZED\","
                    + " pg_catalog.obj_description(c.oid, 'pg_class') AS \"COMMENTS\""
                    + " FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                    + " LEFT JOIN pg_catalog.pg_stat_all_tables s ON s.relid = c.oid"
                    + " WHERE n.nspname = :owner AND c.relname = :name"
            };
            Bind(query, owner, name);
            return query;
        }

        public static SqlQuery Columns(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT n.nspname AS \"OWNER\", c.relname AS \"TABLE_NAME\", a.attname AS \"COLUMN_NAME\","
                    + " pg_catalog.format_type(a.atttypid, a.atttypmod) AS \"TYPE_LABEL\","
                    + " CASE WHEN a.attnotnull THEN 'N' ELSE 'Y' END AS \"NULLABLE\", a.attnum AS \"COLUMN_ID\","
                    + " CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'p' AND a.attnum = ANY (k.conkey)) THEN 1 ELSE 0 END AS \"IS_PK\","
                    + " pg_catalog.pg_get_expr(d.adbin, d.adrelid) AS \"DATA_DEFAULT\", pg_catalog.col_description(c.oid, a.attnum) AS \"COMMENTS\""
                    + " FROM pg_catalog.pg_attribute a"
                    + " JOIN pg_catalog.pg_class c ON c.oid = a.attrelid"
                    + " JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace"
                    + " LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum"
                    + " WHERE n.nspname = :owner AND c.relname = :name AND a.attnum > 0 AND NOT a.attisdropped"
                    + " ORDER BY a.attnum"
            };
            Bind(query, owner, name);
            return query;
        }

        public static DescribedColumn ReadColumn(IDataRecord record)
        {
            return new DescribedColumn
            {
                Column = new ColumnInfo
                {
                    Owner = SchemaRead.Text(record, "OWNER"),
                    ObjectName = SchemaRead.Text(record, "TABLE_NAME"),
                    Name = SchemaRead.Text(record, "COLUMN_NAME"),
                    TypeLabel = SchemaRead.Text(record, "TYPE_LABEL") ?? "",
                    Nullable = !string.Equals(SchemaRead.Text(record, "NULLABLE"), "N", StringComparison.OrdinalIgnoreCase),
                    PrimaryKey = (SchemaRead.Number(record, "IS_PK") ?? 0) != 0,
                    Position = (int)(SchemaRead.Number(record, "COLUMN_ID") ?? 0)
                },
                Default = SchemaRead.Text(record, "DATA_DEFAULT")?.Trim(),
                Comment = SchemaRead.Text(record, "COMMENTS")
            };
        }

        /// <summary>인덱스 컬럼 한 줄씩(인덱스 이름·순서). 식 컬럼은 EXPR, INCLUDE 컬럼은 IS_INCLUDE.</summary>
        public static SqlQuery IndexColumns(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT i.relname AS \"INDEX_NAME\", x.indisunique AS \"IS_UNIQUE\", am.amname AS \"INDEX_TYPE\", x.indisvalid AS \"IS_VALID\","
                    + " (x.indpred IS NOT NULL) AS \"IS_PARTIAL\", (SELECT ct.conname FROM pg_catalog.pg_constraint ct WHERE ct.conindid = x.indexrelid AND ct.conrelid = x.indrelid AND ct.contype IN ('p','u','x') LIMIT 1) AS \"BY_CONSTRAINT\","
                    + " k.ord AS \"POSITION\", a.attname AS \"COLUMN_NAME\", pg_catalog.pg_get_indexdef(x.indexrelid, CAST(k.ord AS int), true) AS \"EXPR\","
                    + " (k.ord > x.indnkeyatts) AS \"IS_INCLUDE\", ((x.indoption[k.ord - 1] & 1) = 1) AS \"IS_DESC\""
                    + " FROM pg_catalog.pg_index x"
                    + " JOIN pg_catalog.pg_class t ON t.oid = x.indrelid JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace"
                    + " JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid JOIN pg_catalog.pg_am am ON am.oid = i.relam"
                    + " CROSS JOIN LATERAL unnest(CAST(x.indkey AS int2[])) WITH ORDINALITY AS k(attnum, ord)"
                    + " LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = x.indrelid AND a.attnum = k.attnum AND k.attnum > 0"
                    + " WHERE n.nspname = :owner AND t.relname = :name"
                    + " ORDER BY i.relname, k.ord"
            };
            Bind(query, owner, name);
            return query;
        }

        static async Task<List<IndexDescription>> IndexesAsync(DbSession session, string owner, string name, CancellationToken token)
        {
            var rows = await session.QueryAsync(IndexColumns(owner, name), r => new
            {
                Name = SchemaRead.Text(r, "INDEX_NAME"), Unique = SchemaRead.Flag(r, "IS_UNIQUE"), Type = SchemaRead.Text(r, "INDEX_TYPE"),
                Valid = SchemaRead.Flag(r, "IS_VALID"), Partial = SchemaRead.Flag(r, "IS_PARTIAL"), Owner = SchemaRead.Text(r, "BY_CONSTRAINT"),
                Column = SchemaRead.Text(r, "COLUMN_NAME"), Expression = SchemaRead.Text(r, "EXPR"),
                Include = SchemaRead.Flag(r, "IS_INCLUDE"), Descending = SchemaRead.Flag(r, "IS_DESC")
            }, token).ConfigureAwait(true);
            return rows.GroupBy(r => r.Name).Select(g =>
            {
                var first = g.First();
                var keys = g.Where(r => !r.Include).ToList();
                var plain = keys.All(r => r.Column != null);
                var index = new IndexDescription
                {
                    Name = first.Name, Unique = first.Unique, Status = first.Valid ? "VALID" : "INVALID",
                    Type = first.Type + (first.Partial ? " (부분)" : "") + (g.Any(r => r.Include) ? " (INCLUDE)" : ""),
                    OwnerConstraint = first.Owner,
                    Recreatable = first.Type == "btree" && !first.Partial && plain && !g.Any(r => r.Include) && first.Owner == null,
                    Columns = string.Join(", ", keys.Select(r => r.Column != null ? TableSchemaLogic.QuoteName(r.Column) + (r.Descending ? " DESC" : "") : r.Expression))
                };
                index.ColumnNames.AddRange(keys.Select(r => r.Column ?? r.Expression));
                return index;
            }).ToList();
        }

        /// <summary>제약 컬럼 한 줄씩. 종류 p·u·f·c(NOT NULL은 컬럼의 NULL 허용에 이미 보인다).</summary>
        public static SqlQuery ConstraintColumns(string owner, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT ct.conname AS \"CONSTRAINT_NAME\", ct.contype AS \"CONTYPE\", k.ord AS \"POSITION\", a.attname AS \"COLUMN_NAME\","
                    + " rn.nspname AS \"R_OWNER\", rt.relname AS \"R_TABLE\", ra.attname AS \"R_COLUMN\","
                    + " CASE WHEN ct.contype = 'c' THEN pg_catalog.pg_get_constraintdef(ct.oid, true) END AS \"DEFINITION\""
                    + " FROM pg_catalog.pg_constraint ct"
                    + " JOIN pg_catalog.pg_class t ON t.oid = ct.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = t.relnamespace"
                    + " LEFT JOIN LATERAL unnest(ct.conkey) WITH ORDINALITY AS k(attnum, ord) ON true"
                    + " LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid = ct.conrelid AND a.attnum = k.attnum"
                    + " LEFT JOIN pg_catalog.pg_class rt ON rt.oid = ct.confrelid LEFT JOIN pg_catalog.pg_namespace rn ON rn.oid = rt.relnamespace"
                    + " LEFT JOIN pg_catalog.pg_attribute ra ON ra.attrelid = ct.confrelid AND ra.attnum = ct.confkey[k.ord]"
                    + " WHERE n.nspname = :owner AND t.relname = :name AND ct.contype IN ('p','u','f','c')"
                    + " ORDER BY CASE ct.contype WHEN 'p' THEN 1 WHEN 'u' THEN 2 WHEN 'f' THEN 3 ELSE 4 END, ct.conname, k.ord"
            };
            Bind(query, owner, name);
            return query;
        }

        static async Task<List<ConstraintDescription>> ConstraintsAsync(DbSession session, string owner, string name, CancellationToken token)
        {
            var rows = await session.QueryAsync(ConstraintColumns(owner, name), r => new
            {
                Name = SchemaRead.Text(r, "CONSTRAINT_NAME"), Type = SchemaRead.Text(r, "CONTYPE"), Column = SchemaRead.Text(r, "COLUMN_NAME"),
                RefOwner = SchemaRead.Text(r, "R_OWNER"), RefTable = SchemaRead.Text(r, "R_TABLE"), RefColumn = SchemaRead.Text(r, "R_COLUMN"),
                Definition = SchemaRead.Text(r, "DEFINITION")
            }, token).ConfigureAwait(true);
            return rows.GroupBy(r => r.Name).Select(g =>
            {
                var first = g.First();
                var type = first.Type == "p" ? "P" : first.Type == "u" ? "U" : first.Type == "f" ? "R" : "C";
                var result = new ConstraintDescription
                {
                    Name = first.Name, Type = type, Status = "ENABLED",
                    Columns = string.Join(", ", g.Where(r => r.Column != null).Select(r => TableSchemaLogic.QuoteName(r.Column))),
                    Reference = first.RefTable == null ? null : TableSchemaLogic.QuoteName(first.RefOwner) + "." + TableSchemaLogic.QuoteName(first.RefTable),
                    Condition = type == "C" ? CheckCondition(first.Definition) : null
                };
                result.ColumnNames.AddRange(g.Where(r => r.Column != null).Select(r => r.Column));
                result.ReferenceColumns.AddRange(g.Where(r => r.RefColumn != null).Select(r => TableSchemaLogic.QuoteName(r.RefColumn)));
                return result;
            }).ToList();
        }

        /// <summary>"CHECK ((amount >= 0)) NOT VALID" → "amount >= 0".</summary>
        public static string CheckCondition(string definition)
        {
            var text = (definition ?? "").Trim();
            if (text.EndsWith(" NOT VALID", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 10).TrimEnd();
            if (text.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase)) text = text.Substring(5);
            return SchemaRead.Unwrap(text);
        }

        static void Bind(SqlQuery query, string owner, string name)
        {
            SchemaRead.Bind(query, "owner", owner);
            SchemaRead.Bind(query, "name", name);
        }
    }

    /// <summary>
    /// PostgreSQL DDL. 컬럼 변경은 ALTER COLUMN … TYPE(USING으로 형 변환)/SET·DROP NOT NULL/SET·DROP DEFAULT.
    /// 인덱스는 테이블과 같은 스키마에 만들어지므로 CREATE INDEX의 이름에는 스키마를 붙이지 않는다. 제약을 끌 수는 없다.
    /// DDL은 세션이 트랜잭션 안에서 실행하고(트랜잭션 DDL), 구조 편집 저장 경로가 문장마다 커밋한다.
    /// </summary>
    internal sealed class PostgresSchemaDdl : DoubleQuoteDdl
    {
        static readonly DataTypeChoice[] Types =
        {
            new DataTypeChoice("varchar(100)", "가변 문자열 100자"), new DataTypeChoice("text", "길이 제한 없는 문자열"), new DataTypeChoice("char(1)", "고정 1자"),
            new DataTypeChoice("integer", "정수(4바이트)"), new DataTypeChoice("bigint", "정수(8바이트)"), new DataTypeChoice("numeric(18,2)", "금액·소수 2자리"),
            new DataTypeChoice("boolean", "참/거짓"), new DataTypeChoice("date", "날짜"), new DataTypeChoice("timestamp", "날짜·시각"),
            new DataTypeChoice("timestamptz", "날짜·시각(시간대)"), new DataTypeChoice("uuid", "UUID"), new DataTypeChoice("jsonb", "JSON"), new DataTypeChoice("bytea", "이진 데이터")
        };

        public override int MaxNameLength => 63;
        public override IReadOnlyList<DataTypeChoice> DataTypes => Types;
        public override bool CanDisable(string constraintType) => false;

        public override string AddColumn(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " ADD COLUMN " + Quote(column.Name) + " " + Fragment(column.DataType)
               + (string.IsNullOrWhiteSpace(column.Default) ? "" : " DEFAULT " + Fragment(column.Default)) + (column.NotNull ? " NOT NULL" : "");
        public override string ChangeType(SchemaTarget target, ColumnSpec column)
        {
            var type = Fragment(column.DataType);
            return "ALTER TABLE " + Table(target) + " ALTER COLUMN " + Quote(column.Name) + " TYPE " + type + " USING " + Quote(column.Name) + "::" + type;
        }
        public override string ChangeNullability(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " ALTER COLUMN " + Quote(column.Name) + (column.NotNull ? " SET NOT NULL" : " DROP NOT NULL");
        public override string ChangeDefault(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " ALTER COLUMN " + Quote(column.Name)
               + (string.IsNullOrWhiteSpace(column.Default) ? " DROP DEFAULT" : " SET DEFAULT " + Fragment(column.Default));
        public override string CommentColumn(SchemaTarget target, string column, string comment)
            => "COMMENT ON COLUMN " + Table(target) + "." + Quote(column) + " IS " + (string.IsNullOrEmpty(comment) ? "NULL" : Literal(comment));
        public override string CreateIndex(SchemaTarget target, string name, bool unique, IReadOnlyList<IndexColumnChoice> columns)
            => "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + Quote(name) + " ON " + Table(target) + " (" + KeyList(columns, true) + ")";
        public override string SetConstraintEnabled(SchemaTarget target, string name, string constraintType, bool enabled)
            => throw new NotSupportedException("PostgreSQL은 제약 조건을 끄고 켤 수 없습니다.");
    }
}
