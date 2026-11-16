using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>
    /// SQLite 테이블 정보(F4)·정의서: sqlite_master와 pragma 함수(table_xinfo·index_list·index_xinfo·foreign_key_list)로 읽는다.
    /// 구조 편집은 없다(<see cref="Ddl"/> = null → 정보 창은 읽기 전용): SQLite ALTER TABLE은 열 형식·NOT NULL·기본값·제약을 바꾸지 못해
    /// 테이블을 새로 만들어 옮겨야 하고, 외래 키·트리거·뷰가 얽히면 데이터를 잃을 위험이 크다.
    /// 제약 이름은 SQLite가 알려 주지 않아 기본 키는 "PRIMARY KEY", 외래 키는 "FK1"…(foreign_key_list의 id 순)로 보인다. CHECK 제약은 pragma로 읽을 수 없어 보이지 않는다.
    /// 주석·통계·생성 시각이 없다(빈 칸).
    /// </summary>
    internal sealed class SqliteTableSchema : ITableSchemaProvider
    {
        public static readonly SqliteTableSchema Instance = new SqliteTableSchema();

        public ISchemaDdl Ddl { get { return null; } }

        const string ObjectType = "CASE m.type WHEN 'table' THEN 'TABLE' ELSE 'VIEW' END";
        const string Relation = "m.type IN ('table', 'view') AND m.name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";

        public Task<TableDescription> DescribeAsync(DbSession session, string owner, string name, CancellationToken token, Action<string> error)
            => SchemaRead.DescribeAsync(session, owner, name, token, error, Resolve, Summary, Columns, ReadColumn,
                async (s, o, n, t) => (await IndexesAsync(s, o, new[] { n }, t).ConfigureAwait(true))[n].ToList(),
                async (s, o, n, t) => (await ConstraintsAsync(s, o, new[] { n }, t).ConfigureAwait(true))[n].ToList());

        public Task<List<TableDescription>> DescribeManyAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token)
            => SchemaRead.DescribeManyAsync(session, owner, names, token, Summary, Columns, ReadColumn, IndexesAsync, ConstraintsAsync);

        /// <summary>
        /// 후보: 스키마를 주면 그 스키마(PRI 1). 없으면 SQLite 이름 찾기 순서대로 temp(1) → main(2). 이름은 대소문자 무시(SQLite 규칙).
        /// ATTACH한 DB는 스키마를 붙여야 찾는다.
        /// </summary>
        public static SqlQuery Resolve(string owner, string name)
        {
            string sql;
            if (owner != null)
                sql = Candidates(owner, 1);
            else
                sql = Candidates("temp", 1) + " UNION ALL " + Candidates("main", 2);
            var query = new SqlQuery { Sql = "SELECT * FROM (" + sql + ") ORDER BY \"PRI\", \"OWNER\"" };
            SchemaRead.Bind(query, "name", name);
            return query;
        }

        static string Candidates(string owner, int priority)
        {
            return "SELECT '" + owner.Replace("'", "''") + "' AS \"OWNER\", m.name AS \"OBJECT_NAME\", " + ObjectType + " AS \"OBJECT_TYPE\","
                + " NULL AS \"VIA\", " + priority + " AS \"PRI\""
                + " FROM " + SqliteMetadata.Schema(owner) + " m WHERE " + Relation + " AND m.name = :name COLLATE NOCASE";
        }

        public static SqlQuery Summary(string owner, string name) => Summary(owner, new[] { name });

        public static SqlQuery Summary(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            var list = SchemaRead.BindNames(query, ":", names);
            query.Sql = "SELECT m.name AS \"OBJECT_NAME\", " + ObjectType + " AS \"OBJECT_TYPE\", NULL AS \"STATUS\", NULL AS \"CREATED\", NULL AS \"LAST_DDL_TIME\","
                + " NULL AS \"NUM_ROWS\", NULL AS \"LAST_ANALYZED\", NULL AS \"COMMENTS\""
                + " FROM " + SqliteMetadata.Schema(owner) + " m WHERE " + Relation + " AND m.name IN " + list;
            return query;
        }

        public static SqlQuery Columns(string owner, string name) => Columns(owner, new[] { name });

        public static SqlQuery Columns(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var list = SchemaRead.BindNames(query, ":", names);
            query.Sql = "SELECT :owner AS \"OWNER\", m.name AS \"TABLE_NAME\", c.name AS \"COLUMN_NAME\", c.type AS \"TYPE_LABEL\","
                + " CASE WHEN c.\"notnull\" THEN 'N' ELSE 'Y' END AS \"NULLABLE\", c.cid + 1 AS \"COLUMN_ID\","
                + " CASE WHEN c.pk > 0 THEN 1 ELSE 0 END AS \"IS_PK\", c.dflt_value AS \"DATA_DEFAULT\", NULL AS \"COMMENTS\""
                + " FROM " + SqliteMetadata.Schema(owner) + " m JOIN pragma_table_xinfo(m.name, :owner) c"
                + " WHERE " + Relation + " AND m.name IN " + list + " AND c.hidden <> 1"
                + " ORDER BY m.name, c.cid";
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
                Comment = null
            };
        }

        /// <summary>인덱스 키 열 한 줄씩(index_xinfo의 key = 1). 식 열은 COLUMN_NAME이 NULL. ORIGIN c(CREATE INDEX)·u(UNIQUE 제약)·pk.</summary>
        public static SqlQuery IndexColumns(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var list = SchemaRead.BindNames(query, ":", names);
            query.Sql = "SELECT m.name AS \"TABLE_NAME\", il.name AS \"INDEX_NAME\", il.\"unique\" AS \"IS_UNIQUE\", il.origin AS \"ORIGIN\", il.partial AS \"IS_PARTIAL\","
                + " x.seqno AS \"POSITION\", x.name AS \"COLUMN_NAME\", x.\"desc\" AS \"IS_DESC\""
                + " FROM " + SqliteMetadata.Schema(owner) + " m"
                + " JOIN pragma_index_list(m.name, :owner) il"
                + " JOIN pragma_index_xinfo(il.name, :owner) x"
                + " WHERE m.type = 'table' AND m.name IN " + list + " AND x.key = 1"
                + " ORDER BY m.name, il.name, x.seqno";
            return query;
        }

        static async Task<ILookup<string, IndexDescription>> IndexesAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token)
        {
            var rows = await session.QueryAsync(IndexColumns(owner, names), r => new
            {
                Table = SchemaRead.Text(r, "TABLE_NAME"), Name = SchemaRead.Text(r, "INDEX_NAME"), Unique = SchemaRead.Flag(r, "IS_UNIQUE"),
                Origin = SchemaRead.Text(r, "ORIGIN"), Partial = SchemaRead.Flag(r, "IS_PARTIAL"),
                Column = SchemaRead.Text(r, "COLUMN_NAME"), Descending = SchemaRead.Flag(r, "IS_DESC")
            }, token).ConfigureAwait(true);
            return rows.GroupBy(r => new { r.Table, r.Name }).Select(g =>
            {
                var first = g.First();
                var index = new IndexDescription
                {
                    Name = first.Name, Unique = first.Unique, Status = "VALID",
                    Type = "BTREE" + (first.Partial ? " (부분)" : ""),
                    OwnerConstraint = first.Origin == "pk" ? "PRIMARY KEY" : first.Origin == "u" ? first.Name : null,
                    Recreatable = false,
                    Columns = string.Join(", ", g.Select(r => r.Column != null ? TableSchemaLogic.QuoteName(r.Column) + (r.Descending ? " DESC" : "") : "(식)"))
                };
                index.ColumnNames.AddRange(g.Select(r => r.Column ?? "(식)"));
                return new { first.Table, Index = index };
            }).ToLookup(x => x.Table, x => x.Index, StringComparer.Ordinal);
        }

        /// <summary>
        /// 제약 한 줄씩: 기본 키(table_xinfo의 pk 순서), UNIQUE 제약(index_list origin 'u'), 외래 키(foreign_key_list).
        /// 열: TABLE_NAME, CONSTRAINT_NAME, CONTYPE(P·U·R), POSITION, COLUMN_NAME, R_TABLE, R_COLUMN, DELETE_RULE, UPDATE_RULE.
        /// </summary>
        public static SqlQuery ConstraintColumns(string owner, IReadOnlyList<string> names)
        {
            var query = new SqlQuery();
            SchemaRead.Bind(query, "owner", owner);
            var list = SchemaRead.BindNames(query, ":", names);
            var tables = SqliteMetadata.Schema(owner) + " m";
            var where = " WHERE m.type = 'table' AND m.name IN " + list;
            query.Sql = "SELECT * FROM ("
                + "SELECT m.name AS \"TABLE_NAME\", 'PRIMARY KEY' AS \"CONSTRAINT_NAME\", 'P' AS \"CONTYPE\", c.pk AS \"POSITION\", c.name AS \"COLUMN_NAME\","
                + " NULL AS \"R_TABLE\", NULL AS \"R_COLUMN\", NULL AS \"DELETE_RULE\", NULL AS \"UPDATE_RULE\""
                + " FROM " + tables + " JOIN pragma_table_xinfo(m.name, :owner) c" + where + " AND c.pk > 0"
                + " UNION ALL"
                + " SELECT m.name, il.name, 'U', ii.seqno + 1, ii.name, NULL, NULL, NULL, NULL"
                + " FROM " + tables + " JOIN pragma_index_list(m.name, :owner) il JOIN pragma_index_info(il.name, :owner) ii" + where + " AND il.origin = 'u'"
                + " UNION ALL"
                + " SELECT m.name, 'FK' || (f.id + 1), 'R', f.seq + 1, f.\"from\", f.\"table\", f.\"to\", f.on_delete, f.on_update"
                + " FROM " + tables + " JOIN pragma_foreign_key_list(m.name, :owner) f" + where
                + ") ORDER BY \"TABLE_NAME\", CASE \"CONTYPE\" WHEN 'P' THEN 1 WHEN 'U' THEN 2 ELSE 3 END, \"CONSTRAINT_NAME\", \"POSITION\"";
            return query;
        }

        static async Task<ILookup<string, ConstraintDescription>> ConstraintsAsync(DbSession session, string owner, IReadOnlyList<string> names, CancellationToken token)
        {
            var rows = await session.QueryAsync(ConstraintColumns(owner, names), r => new
            {
                Table = SchemaRead.Text(r, "TABLE_NAME"), Name = SchemaRead.Text(r, "CONSTRAINT_NAME"), Type = SchemaRead.Text(r, "CONTYPE"),
                Column = SchemaRead.Text(r, "COLUMN_NAME"), RefTable = SchemaRead.Text(r, "R_TABLE"), RefColumn = SchemaRead.Text(r, "R_COLUMN"),
                DeleteRule = SchemaRead.Text(r, "DELETE_RULE"), UpdateRule = SchemaRead.Text(r, "UPDATE_RULE")
            }, token).ConfigureAwait(true);
            return rows.GroupBy(r => new { r.Table, r.Name }).Select(g =>
            {
                var first = g.First();
                var result = new ConstraintDescription
                {
                    Name = first.Name, Type = first.Type, Status = "ENABLED",
                    Columns = string.Join(", ", g.Where(r => r.Column != null).Select(r => TableSchemaLogic.QuoteName(r.Column))),
                    // 외래 키는 같은 데이터베이스(스키마)의 테이블만 가리킨다
                    Reference = first.RefTable == null ? null : TableSchemaLogic.QuoteName(owner) + "." + TableSchemaLogic.QuoteName(first.RefTable),
                    DeleteRule = first.Type == "R" ? first.DeleteRule : null,
                    UpdateRule = first.Type == "R" ? first.UpdateRule : null
                };
                result.ColumnNames.AddRange(g.Where(r => r.Column != null).Select(r => r.Column));
                // to가 NULL이면 참조 테이블의 기본 키를 가리킨다
                result.ReferenceColumns.AddRange(g.Where(r => r.RefColumn != null).Select(r => TableSchemaLogic.QuoteName(r.RefColumn)));
                return new { first.Table, Constraint = result };
            }).ToLookup(x => x.Table, x => x.Constraint, StringComparer.Ordinal);
        }
    }
}
