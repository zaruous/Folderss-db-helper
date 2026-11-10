using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>
    /// SQL Server: sys.* 카탈로그로 조회한다. 스키마를 생략하면 기본 스키마(SCHEMA_NAME()) → 그 스키마의 동의어 → 다른 스키마 순.
    /// 주석은 확장 속성 MS_Description, 기본값은 기본값 제약(sys.default_constraints).
    /// </summary>
    internal sealed class SqlServerTableSchema : ITableSchemaProvider
    {
        public static readonly SqlServerTableSchema Instance = new SqlServerTableSchema();
        public ISchemaDdl Ddl { get; } = new SqlServerSchemaDdl();

        const string ObjectType = "CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END";
        /// <summary>대상 테이블들의 object_id 목록(스키마 @owner, 이름 IN 목록).</summary>
        static string TargetIds(string list) => "(SELECT o.object_id FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = @owner AND o.name IN " + list + ")";

        public Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error)
            => SchemaRead.DescribeAsync(session, owner, name, token, error, Resolve, Summary, Columns, ReadColumn,
                async (s, o, n, t) => (await IndexesAsync(s, o, new[] { n }, t).ConfigureAwait(true))[n].ToList(),
                async (s, o, n, t) => (await ConstraintsAsync(s, o, new[] { n }, t).ConfigureAwait(true))[n].ToList());

        public Task<List<TableDescription>> DescribeManyAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token)
            => SchemaRead.DescribeManyAsync(session, owner, names, token, Summary, Columns, ReadColumn, IndexesAsync, ConstraintsAsync);

        /// <summary>후보: PRI 1 지정·기본 스키마의 테이블·뷰, 2 그 스키마 동의어가 가리키는 테이블·뷰, 4 다른 스키마.</summary>
        public static SqlQuery Resolve(string owner, string name)
        {
            var schema = owner == null ? "SCHEMA_NAME()" : "@owner";
            var sql = "SELECT OWNER, OBJECT_NAME, OBJECT_TYPE, VIA, PRI FROM ("
                + "SELECT s.name AS OWNER, o.name AS OBJECT_NAME, " + ObjectType + " AS OBJECT_TYPE, CAST(NULL AS nvarchar(300)) AS VIA,"
                + " CASE WHEN s.name = " + schema + " THEN 1 ELSE 4 END AS PRI"
                + " FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id"
                + " WHERE o.type IN ('U','V') AND o.name = @name" + (owner == null ? "" : " AND s.name = @owner")
                + " UNION ALL "
                + "SELECT bs.name, o.name, " + ObjectType + ", ss.name + '.' + sy.name, 2"
                + " FROM sys.synonyms sy JOIN sys.schemas ss ON ss.schema_id = sy.schema_id"
                + " JOIN sys.objects o ON o.object_id = OBJECT_ID(sy.base_object_name) JOIN sys.schemas bs ON bs.schema_id = o.schema_id"
                + " WHERE o.type IN ('U','V') AND sy.name = @name AND ss.name = " + schema
                + ") q ORDER BY PRI, OWNER";
            var query = new SqlQuery { Sql = sql };
            if (owner != null) SchemaRead.Bind(query, "owner", owner);
            SchemaRead.Bind(query, "name", name);
            return query;
        }

        public static SqlQuery Summary(string owner, string name) => Summary(owner, new[] { name });

        /// <summary>요약(이름 목록). 열 OBJECT_NAME으로 어느 테이블인지 가린다.</summary>
        public static SqlQuery Summary(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var list = SchemaRead.BindNames(query, "@", names);
            query.Sql = "SELECT o.name AS OBJECT_NAME, " + ObjectType + " AS OBJECT_TYPE, CAST(NULL AS nvarchar(10)) AS STATUS, o.create_date AS CREATED, o.modify_date AS LAST_DDL_TIME,"
                + " (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS NUM_ROWS,"
                + " (SELECT MAX(STATS_DATE(st.object_id, st.stats_id)) FROM sys.stats st WHERE st.object_id = o.object_id) AS LAST_ANALYZED,"
                + " CAST(ep.value AS nvarchar(4000)) AS COMMENTS"
                + " FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id"
                + " LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'"
                + " WHERE s.name = @owner AND o.name IN " + list + " AND o.type IN ('U', 'V')";
            return query;
        }

        public static SqlQuery Columns(string owner, string name) => Columns(owner, new[] { name });

        public static SqlQuery Columns(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var list = SchemaRead.BindNames(query, "@", names);
            query.Sql = "SELECT s.name AS OWNER, o.name AS TABLE_NAME, c.name AS COLUMN_NAME, " + SqlServerMetadata.TypeLabel("c", "t") + " AS TYPE_LABEL,"
                + " CASE WHEN c.is_nullable = 1 THEN 'Y' ELSE 'N' END AS NULLABLE, c.column_id AS COLUMN_ID,"
                + " CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id"
                + " WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS IS_PK,"
                + " dc.definition AS DATA_DEFAULT, CAST(ep.value AS nvarchar(4000)) AS COMMENTS"
                + " FROM sys.columns c"
                + " JOIN sys.objects o ON o.object_id = c.object_id"
                + " JOIN sys.schemas s ON s.schema_id = o.schema_id"
                + " JOIN sys.types t ON t.user_type_id = c.user_type_id"
                + " LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id"
                + " LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'"
                + " WHERE s.name = @owner AND o.name IN " + list
                + " ORDER BY o.name, c.column_id";
            return query;
        }

        public static DescribedColumn ReadColumn(IDataRecord record)
        {
            var defaultValue = SchemaRead.Text(record, "DATA_DEFAULT");
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
                Default = defaultValue == null ? null : SchemaRead.Unwrap(defaultValue), // "((0))" → "0"
                Comment = SchemaRead.Text(record, "COMMENTS")
            };
        }

        /// <summary>인덱스 컬럼 한 줄씩(힙 제외). INCLUDE 컬럼은 IS_INCLUDE.</summary>
        public static SqlQuery IndexColumns(string owner, string name) => IndexColumns(owner, new[] { name });

        public static SqlQuery IndexColumns(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var list = SchemaRead.BindNames(query, "@", names);
            query.Sql = "SELECT OBJECT_NAME(i.object_id) AS TABLE_NAME, i.name AS INDEX_NAME, i.is_unique AS IS_UNIQUE, i.type_desc AS INDEX_TYPE, i.is_disabled AS IS_DISABLED,"
                + " CASE WHEN i.is_primary_key = 1 OR i.is_unique_constraint = 1 THEN 1 ELSE 0 END AS BY_CONSTRAINT, i.has_filter AS HAS_FILTER,"
                + " ic.key_ordinal AS POSITION, ic.is_included_column AS IS_INCLUDE, ic.is_descending_key AS IS_DESC, c.name AS COLUMN_NAME"
                + " FROM sys.indexes i"
                + " LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id"
                + " LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id"
                + " WHERE i.object_id IN " + TargetIds(list) + " AND i.type > 0 AND i.is_hypothetical = 0"
                + " ORDER BY TABLE_NAME, i.name, ic.is_included_column, ic.key_ordinal";
            return query;
        }

        /// <summary>인덱스(테이블 이름별).</summary>
        static async Task<ILookup<string, IndexDescription>> IndexesAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token)
        {
            var rows = await session.QueryAsync(IndexColumns(owner, names), r => new
            {
                Table = SchemaRead.Text(r, "TABLE_NAME"), Name = SchemaRead.Text(r, "INDEX_NAME"), Unique = SchemaRead.Flag(r, "IS_UNIQUE"), Type = SchemaRead.Text(r, "INDEX_TYPE"),
                Disabled = SchemaRead.Flag(r, "IS_DISABLED"), Owned = SchemaRead.Flag(r, "BY_CONSTRAINT"), Filtered = SchemaRead.Flag(r, "HAS_FILTER"),
                Include = SchemaRead.Flag(r, "IS_INCLUDE"), Descending = SchemaRead.Flag(r, "IS_DESC"), Column = SchemaRead.Text(r, "COLUMN_NAME")
            }, token).ConfigureAwait(true);
            return rows.GroupBy(r => new { r.Table, r.Name }).Select(g =>
            {
                var first = g.First();
                var keys = g.Where(r => !r.Include && r.Column != null).ToList();
                var index = new IndexDescription
                {
                    Name = first.Name, Unique = first.Unique, Status = first.Disabled ? "DISABLED" : "VALID",
                    Type = first.Type + (first.Filtered ? " (필터)" : "") + (g.Any(r => r.Include) ? " (INCLUDE)" : ""),
                    OwnerConstraint = first.Owned ? first.Name : null, // 기본 키·고유 제약의 인덱스는 제약과 이름이 같다
                    Recreatable = first.Type == "NONCLUSTERED" && !first.Filtered && !g.Any(r => r.Include) && !first.Owned,
                    Columns = string.Join(", ", keys.Select(r => TableSchemaLogic.QuoteName(r.Column) + (r.Descending ? " DESC" : "")))
                };
                index.ColumnNames.AddRange(keys.Select(r => r.Column));
                return new { first.Table, Index = index };
            }).ToLookup(x => x.Table, x => x.Index, StringComparer.Ordinal);
        }

        /// <summary>제약 컬럼 한 줄씩: 기본 키·고유(sys.key_constraints)·외래 키·검사.</summary>
        public static SqlQuery ConstraintColumns(string owner, string name) => ConstraintColumns(owner, new[] { name });

        public static SqlQuery ConstraintColumns(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var targets = TargetIds(SchemaRead.BindNames(query, "@", names));
            query.Sql = "SELECT TABLE_NAME, CONSTRAINT_NAME, CTYPE, IS_DISABLED, POSITION, COLUMN_NAME, R_OWNER, R_TABLE, R_COLUMN, DEFINITION, DELETE_RULE, UPDATE_RULE FROM ("
                + "SELECT OBJECT_NAME(kc.parent_object_id) AS TABLE_NAME, kc.name AS CONSTRAINT_NAME, CASE kc.type WHEN 'PK' THEN 'P' ELSE 'U' END AS CTYPE, CAST(0 AS bit) AS IS_DISABLED,"
                + " ic.key_ordinal AS POSITION, c.name AS COLUMN_NAME, CAST(NULL AS sysname) AS R_OWNER, CAST(NULL AS sysname) AS R_TABLE, CAST(NULL AS sysname) AS R_COLUMN,"
                + " CAST(NULL AS nvarchar(max)) AS DEFINITION, CAST(NULL AS nvarchar(60)) AS DELETE_RULE, CAST(NULL AS nvarchar(60)) AS UPDATE_RULE"
                + " FROM sys.key_constraints kc"
                + " JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id"
                + " JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id"
                + " WHERE kc.parent_object_id IN " + targets
                + " UNION ALL "
                + "SELECT OBJECT_NAME(fk.parent_object_id), fk.name, 'R', fk.is_disabled, fkc.constraint_column_id, pc.name, rs.name, ro.name, rc.name, NULL,"
                + " REPLACE(fk.delete_referential_action_desc, '_', ' '), REPLACE(fk.update_referential_action_desc, '_', ' ')"
                + " FROM sys.foreign_keys fk"
                + " JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id"
                + " JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id"
                + " JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id = ro.schema_id"
                + " JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id"
                + " WHERE fk.parent_object_id IN " + targets
                + " UNION ALL "
                + "SELECT OBJECT_NAME(cc.parent_object_id), cc.name, 'C', cc.is_disabled, 1, col.name, NULL, NULL, NULL, cc.definition, NULL, NULL"
                + " FROM sys.check_constraints cc LEFT JOIN sys.columns col ON col.object_id = cc.parent_object_id AND col.column_id = cc.parent_column_id"
                + " WHERE cc.parent_object_id IN " + targets
                + ") q ORDER BY TABLE_NAME, CASE CTYPE WHEN 'P' THEN 1 WHEN 'U' THEN 2 WHEN 'R' THEN 3 ELSE 4 END, CONSTRAINT_NAME, POSITION";
            return query;
        }

        /// <summary>제약(테이블 이름별).</summary>
        static async Task<ILookup<string, ConstraintDescription>> ConstraintsAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token)
        {
            var rows = await session.QueryAsync(ConstraintColumns(owner, names), r => new
            {
                Table = SchemaRead.Text(r, "TABLE_NAME"), Name = SchemaRead.Text(r, "CONSTRAINT_NAME"), Type = SchemaRead.Text(r, "CTYPE"), Disabled = SchemaRead.Flag(r, "IS_DISABLED"),
                Column = SchemaRead.Text(r, "COLUMN_NAME"), RefOwner = SchemaRead.Text(r, "R_OWNER"), RefTable = SchemaRead.Text(r, "R_TABLE"),
                RefColumn = SchemaRead.Text(r, "R_COLUMN"), Definition = SchemaRead.Text(r, "DEFINITION"),
                DeleteRule = SchemaRead.Text(r, "DELETE_RULE"), UpdateRule = SchemaRead.Text(r, "UPDATE_RULE")
            }, token).ConfigureAwait(true);
            var ddl = (SqlServerSchemaDdl)Instance.Ddl;
            return rows.GroupBy(r => new { r.Table, r.Name }).Select(g =>
            {
                var first = g.First();
                var result = new ConstraintDescription
                {
                    Name = first.Name, Type = first.Type, Status = first.Disabled ? "DISABLED" : "ENABLED",
                    // 검사 조건이 한 컬럼에 걸려도 그 컬럼은 정의의 일부일 뿐 — 키 컬럼으로 보이지 않는다.
                    Columns = first.Type == "C" ? "" : string.Join(", ", g.Where(r => r.Column != null).Select(r => TableSchemaLogic.QuoteName(r.Column))),
                    Reference = first.RefTable == null ? null : ddl.Quote(first.RefOwner) + "." + ddl.Quote(first.RefTable),
                    Condition = first.Type == "C" ? SchemaRead.Unwrap(first.Definition) : null,
                    DeleteRule = first.Type == "R" ? first.DeleteRule : null,
                    UpdateRule = first.Type == "R" ? first.UpdateRule : null
                };
                if (first.Type != "C") result.ColumnNames.AddRange(g.Where(r => r.Column != null).Select(r => r.Column));
                result.ReferenceColumns.AddRange(g.Where(r => r.RefColumn != null).Select(r => ddl.Quote(r.RefColumn)));
                return new { first.Table, Constraint = result };
            }).ToLookup(x => x.Table, x => x.Constraint, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// SQL Server DDL. 식별자는 [ ], 이름 바꾸기는 sp_rename, 주석은 확장 속성 MS_Description.
    /// 컬럼 형식·NULL 변경은 ALTER COLUMN에 형식과 NULL 여부를 함께 적는다. 기본값은 이름 있는 기본값 제약이라
    /// 바꾸거나 컬럼을 지울 때 기존 기본값 제약을 찾아 먼저 지운다(한 배치 — 다시 실행해도 안전).
    /// 제약 끄기·켜기는 외래 키·검사만(NOCHECK / WITH CHECK CHECK).
    /// </summary>
    internal sealed class SqlServerSchemaDdl : SchemaDdlBase
    {
        static readonly DataTypeChoice[] Types =
        {
            new DataTypeChoice("nvarchar(100)", "유니코드 가변 문자열 100자"), new DataTypeChoice("nvarchar(max)", "긴 유니코드 문자열"),
            new DataTypeChoice("varchar(100)", "가변 문자열 100자"), new DataTypeChoice("char(1)", "고정 1자"),
            new DataTypeChoice("int", "정수(4바이트)"), new DataTypeChoice("bigint", "정수(8바이트)"), new DataTypeChoice("decimal(18,2)", "금액·소수 2자리"),
            new DataTypeChoice("bit", "참/거짓"), new DataTypeChoice("date", "날짜"), new DataTypeChoice("datetime2(7)", "날짜·시각"),
            new DataTypeChoice("uniqueidentifier", "GUID"), new DataTypeChoice("varbinary(max)", "이진 데이터")
        };

        public override int MaxNameLength => 128;
        public override IReadOnlyList<DataTypeChoice> DataTypes => Types;
        public override bool CanDisable(string constraintType) => constraintType == "R" || constraintType == "C";

        public override string Quote(string name) => "[" + Checked(name).Replace("]", "]]") + "]";
        static string N(string text) => "N" + Literal(text);

        public override string AddColumn(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " ADD " + Quote(column.Name) + " " + Fragment(column.DataType) + (column.NotNull ? " NOT NULL" : " NULL")
               + (string.IsNullOrWhiteSpace(column.Default) ? "" : " DEFAULT (" + Fragment(column.Default) + ")");

        public override string DropColumn(SchemaTarget target, string column)
            => DropDefault(target, column) + "\nALTER TABLE " + Table(target) + " DROP COLUMN " + Quote(column);

        public override string RenameColumn(SchemaTarget target, string from, string to)
            => "EXEC sp_rename " + N(Table(target) + "." + Quote(from)) + ", " + N(Checked(to)) + ", N'COLUMN'";

        string AlterColumn(SchemaTarget target, ColumnSpec column)
            => "ALTER TABLE " + Table(target) + " ALTER COLUMN " + Quote(column.Name) + " " + Fragment(column.DataType) + (column.NotNull ? " NOT NULL" : " NULL");
        public override string ChangeType(SchemaTarget target, ColumnSpec column) => AlterColumn(target, column);
        public override string ChangeNullability(SchemaTarget target, ColumnSpec column) => AlterColumn(target, column);

        public override string ChangeDefault(SchemaTarget target, ColumnSpec column)
            => DropDefault(target, column.Name)
               + (string.IsNullOrWhiteSpace(column.Default) ? "" : "\nALTER TABLE " + Table(target) + " ADD DEFAULT (" + Fragment(column.Default) + ") FOR " + Quote(column.Name));

        /// <summary>컬럼의 기본값 제약이 있으면 지우는 배치 앞부분.</summary>
        string DropDefault(SchemaTarget target, string column)
        {
            var table = Table(target);
            return "DECLARE @df sysname = (SELECT d.name FROM sys.default_constraints d"
                + " JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id"
                + " WHERE d.parent_object_id = OBJECT_ID(" + N(table) + ") AND c.name = " + N(Checked(column)) + ");"
                // EXEC(…) 안에는 함수 호출을 쓸 수 없어 변수에 담아 실행한다.
                + "\nIF @df IS NOT NULL BEGIN DECLARE @drop nvarchar(max) = " + N("ALTER TABLE " + table + " DROP CONSTRAINT ") + " + QUOTENAME(@df); EXEC(@drop); END;";
        }

        public override string CommentColumn(SchemaTarget target, string column, string comment)
        {
            var levels = "@level0type = N'SCHEMA', @level0name = " + N(Checked(target.Owner)) + ", @level1type = N'TABLE', @level1name = " + N(Checked(target.Table))
                + ", @level2type = N'COLUMN', @level2name = " + N(Checked(column));
            var table = N(Table(target));
            var sql = "IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND major_id = OBJECT_ID(" + table + ")"
                + " AND minor_id = COLUMNPROPERTY(OBJECT_ID(" + table + "), " + N(Checked(column)) + ", 'ColumnId') AND name = N'MS_Description')"
                + "\n    EXEC sys.sp_dropextendedproperty @name = N'MS_Description', " + levels;
            if (!string.IsNullOrEmpty(comment))
                sql += ";\nEXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = " + N(comment) + ", " + levels;
            return sql;
        }

        public override string CreateIndex(SchemaTarget target, string name, bool unique, IReadOnlyList<IndexColumnChoice> columns)
            => "CREATE " + (unique ? "UNIQUE " : "") + "INDEX " + Quote(name) + " ON " + Table(target) + " (" + KeyList(columns, true) + ")";
        public override string DropIndex(SchemaTarget target, string name) => "DROP INDEX " + Quote(name) + " ON " + Table(target);
        public override string RenameIndex(SchemaTarget target, string from, string to)
            => "EXEC sp_rename " + N(Table(target) + "." + Quote(from)) + ", " + N(Checked(to)) + ", N'INDEX'";
        public override string RenameConstraint(SchemaTarget target, string from, string to)
            => "EXEC sp_rename " + N(Quote(target.Owner) + "." + Quote(from)) + ", " + N(Checked(to)) + ", N'OBJECT'";
        public override string SetConstraintEnabled(SchemaTarget target, string name, string constraintType, bool enabled)
        {
            if (!CanDisable(constraintType)) throw new NotSupportedException("SQL Server는 외래 키·검사 제약만 끄고 켤 수 있습니다.");
            return "ALTER TABLE " + Table(target) + (enabled ? " WITH CHECK CHECK" : " NOCHECK") + " CONSTRAINT " + Quote(name);
        }
    }
}
