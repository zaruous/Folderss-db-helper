using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace MyPlugin
{
    /// <summary>
    /// SQLite 데이터 사전 조회 SQL과 읽기(순수 로직, 테스트 대상).
    /// - 스키마 = 연결의 데이터베이스(main·temp·ATTACH한 이름, pragma_database_list). 객체는 그 스키마의 sqlite_master.
    /// - sqlite_master 앞의 스키마 이름은 바인드할 수 없는 자리라 식별자로 따옴표 처리한다(<see cref="Schema"/>). pragma 함수의 스키마 인자는 바인드한다.
    /// - 객체 종류: 테이블·뷰만 트리에 보인다(인덱스·트리거는 트리 묶음이 없다). 시퀀스·코드 묶음은 늘 비어 있다. 내부 테이블(sqlite_%)은 뺀다.
    /// - 검색은 main·temp만 본다(ATTACH한 DB는 이름을 미리 알 수 없어 한 SQL로 훑지 못한다).
    /// - 행 수 통계·주석이 없다(NUM_ROWS·COMMENTS는 NULL). 행 주소(rowid)로 행을 찾지 않는다 — VACUUM 때 바뀔 수 있다.
    /// - 바인드는 :owner·:name·:pattern·:limit(Microsoft.Data.Sqlite는 접두 없는 이름을 ':이름'에 맞춘다).
    /// </summary>
    internal sealed class SqliteMetadata : IMetadataProvider
    {
        private const string UserObjects = "m.type IN ('table', 'view') AND m.name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";
        private const string ObjectType = "CASE m.type WHEN 'table' THEN 'TABLE' ELSE 'VIEW' END";

        public bool HasMaintainedFlagFallback { get { return false; } }
        public bool SupportsTableInfo { get { return true; } }

        /// <summary>"main" → "\"main\".sqlite_master". 이름 안의 "는 두 번.</summary>
        public static string Schema(string owner)
        {
            return Quote(owner) + ".sqlite_master";
        }

        public static string Quote(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("스키마 이름이 비었습니다.", nameof(name));
            return "\"" + name.Replace("\"", "\"\"") + "\"";
        }

        public SqlQuery TableSpecCandidates(string owner)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT :owner AS \"OWNER\", m.name AS \"OBJECT_NAME\", " + ObjectType + " AS \"OBJECT_TYPE\","
                    + " NULL AS \"NUM_ROWS\", NULL AS \"COMMENTS\""
                    + " FROM " + Schema(owner) + " m WHERE " + UserObjects + " ORDER BY m.name"
            };
            Bind(query, "owner", owner);
            return query;
        }

        public SqlQuery CurrentSchema()
        {
            return new SqlQuery { Sql = "SELECT 'main'" };
        }

        public SqlQuery Schemas(bool hasMaintainedFlag)
        {
            return new SqlQuery
            {
                Sql = "SELECT d.name AS \"USERNAME\", 'N' AS \"ORACLE_MAINTAINED\" FROM pragma_database_list d ORDER BY d.seq"
            };
        }

        public SchemaInfo ReadSchema(IDataRecord record, bool hasMaintainedFlag)
        {
            return new SchemaInfo { Name = Text(record, "USERNAME"), OracleMaintained = false };
        }

        public SqlQuery GroupCounts(string owner)
        {
            return new SqlQuery
            {
                Sql = "SELECT " + ObjectType + " AS \"OBJECT_TYPE\", COUNT(*) AS \"CNT\" FROM " + Schema(owner) + " m"
                    + " WHERE " + UserObjects + " GROUP BY 1"
            };
        }

        public SqlQuery Objects(string owner, string group, int limit)
        {
            string where;
            switch (group)
            {
                case TreeGroups.Table:
                    where = "m.type = 'table'";
                    break;
                case TreeGroups.View:
                    where = "m.type = 'view'";
                    break;
                case TreeGroups.Sequence:
                case TreeGroups.Code:
                    // SQLite에는 없는 묶음
                    where = "0";
                    break;
                default:
                    throw new ArgumentException("알 수 없는 객체 묶음입니다: " + group, nameof(group));
            }
            var query = new SqlQuery
            {
                Sql = "SELECT :owner AS \"OWNER\", m.name AS \"OBJECT_NAME\", " + ObjectType + " AS \"OBJECT_TYPE\", 'VALID' AS \"STATUS\", NULL AS \"NUM_ROWS\""
                    + " FROM " + Schema(owner) + " m WHERE " + UserObjects + " AND " + where + " ORDER BY m.name"
            };
            Bind(query, "owner", owner);
            return Limit(query, limit);
        }

        public DbObjectInfo ReadObject(IDataRecord record)
        {
            var numRows = Number(record, "NUM_ROWS");
            return new DbObjectInfo
            {
                Owner = Text(record, "OWNER"),
                Name = Text(record, "OBJECT_NAME"),
                Type = Text(record, "OBJECT_TYPE"),
                Status = Text(record, "STATUS"),
                NumRows = numRows.HasValue ? (long)numRows.Value : (long?)null
            };
        }

        /// <summary>pragma_table_xinfo(생성 열 포함, 가상 테이블의 숨은 열 제외). 형식은 선언한 글 그대로(없으면 빈 글).</summary>
        public SqlQuery Columns(string owner, string objectName)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT :owner AS \"OWNER\", :name AS \"TABLE_NAME\", c.name AS \"COLUMN_NAME\", c.type AS \"TYPE_LABEL\","
                    + " CASE WHEN c.\"notnull\" THEN 'N' ELSE 'Y' END AS \"NULLABLE\", c.cid + 1 AS \"COLUMN_ID\","
                    + " CASE WHEN c.pk > 0 THEN 1 ELSE 0 END AS \"IS_PK\""
                    + " FROM pragma_table_xinfo(:name, :owner) c WHERE c.hidden <> 1 ORDER BY c.cid"
            };
            Bind(query, "owner", owner);
            Bind(query, "name", objectName);
            return query;
        }

        public ColumnInfo ReadColumn(IDataRecord record)
        {
            return new ColumnInfo
            {
                Owner = Text(record, "OWNER"),
                ObjectName = Text(record, "TABLE_NAME"),
                Name = Text(record, "COLUMN_NAME"),
                TypeLabel = Text(record, "TYPE_LABEL") ?? "",
                Nullable = !string.Equals(Text(record, "NULLABLE"), "N", StringComparison.OrdinalIgnoreCase),
                PrimaryKey = (Number(record, "IS_PK") ?? 0) != 0,
                Position = (int)(Number(record, "COLUMN_ID") ?? 0)
            };
        }

        /// <summary>고유 인덱스(UNIQUE 제약 포함): 기본 키·부분 인덱스·식 열이 있는 인덱스는 뺀다.</summary>
        public SqlQuery UniqueKeys(string owner, string objectName)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT il.name AS \"KEY_NAME\", ii.name AS \"COLUMN_NAME\", ii.seqno + 1 AS \"POSITION\""
                    + " FROM pragma_index_list(:name, :owner) il JOIN pragma_index_info(il.name, :owner) ii"
                    + " WHERE il.\"unique\" = 1 AND il.origin <> 'pk' AND il.partial = 0"
                    + " AND NOT EXISTS (SELECT 1 FROM pragma_index_info(il.name, :owner) x WHERE x.name IS NULL)"
                    + " ORDER BY 1, 3"
            };
            Bind(query, "owner", owner);
            Bind(query, "name", objectName);
            return query;
        }

        public UniqueKeyColumn ReadUniqueKey(IDataRecord record)
        {
            return new UniqueKeyColumn
            {
                KeyName = Text(record, "KEY_NAME"),
                ColumnName = Text(record, "COLUMN_NAME"),
                Position = (int)(Number(record, "POSITION") ?? 0)
            };
        }

        /// <summary>rowid는 INTEGER PRIMARY KEY가 없으면 VACUUM 때 바뀔 수 있어 행을 다시 찾는 데 쓰지 않는다(기본 키·고유 키로만 편집).</summary>
        public SqlQuery RowIdTable(string owner, string objectName)
        {
            return null;
        }

        public SqlQuery SearchSchemas(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT d.name AS \"USERNAME\", 'N' AS \"ORACLE_MAINTAINED\" FROM pragma_database_list d"
                    + " WHERE UPPER(d.name) LIKE :pattern ESCAPE '\\' ORDER BY d.name"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        // main·temp의 테이블·뷰(검색 대상)
        private static string SearchableObjects(string columns)
        {
            return "SELECT 'main' AS \"OWNER\", " + columns + " FROM \"main\".sqlite_master m WHERE " + UserObjects
                + " UNION ALL SELECT 'temp', " + columns + " FROM \"temp\".sqlite_master m WHERE " + UserObjects;
        }

        public SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT h.\"OWNER\", h.\"OBJECT_TYPE\", h.\"OBJECT_NAME\" FROM ("
                    + SearchableObjects(ObjectType + " AS \"OBJECT_TYPE\", m.name AS \"OBJECT_NAME\"")
                    + ") h WHERE UPPER(h.\"OBJECT_NAME\") LIKE :pattern ESCAPE '\\' ORDER BY h.\"OWNER\", h.\"OBJECT_TYPE\", h.\"OBJECT_NAME\""
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        public SqlQuery SearchTables(string pattern, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT 'main' AS \"OWNER\", 'TABLE' AS \"OBJECT_TYPE\", m.name AS \"OBJECT_NAME\""
                    + " FROM \"main\".sqlite_master m WHERE " + UserObjects + " AND m.type = 'table'"
                    + " AND UPPER(m.name) LIKE :pattern ESCAPE '\\' ORDER BY m.name"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        public SearchHit ReadObjectHit(IDataRecord record)
        {
            return new SearchHit
            {
                Owner = Text(record, "OWNER"),
                ObjectType = Text(record, "OBJECT_TYPE"),
                ObjectName = Text(record, "OBJECT_NAME")
            };
        }

        public SqlQuery SearchColumns(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT h.\"OWNER\", h.\"OBJECT_TYPE\", h.\"OBJECT_NAME\", c.name AS \"COLUMN_NAME\", c.type AS \"TYPE_LABEL\" FROM ("
                    + SearchableObjects(ObjectType + " AS \"OBJECT_TYPE\", m.name AS \"OBJECT_NAME\"")
                    + ") h JOIN pragma_table_xinfo(h.\"OBJECT_NAME\", h.\"OWNER\") c"
                    + " WHERE c.hidden <> 1 AND UPPER(c.name) LIKE :pattern ESCAPE '\\'"
                    + " ORDER BY h.\"OWNER\", h.\"OBJECT_NAME\", c.cid"
            };
            Bind(query, "pattern", pattern);
            return Limit(query, limit);
        }

        public SearchHit ReadColumnHit(IDataRecord record)
        {
            var hit = ReadObjectHit(record);
            hit.ColumnName = Text(record, "COLUMN_NAME");
            hit.ColumnType = Text(record, "TYPE_LABEL") ?? "";
            return hit;
        }

        public SqlQuery ServerVersion()
        {
            return new SqlQuery { Sql = "SELECT 'SQLite ' || sqlite_version()" };
        }

        public string ReadServerVersion(IDataRecord record)
        {
            return (record.IsDBNull(0) ? "" : Convert.ToString(record.GetValue(0), CultureInfo.InvariantCulture)).Trim();
        }

        /// <summary>뷰: sqlite_master.sql(CREATE VIEW 원문 그대로). 다른 종류는 소스가 없다(행 없음).</summary>
        public SqlQuery Source(string owner, string objectType, string name)
        {
            var query = new SqlQuery
            {
                Sql = "SELECT 'VIEW' AS \"PART\", m.sql AS \"TEXT\" FROM " + Schema(owner) + " m"
                    + " WHERE m.type = 'view' AND m.name = :name AND :type = 'VIEW'"
            };
            Bind(query, "name", name);
            Bind(query, "type", objectType);
            return query;
        }

        public string BuildSource(string owner, string objectType, string name, IList<SourcePart> parts)
        {
            return ObjectSource.JoinStatements(parts, ";");
        }

        // ---------- 도우미 ----------

        private static SqlQuery Limit(SqlQuery query, int limit)
        {
            return PostgresMetadata.Limit(query, limit);
        }

        private static void Bind(SqlQuery query, string name, object value)
        {
            query.Parameters.Add(new KeyValuePair<string, object>(name, value ?? DBNull.Value));
        }

        private static string Text(IDataRecord record, string name)
        {
            var value = record[name];
            return value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static decimal? Number(IDataRecord record, string name)
        {
            var value = record[name];
            return value == null || value is DBNull ? (decimal?)null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
    }
}
