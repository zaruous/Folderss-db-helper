using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// OracleMetadata: SQL 텍스트, 결과 읽기(DataTable 리더), 형식 표시, ORA-00904 판단.
    /// 끝부분(Simulated_*)은 ALL_* 뷰를 흉내 낸 SQLite 표에 SQL을 실제로 실행해 조인·필터·정렬·열 이름이 Read*와 맞는지 본다.
    /// SQLite는 Oracle 문법을 검증하지 못한다(ROWNUM은 LIMIT으로 바꿔 실행).
    /// </summary>
    public class OracleMetadataTests
    {
        private const string TreeTypes = "('TABLE', 'VIEW', 'SEQUENCE', 'PROCEDURE', 'FUNCTION', 'PACKAGE')";

        private static string Norm(string sql)
        {
            return Regex.Replace(sql, @"\s+", " ").Trim();
        }

        private static string Limited(string inner)
        {
            return "SELECT * FROM (" + inner + ") WHERE ROWNUM <= :limit";
        }

        private static string KnownSystemList()
        {
            return "(" + string.Join(", ", OracleMetadata.KnownSystemSchemas.Select(s => "'" + s + "'")) + ")";
        }

        /// <summary>이름·값·값 형식(string/int)까지 순서대로 비교한다.</summary>
        private static void AssertParameters(SqlQuery query, params object[] nameValuePairs)
        {
            Assert.Equal(nameValuePairs.Length / 2, query.Parameters.Count);
            for (var i = 0; i < query.Parameters.Count; i++)
            {
                Assert.Equal(nameValuePairs[i * 2], query.Parameters[i].Key);
                Assert.Equal(nameValuePairs[i * 2 + 1], query.Parameters[i].Value);
                Assert.Equal(nameValuePairs[i * 2 + 1].GetType(), query.Parameters[i].Value.GetType());
            }
        }

        // ---------- SQL 텍스트 ----------

        [Fact]
        public void CompileErrors_ForOwnerOrCurrentSchema_SortedAndLimited()
        {
            var query = OracleMetadata.CompileErrors("SCOTT", "PKG", new[] { "PACKAGE", "PACKAGE BODY" }, 20);

            Assert.Equal(Limited("SELECT TYPE, LINE, POSITION, TEXT FROM ALL_ERRORS WHERE OWNER = :owner AND NAME = :name"
                + " AND TYPE IN ('PACKAGE', 'PACKAGE BODY') ORDER BY TYPE, SEQUENCE"), Norm(query.Sql));
            AssertParameters(query, "owner", "SCOTT", "name", "PKG", "limit", 21);

            var current = OracleMetadata.CompileErrors(null, "P", new[] { "PROCEDURE" }, 20);

            Assert.Contains("WHERE OWNER = SYS_CONTEXT('USERENV','CURRENT_SCHEMA') AND NAME = :name AND TYPE IN ('PROCEDURE')", current.Sql);
            AssertParameters(current, "name", "P", "limit", 21);
            Assert.Throws<ArgumentException>(() => OracleMetadata.CompileErrors("SCOTT", "P", new string[0], 20));
        }

        [Fact]
        public void ReadCompileError_LineColumnAndOneLineText()
        {
            var table = new DataTable();
            table.Columns.Add("TYPE", typeof(string));
            table.Columns.Add("LINE", typeof(decimal));
            table.Columns.Add("POSITION", typeof(decimal));
            table.Columns.Add("TEXT", typeof(string));
            table.Rows.Add("PACKAGE BODY", 3m, 5m, "PLS-00103: Encountered the symbol \"END\" when expecting one of the following:\n\n   := . ( @ % ;");
            table.Rows.Add("PROCEDURE", DBNull.Value, DBNull.Value, "  PL/SQL: Statement ignored  ");

            var plain = ReadAll(table, r => OracleMetadata.ReadCompileError(r, false));
            var typed = ReadAll(table, r => OracleMetadata.ReadCompileError(r, true));

            Assert.Equal("줄 3, 열 5: PLS-00103: Encountered the symbol \"END\" when expecting one of the following: := . ( @ % ;", plain[0]);
            Assert.Equal("줄 ?, 열 ?: PL/SQL: Statement ignored", plain[1]);
            Assert.StartsWith("PACKAGE BODY 줄 3, 열 5: PLS-00103", typed[0]);
        }

        [Fact]
        public void CurrentSchema_SysContextFromDual_NoParameters()
        {
            var query = OracleMetadata.CurrentSchema();

            Assert.Equal("SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL", query.Sql);
            Assert.Empty(query.Parameters);
        }

        [Theory]
        [InlineData(true, "SELECT USERNAME, ORACLE_MAINTAINED FROM ALL_USERS ORDER BY USERNAME")]
        [InlineData(false, "SELECT USERNAME FROM ALL_USERS ORDER BY USERNAME")]
        public void Schemas_AllUsersByName_OracleMaintainedOnlyWhenAvailable(bool hasOracleMaintained, string expected)
        {
            var query = OracleMetadata.Schemas(hasOracleMaintained);

            Assert.Equal(expected, Norm(query.Sql));
            Assert.Empty(query.Parameters);
        }

        [Fact]
        public void GroupCounts_CountsTreeTypesOfOwner_ExcludesRecycleBin()
        {
            var query = OracleMetadata.GroupCounts("SCOTT");

            Assert.Equal("SELECT OBJECT_TYPE, COUNT(*) AS CNT FROM ALL_OBJECTS WHERE OWNER = :owner AND OBJECT_TYPE IN " + TreeTypes
                + " AND OBJECT_NAME NOT LIKE 'BIN$%' GROUP BY OBJECT_TYPE", Norm(query.Sql));
            AssertParameters(query, "owner", "SCOTT");
        }

        [Fact]
        public void Objects_Table_LeftJoinsAllTablesForNumRows_RownumWrapperWithLimitPlusOne()
        {
            var query = OracleMetadata.Objects("SCOTT", TreeGroups.Table, 1000);

            Assert.Equal(Limited("SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, o.STATUS, t.NUM_ROWS FROM ALL_OBJECTS o"
                + " LEFT JOIN ALL_TABLES t ON t.OWNER = o.OWNER AND t.TABLE_NAME = o.OBJECT_NAME"
                + " WHERE o.OWNER = :owner AND o.OBJECT_TYPE IN ('TABLE') AND o.OBJECT_NAME NOT LIKE 'BIN$%'"
                + " ORDER BY o.OBJECT_NAME"), Norm(query.Sql));
            AssertParameters(query, "owner", "SCOTT", "limit", 1001);
        }

        [Theory]
        [InlineData(TreeGroups.View, "('VIEW')")]
        [InlineData(TreeGroups.Sequence, "('SEQUENCE')")]
        [InlineData(TreeGroups.Code, "('PROCEDURE', 'FUNCTION', 'PACKAGE')")]
        public void Objects_OtherGroups_NullNumRowsWithoutAllTables(string group, string types)
        {
            var query = OracleMetadata.Objects("HR", group, 5000);

            Assert.Equal(Limited("SELECT o.OWNER, o.OBJECT_NAME, o.OBJECT_TYPE, o.STATUS, CAST(NULL AS NUMBER) AS NUM_ROWS FROM ALL_OBJECTS o"
                + " WHERE o.OWNER = :owner AND o.OBJECT_TYPE IN " + types + " AND o.OBJECT_NAME NOT LIKE 'BIN$%'"
                + " ORDER BY o.OBJECT_NAME"), Norm(query.Sql));
            Assert.DoesNotContain("ALL_TABLES", query.Sql);
            AssertParameters(query, "owner", "HR", "limit", 5001);
        }

        [Theory]
        [InlineData(TreeGroups.Table)]
        [InlineData(TreeGroups.View)]
        [InlineData(TreeGroups.Sequence)]
        [InlineData(TreeGroups.Code)]
        public void Objects_LimitZero_NoRownumWrapperNorLimitParameter(string group)
        {
            var query = OracleMetadata.Objects("SCOTT", group, 0);

            Assert.StartsWith("SELECT o.OWNER, o.OBJECT_NAME", query.Sql);
            Assert.EndsWith(" ORDER BY o.OBJECT_NAME", query.Sql);
            Assert.DoesNotContain("ROWNUM", query.Sql);
            Assert.DoesNotContain(":limit", query.Sql);
            AssertParameters(query, "owner", "SCOTT");
        }

        [Theory]
        [InlineData("SYNONYM")]
        [InlineData("PROCEDURE")]
        [InlineData("table")]
        [InlineData("")]
        [InlineData(null)]
        public void Objects_UnknownGroup_Throws(string group)
        {
            Assert.Throws<ArgumentException>(() => OracleMetadata.Objects("SCOTT", group, 0));
        }

        [Fact]
        public void Limit_NegativeThrows_MaxValueDoesNotOverflow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => OracleMetadata.Objects("SCOTT", TreeGroups.Table, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => OracleMetadata.SearchObjects("%A%", true, true, -1));

            var query = OracleMetadata.SearchObjects("%A%", true, true, int.MaxValue);

            Assert.Equal(int.MaxValue, query.Parameters.Single(p => p.Key == "limit").Value);
        }

        [Fact]
        public void Columns_AllTabColumnsWithPrimaryKeyExists_OrderedByColumnId()
        {
            var query = OracleMetadata.Columns("SCOTT", "EMP");

            Assert.Equal("SELECT c.OWNER, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE,"
                + " c.NULLABLE, c.COLUMN_ID, c.CHAR_USED, c.CHAR_LENGTH,"
                + " CASE WHEN EXISTS (SELECT 1 FROM ALL_CONSTRAINTS k"
                + " JOIN ALL_CONS_COLUMNS kc ON kc.OWNER = k.OWNER AND kc.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND kc.TABLE_NAME = k.TABLE_NAME"
                + " WHERE k.CONSTRAINT_TYPE = 'P' AND k.OWNER = c.OWNER AND k.TABLE_NAME = c.TABLE_NAME AND kc.COLUMN_NAME = c.COLUMN_NAME)"
                + " THEN 1 ELSE 0 END AS IS_PK"
                + " FROM ALL_TAB_COLUMNS c WHERE c.OWNER = :owner AND c.TABLE_NAME = :name ORDER BY c.COLUMN_ID", Norm(query.Sql));
            AssertParameters(query, "owner", "SCOTT", "name", "EMP");
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void SearchSchemas_UpperLikeEscape_SystemFilterByVersion(bool includeSystem, bool hasOracleMaintained)
        {
            var query = OracleMetadata.SearchSchemas("%SCO%", includeSystem, hasOracleMaintained, OracleMetadata.SearchLimit);

            var columns = hasOracleMaintained ? "USERNAME, ORACLE_MAINTAINED" : "USERNAME";
            var filter = includeSystem ? ""
                : hasOracleMaintained ? " AND ORACLE_MAINTAINED = 'N'"
                : " AND USERNAME NOT IN " + KnownSystemList();
            Assert.Equal(Limited("SELECT " + columns + @" FROM ALL_USERS WHERE UPPER(USERNAME) LIKE :pattern ESCAPE '\'" + filter
                + " ORDER BY USERNAME"), Norm(query.Sql));
            AssertParameters(query, "pattern", "%SCO%", "limit", 501);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void SearchObjects_TreeTypesOnly_OrderedByOwnerTypeName(bool includeSystem, bool hasOracleMaintained)
        {
            var query = OracleMetadata.SearchObjects("%EMP%", includeSystem, hasOracleMaintained, 500);

            Assert.Equal(Limited(@"SELECT OWNER, OBJECT_TYPE, OBJECT_NAME FROM ALL_OBJECTS WHERE UPPER(OBJECT_NAME) LIKE :pattern ESCAPE '\'"
                + " AND OBJECT_TYPE IN " + TreeTypes + " AND OBJECT_NAME NOT LIKE 'BIN$%'"
                + ExpectedOwnerFilter("OWNER", includeSystem, hasOracleMaintained)
                + " ORDER BY OWNER, OBJECT_TYPE, OBJECT_NAME"), Norm(query.Sql));
            AssertParameters(query, "pattern", "%EMP%", "limit", 501);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void SearchColumns_JoinsAllObjectsForTableOrView_OrderedByOwnerTableColumnId(bool includeSystem, bool hasOracleMaintained)
        {
            var query = OracleMetadata.SearchColumns("%ID%", includeSystem, hasOracleMaintained, 500);

            Assert.Equal(Limited("SELECT c.OWNER, o.OBJECT_TYPE, c.TABLE_NAME AS OBJECT_NAME, c.COLUMN_NAME,"
                + " c.DATA_TYPE, c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE, c.CHAR_USED, c.CHAR_LENGTH"
                + " FROM ALL_TAB_COLUMNS c JOIN ALL_OBJECTS o ON o.OWNER = c.OWNER AND o.OBJECT_NAME = c.TABLE_NAME"
                + @" WHERE UPPER(c.COLUMN_NAME) LIKE :pattern ESCAPE '\' AND o.OBJECT_TYPE IN ('TABLE', 'VIEW') AND o.OBJECT_NAME NOT LIKE 'BIN$%'"
                + ExpectedOwnerFilter("c.OWNER", includeSystem, hasOracleMaintained)
                + " ORDER BY c.OWNER, c.TABLE_NAME, c.COLUMN_ID"), Norm(query.Sql));
            AssertParameters(query, "pattern", "%ID%", "limit", 501);
        }

        private static string ExpectedOwnerFilter(string column, bool includeSystem, bool hasOracleMaintained)
        {
            if (includeSystem)
                return "";
            return hasOracleMaintained
                ? " AND " + column + " IN (SELECT USERNAME FROM ALL_USERS WHERE ORACLE_MAINTAINED = 'N')"
                : " AND " + column + " NOT IN " + KnownSystemList();
        }

        [Fact]
        public void Search_LimitZero_NoRownumWrapper()
        {
            var queries = new[]
            {
                OracleMetadata.SearchSchemas("%A%", false, true, 0),
                OracleMetadata.SearchObjects("%A%", false, true, 0),
                OracleMetadata.SearchColumns("%A%", false, true, 0)
            };

            Assert.All(queries, q =>
            {
                Assert.DoesNotContain("ROWNUM", q.Sql);
                AssertParameters(q, "pattern", "%A%");
            });
        }

        [Fact]
        public void Search_WithoutSystemOn11g_NotInListQuotesEveryKnownSchema()
        {
            var sql = OracleMetadata.SearchObjects("%A%", false, false, 10).Sql;

            Assert.All(OracleMetadata.KnownSystemSchemas, s => Assert.Contains("'" + s + "'", sql));
            Assert.Contains("'SYS$UMF'", sql);
            Assert.Contains("'XS$NULL'", sql);
        }

        [Fact]
        public void KnownSystemSchemas_UniqueUppercaseLiterals_Includes11gDefaults()
        {
            var names = OracleMetadata.KnownSystemSchemas;

            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
            Assert.All(names, s =>
            {
                Assert.Equal(s.ToUpperInvariant(), s);
                Assert.DoesNotContain("'", s);
            });
            Assert.Contains("SYS", names);
            Assert.Contains("SYSTEM", names);
            Assert.Contains("EXFSYS", names);
            Assert.Contains("SYSMAN", names);
        }

        /// <summary>11g(ORACLE_MAINTAINED 없음)용 쿼리는 그 열을 전혀 쓰지 않아야 대체 쿼리가 성공한다.</summary>
        [Fact]
        public void WithoutOracleMaintained_NoQueryReferencesTheColumn()
        {
            var queries = new List<SqlQuery> { OracleMetadata.Schemas(false) };
            foreach (var includeSystem in new[] { true, false })
            {
                queries.Add(OracleMetadata.SearchSchemas("%A%", includeSystem, false, 10));
                queries.Add(OracleMetadata.SearchObjects("%A%", includeSystem, false, 10));
                queries.Add(OracleMetadata.SearchColumns("%A%", includeSystem, false, 10));
            }

            Assert.All(queries, q => Assert.DoesNotContain("ORACLE_MAINTAINED", q.Sql));
        }

        [Fact]
        public void UserValues_OnlyInParameters_NeverInSqlText()
        {
            const string owner = "O'BRIEN";
            const string name = "EMP\"; DROP TABLE X; --";
            const string pattern = "%O'BRIEN%";
            var queries = new List<SqlQuery>
            {
                OracleMetadata.GroupCounts(owner),
                OracleMetadata.Objects(owner, TreeGroups.Table, 10),
                OracleMetadata.Objects(owner, TreeGroups.Code, 0),
                OracleMetadata.Columns(owner, name)
            };
            foreach (var includeSystem in new[] { true, false })
                foreach (var hasOracleMaintained in new[] { true, false })
                {
                    queries.Add(OracleMetadata.SearchSchemas(pattern, includeSystem, hasOracleMaintained, 10));
                    queries.Add(OracleMetadata.SearchObjects(pattern, includeSystem, hasOracleMaintained, 10));
                    queries.Add(OracleMetadata.SearchColumns(pattern, includeSystem, hasOracleMaintained, 10));
                }

            Assert.All(queries, q =>
            {
                Assert.DoesNotContain("BRIEN", q.Sql);
                Assert.DoesNotContain("DROP", q.Sql);
                Assert.Contains(q.Parameters, p => Equals(p.Value, owner) || Equals(p.Value, pattern));
            });
            Assert.Equal(name, OracleMetadata.Columns(owner, name).Parameters.Single(p => p.Key == "name").Value);
        }

        private static Dictionary<string, SqlQuery> AllQueries()
        {
            var queries = new Dictionary<string, SqlQuery>
            {
                { "CurrentSchema", OracleMetadata.CurrentSchema() },
                { "Schemas(true)", OracleMetadata.Schemas(true) },
                { "Schemas(false)", OracleMetadata.Schemas(false) },
                { "GroupCounts", OracleMetadata.GroupCounts("SCOTT") },
                { "Columns", OracleMetadata.Columns("SCOTT", "EMP") },
                { "CompileErrors(owner)", OracleMetadata.CompileErrors("SCOTT", "PKG", new[] { "PACKAGE", "PACKAGE BODY" }, OracleMetadata.CompileErrorLimit) },
                { "CompileErrors(current)", OracleMetadata.CompileErrors(null, "P", new[] { "PROCEDURE" }, OracleMetadata.CompileErrorLimit) }
            };
            foreach (var group in new[] { TreeGroups.Table, TreeGroups.View, TreeGroups.Sequence, TreeGroups.Code })
                foreach (var limit in new[] { 0, 1000 })
                    queries.Add("Objects(" + group + "," + limit + ")", OracleMetadata.Objects("SCOTT", group, limit));
            foreach (var includeSystem in new[] { true, false })
                foreach (var hasOracleMaintained in new[] { true, false })
                    foreach (var limit in new[] { 0, 500 })
                    {
                        var args = "(" + includeSystem + "," + hasOracleMaintained + "," + limit + ")";
                        queries.Add("SearchSchemas" + args, OracleMetadata.SearchSchemas("%A%", includeSystem, hasOracleMaintained, limit));
                        queries.Add("SearchObjects" + args, OracleMetadata.SearchObjects("%A%", includeSystem, hasOracleMaintained, limit));
                        queries.Add("SearchColumns" + args, OracleMetadata.SearchColumns("%A%", includeSystem, hasOracleMaintained, limit));
                    }
            return queries;
        }

        public static IEnumerable<object[]> AllQueryNames()
        {
            return AllQueries().Keys.Select(k => new object[] { k });
        }

        /// <summary>
        /// BindByName: SQL의 :이름과 Parameters가 1:1이어야 한다(빠지면 ORA-01008, 남으면 ORA-01036).
        /// 끝의 ';'는 ODP.NET에서 ORA-00911. 따옴표·괄호 짝은 SQL을 손으로 이어 붙일 때 생기기 쉬운 실수.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllQueryNames))]
        public void EveryQuery_BindsMatchParameters_NoSemicolon_BalancedQuotesAndParentheses(string name)
        {
            var query = AllQueries()[name];
            var sql = query.Sql;

            Assert.NotEqual(';', sql.TrimEnd().Last());
            Assert.Equal(0, sql.Count(ch => ch == '\'') % 2);
            var outsideLiterals = Regex.Replace(sql, "'[^']*'", "''");
            var depth = 0;
            foreach (var ch in outsideLiterals)
            {
                if (ch == '(')
                    depth++;
                else if (ch == ')')
                    Assert.True(--depth >= 0, "닫는 괄호가 먼저 나옴");
            }
            Assert.Equal(0, depth);

            var binds = Regex.Matches(outsideLiterals, @":(\w+)").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(binds.Distinct().OrderBy(b => b, StringComparer.Ordinal), query.Parameters.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(query.Parameters, p =>
            {
                Assert.DoesNotContain(":", p.Key);
                Assert.True(p.Value is string || p.Value is int, p.Key + " 값 형식: " + p.Value.GetType());
            });
        }

        // ---------- 결과 읽기 (ODP.NET처럼 NUMBER는 decimal, 문자열은 string, NULL은 DBNull) ----------

        private static List<T> ReadAll<T>(DataTable table, Func<IDataRecord, T> read)
        {
            var rows = new List<T>();
            using (var reader = table.CreateDataReader())
            {
                while (reader.Read())
                    rows.Add(read(reader));
            }
            return rows;
        }

        private static object As(Type type, long value)
        {
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void ReadSchema_WithOracleMaintained_FlagDecides()
        {
            var table = new DataTable();
            table.Columns.Add("USERNAME", typeof(string));
            table.Columns.Add("ORACLE_MAINTAINED", typeof(string));
            table.Rows.Add("SYS", "Y");
            table.Rows.Add("SCOTT", "N");
            table.Rows.Add("SYSTEM", "N");          // 목록에 있어도 플래그가 우선
            table.Rows.Add("APP", DBNull.Value);

            var schemas = ReadAll(table, r => OracleMetadata.ReadSchema(r, true));

            Assert.Equal(new[] { "SYS", "SCOTT", "SYSTEM", "APP" }, schemas.Select(s => s.Name));
            Assert.Equal(new[] { true, false, false, false }, schemas.Select(s => s.OracleMaintained));
        }

        [Fact]
        public void ReadSchema_WithoutOracleMaintained_KnownSystemSchemasDecide()
        {
            var table = new DataTable();
            table.Columns.Add("USERNAME", typeof(string));      // 11g 쿼리에는 ORACLE_MAINTAINED 열이 없다
            table.Rows.Add("SYS");
            table.Rows.Add("XS$NULL");
            table.Rows.Add("SCOTT");
            table.Rows.Add("sys");                                // 따옴표로 만든 소문자 사용자는 다른 사용자

            var schemas = ReadAll(table, r => OracleMetadata.ReadSchema(r, false));

            Assert.Equal(new[] { "SYS", "XS$NULL", "SCOTT", "sys" }, schemas.Select(s => s.Name));
            Assert.Equal(new[] { true, true, false, false }, schemas.Select(s => s.OracleMaintained));
        }

        [Fact]
        public void ReadGroupCounts_MergesProcedureFunctionPackageIntoCode_MissingGroupsZero()
        {
            var table = new DataTable();
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("CNT", typeof(decimal));
            table.Rows.Add("TABLE", 12m);
            table.Rows.Add("PROCEDURE", 2m);
            table.Rows.Add("FUNCTION", 3m);
            table.Rows.Add("PACKAGE", 4m);
            table.Rows.Add("SYNONYM", 9m);          // 트리에 없는 종류는 무시

            var counts = OracleMetadata.ReadGroupCounts(table.CreateDataReader());

            Assert.Equal(4, counts.Count);
            Assert.Equal(12, counts[TreeGroups.Table]);
            Assert.Equal(0, counts[TreeGroups.View]);
            Assert.Equal(0, counts[TreeGroups.Sequence]);
            Assert.Equal(9, counts[TreeGroups.Code]);
        }

        [Theory]
        [InlineData(typeof(decimal))]
        [InlineData(typeof(int))]
        [InlineData(typeof(long))]
        [InlineData(typeof(double))]
        public void ReadGroupCounts_CountAsAnyNumericTypeOrNull(Type countType)
        {
            var table = new DataTable();
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("CNT", countType);
            table.Rows.Add("VIEW", As(countType, 5));
            table.Rows.Add("SEQUENCE", DBNull.Value);
            table.Rows.Add("FUNCTION", As(countType, 1));

            var counts = OracleMetadata.ReadGroupCounts(table.CreateDataReader());

            Assert.Equal(0, counts[TreeGroups.Table]);
            Assert.Equal(5, counts[TreeGroups.View]);
            Assert.Equal(0, counts[TreeGroups.Sequence]);
            Assert.Equal(1, counts[TreeGroups.Code]);
        }

        [Fact]
        public void ReadGroupCounts_NoRows_AllFourGroupsZero()
        {
            var table = new DataTable();
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("CNT", typeof(decimal));

            var counts = OracleMetadata.ReadGroupCounts(table.CreateDataReader());

            Assert.Equal(new[] { TreeGroups.Code, TreeGroups.Sequence, TreeGroups.Table, TreeGroups.View }, counts.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(counts.Values, v => Assert.Equal(0, v));
        }

        private static DataTable ObjectTable(Type numRowsType)
        {
            var table = new DataTable();
            table.Columns.Add("OWNER", typeof(string));
            table.Columns.Add("OBJECT_NAME", typeof(string));
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("STATUS", typeof(string));
            table.Columns.Add("NUM_ROWS", numRowsType);
            return table;
        }

        [Fact]
        public void ReadObject_ReadsFields_NumRowsNullWithoutStatistics()
        {
            var table = ObjectTable(typeof(decimal));
            table.Rows.Add("SCOTT", "EMP", "TABLE", "VALID", 14m);
            table.Rows.Add("SCOTT", "BONUS", "TABLE", "VALID", DBNull.Value);
            table.Rows.Add("SCOTT", "RAISE_SAL", "PROCEDURE", "INVALID", DBNull.Value);

            var objects = ReadAll(table, OracleMetadata.ReadObject);

            Assert.Equal(new[] { "EMP", "BONUS", "RAISE_SAL" }, objects.Select(o => o.Name));
            Assert.Equal(new[] { "TABLE", "TABLE", "PROCEDURE" }, objects.Select(o => o.Type));
            Assert.Equal(new[] { "VALID", "VALID", "INVALID" }, objects.Select(o => o.Status));
            Assert.All(objects, o => Assert.Equal("SCOTT", o.Owner));
            Assert.Equal(new long?[] { 14, null, null }, objects.Select(o => o.NumRows));
        }

        [Theory]
        [InlineData(typeof(decimal), 3000000000L)]
        [InlineData(typeof(long), 3000000000L)]
        [InlineData(typeof(double), 3000000000L)]
        [InlineData(typeof(int), 1234L)]
        public void ReadObject_NumRowsAsAnyNumericType(Type type, long value)
        {
            var table = ObjectTable(type);
            table.Rows.Add("SCOTT", "BIG", "TABLE", "VALID", As(type, value));

            var info = ReadAll(table, OracleMetadata.ReadObject).Single();

            Assert.Equal((long?)value, info.NumRows);
        }

        private static DataTable ColumnTable(Type numberType)
        {
            var table = new DataTable();
            table.Columns.Add("OWNER", typeof(string));
            table.Columns.Add("TABLE_NAME", typeof(string));
            table.Columns.Add("COLUMN_NAME", typeof(string));
            table.Columns.Add("DATA_TYPE", typeof(string));
            table.Columns.Add("DATA_LENGTH", numberType);
            table.Columns.Add("DATA_PRECISION", numberType);
            table.Columns.Add("DATA_SCALE", numberType);
            table.Columns.Add("NULLABLE", typeof(string));
            table.Columns.Add("COLUMN_ID", numberType);
            table.Columns.Add("CHAR_USED", typeof(string));
            table.Columns.Add("CHAR_LENGTH", numberType);
            table.Columns.Add("IS_PK", numberType);
            return table;
        }

        [Theory]
        [InlineData(typeof(decimal))]
        [InlineData(typeof(int))]
        [InlineData(typeof(long))]
        [InlineData(typeof(double))]
        public void ReadColumn_ReadsFieldsAndFormatsType_NumbersAsAnyNumericType(Type numberType)
        {
            var table = ColumnTable(numberType);
            var nul = DBNull.Value;
            table.Rows.Add("SCOTT", "EMP", "EMPNO", "NUMBER", As(numberType, 22), As(numberType, 4), As(numberType, 0), "N", As(numberType, 1), nul, As(numberType, 0), As(numberType, 1));
            table.Rows.Add("SCOTT", "EMP", "ENAME", "VARCHAR2", As(numberType, 40), nul, nul, "Y", As(numberType, 2), "C", As(numberType, 10), As(numberType, 0));
            table.Rows.Add("SCOTT", "EMP", "SAL", "NUMBER", As(numberType, 22), As(numberType, 7), As(numberType, 2), "Y", As(numberType, 3), nul, As(numberType, 0), As(numberType, 0));
            table.Rows.Add("SCOTT", "EMP", "HIREDATE", "DATE", As(numberType, 7), nul, nul, "Y", As(numberType, 4), nul, As(numberType, 0), As(numberType, 0));
            // 값이 비어도 예외 없이: 형식 "", NULL 허용으로 봄, 기본 키 아님, 위치 0
            table.Rows.Add("SCOTT", "EMP", "ODD", nul, nul, nul, nul, nul, nul, nul, nul, nul);

            var columns = ReadAll(table, OracleMetadata.ReadColumn);

            Assert.Equal(new[] { "EMPNO", "ENAME", "SAL", "HIREDATE", "ODD" }, columns.Select(c => c.Name));
            Assert.Equal(new[] { "NUMBER(4)", "VARCHAR2(10 CHAR)", "NUMBER(7,2)", "DATE", "" }, columns.Select(c => c.TypeLabel));
            Assert.Equal(new[] { false, true, true, true, true }, columns.Select(c => c.Nullable));
            Assert.Equal(new[] { true, false, false, false, false }, columns.Select(c => c.PrimaryKey));
            Assert.Equal(new[] { 1, 2, 3, 4, 0 }, columns.Select(c => c.Position));
            Assert.All(columns, c =>
            {
                Assert.Equal("SCOTT", c.Owner);
                Assert.Equal("EMP", c.ObjectName);
            });
        }

        [Fact]
        public void ReadObjectHit_HasNoColumn()
        {
            var table = new DataTable();
            table.Columns.Add("OWNER", typeof(string));
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("OBJECT_NAME", typeof(string));
            table.Rows.Add("SCOTT", "PACKAGE", "EMP_PKG");

            var hit = ReadAll(table, OracleMetadata.ReadObjectHit).Single();

            Assert.Equal("SCOTT", hit.Owner);
            Assert.Equal("PACKAGE", hit.ObjectType);
            Assert.Equal("EMP_PKG", hit.ObjectName);
            Assert.Null(hit.ColumnName);
            Assert.Null(hit.ColumnType);
        }

        [Fact]
        public void ReadColumnHit_ReadsObjectAndFormatsColumnType()
        {
            var table = new DataTable();
            table.Columns.Add("OWNER", typeof(string));
            table.Columns.Add("OBJECT_TYPE", typeof(string));
            table.Columns.Add("OBJECT_NAME", typeof(string));
            table.Columns.Add("COLUMN_NAME", typeof(string));
            table.Columns.Add("DATA_TYPE", typeof(string));
            table.Columns.Add("DATA_LENGTH", typeof(decimal));
            table.Columns.Add("DATA_PRECISION", typeof(decimal));
            table.Columns.Add("DATA_SCALE", typeof(decimal));
            table.Columns.Add("CHAR_USED", typeof(string));
            table.Columns.Add("CHAR_LENGTH", typeof(decimal));
            table.Rows.Add("SCOTT", "VIEW", "EMP_V", "ENAME", "VARCHAR2", 10m, DBNull.Value, DBNull.Value, "B", 10m);
            table.Rows.Add("HR", "TABLE", "EMPLOYEES", "EMPLOYEE_ID", "NUMBER", 22m, 6m, 0m, DBNull.Value, 0m);

            var hits = ReadAll(table, OracleMetadata.ReadColumnHit);

            Assert.Equal(new[] { "SCOTT", "HR" }, hits.Select(h => h.Owner));
            Assert.Equal(new[] { "VIEW", "TABLE" }, hits.Select(h => h.ObjectType));
            Assert.Equal(new[] { "EMP_V", "EMPLOYEES" }, hits.Select(h => h.ObjectName));
            Assert.Equal(new[] { "ENAME", "EMPLOYEE_ID" }, hits.Select(h => h.ColumnName));
            Assert.Equal(new[] { "VARCHAR2(10 BYTE)", "NUMBER(6)" }, hits.Select(h => h.ColumnType));
        }

        // ---------- 형식 표시 (ALL_TAB_COLUMNS 실제 값 조합) ----------

        [Theory]
        [InlineData("VARCHAR2", 40, null, null, "C", 10, "VARCHAR2(10 CHAR)")]
        [InlineData("VARCHAR2", 10, null, null, "B", 10, "VARCHAR2(10 BYTE)")]
        [InlineData("VARCHAR2", 10, null, null, null, 10, "VARCHAR2(10 BYTE)")]
        [InlineData("CHAR", 1, null, null, "B", 1, "CHAR(1 BYTE)")]
        [InlineData("CHAR", 12, null, null, "C", 3, "CHAR(3 CHAR)")]
        [InlineData("NVARCHAR2", 40, null, null, "C", 20, "NVARCHAR2(20)")]
        [InlineData("NCHAR", 10, null, null, "C", 5, "NCHAR(5)")]
        [InlineData("NUMBER", 22, null, null, null, 0, "NUMBER")]
        [InlineData("NUMBER", 22, 10, 0, null, 0, "NUMBER(10)")]
        [InlineData("NUMBER", 22, 10, null, null, 0, "NUMBER(10)")]
        [InlineData("NUMBER", 22, 7, 2, null, 0, "NUMBER(7,2)")]
        [InlineData("NUMBER", 22, 5, -2, null, 0, "NUMBER(5,-2)")]
        // INTEGER·NUMBER(*,0)은 정밀도 null, 소수 자릿수 0으로 저장된다. 계약("정밀도 없으면 NUMBER")대로 NUMBER로 보인다.
        [InlineData("NUMBER", 22, null, 0, null, 0, "NUMBER")]
        [InlineData("FLOAT", 22, 126, null, null, 0, "FLOAT(126)")]
        [InlineData("FLOAT", 22, 63, null, null, 0, "FLOAT(63)")]
        [InlineData("RAW", 16, null, null, null, 0, "RAW(16)")]
        [InlineData("DATE", 7, null, null, null, 0, "DATE")]
        [InlineData("CLOB", 4000, null, null, null, 0, "CLOB")]
        // TIMESTAMP·INTERVAL은 DATA_SCALE(·DATA_PRECISION)에 값이 있어도 DATA_TYPE에 이미 정밀도가 들어 있다.
        [InlineData("TIMESTAMP(6)", 11, null, 6, null, 0, "TIMESTAMP(6)")]
        [InlineData("TIMESTAMP(6) WITH TIME ZONE", 13, null, 6, null, 0, "TIMESTAMP(6) WITH TIME ZONE")]
        [InlineData("INTERVAL DAY(2) TO SECOND(6)", 11, 2, 6, null, 0, "INTERVAL DAY(2) TO SECOND(6)")]
        [InlineData("BINARY_DOUBLE", 8, null, null, null, 0, "BINARY_DOUBLE")]
        [InlineData("XMLTYPE", 2000, null, null, null, 0, "XMLTYPE")]
        public void FormatColumnType_Table(string dataType, int? dataLength, int? precision, int? scale, string charUsed, int? charLength, string expected)
        {
            Assert.Equal(expected, OracleMetadata.FormatColumnType(dataType, dataLength, precision, scale, charUsed, charLength));
        }

        [Fact]
        public void FormatColumnType_DecimalsWithTrailingZeros_PrintAsIntegers()
        {
            Assert.Equal("NUMBER(10,2)", OracleMetadata.FormatColumnType("NUMBER", 22.0m, 10.00m, 2.0m, null, 0m));
            Assert.Equal("VARCHAR2(20 CHAR)", OracleMetadata.FormatColumnType("VARCHAR2", 80m, null, null, "C", 20.0m));
            Assert.Equal("RAW(16)", OracleMetadata.FormatColumnType("RAW", 16.000m, null, null, null, 0m));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatColumnType_NoDataType_Empty(string dataType)
        {
            Assert.Equal("", OracleMetadata.FormatColumnType(dataType, 22m, null, null, null, null));
        }

        [Fact]
        public void FormatColumnType_MissingLengths_TypeOnly()
        {
            Assert.Equal("VARCHAR2", OracleMetadata.FormatColumnType("VARCHAR2", null, null, null, "B", null));
            Assert.Equal("NVARCHAR2", OracleMetadata.FormatColumnType("NVARCHAR2", 40m, null, null, "C", null));
            Assert.Equal("FLOAT", OracleMetadata.FormatColumnType("FLOAT", 22m, null, null, null, null));
            Assert.Equal("RAW", OracleMetadata.FormatColumnType("RAW", null, null, null, null, null));
        }

        // ---------- ORA-00904 판단 ----------

        private static OracleException NewOracleException(int number, string message)
        {
            // ODP.NET은 OracleException 생성자를 공개하지 않아 내부 생성자(errCode, dataSrc, procedure, errMsg, parseErrorOffset)를 쓴다.
            var ctor = typeof(OracleException).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(string), typeof(string), typeof(string), typeof(int) }, null);
            Assert.NotNull(ctor);
            return (OracleException)ctor.Invoke(new object[] { number, "", "", message, 0 });
        }

        [Fact]
        public void IsInvalidIdentifier_OracleExceptionNumber904_EvenWithoutCodeInMessage()
        {
            var ex = NewOracleException(904, "invalid identifier");

            Assert.Equal(904, ex.Number);
            Assert.DoesNotContain("ORA-00904", ex.Message, StringComparison.Ordinal);   // 번호만으로 판단되는지 확인
            Assert.True(OracleMetadata.IsInvalidIdentifier(ex));
        }

        [Fact]
        public void IsInvalidIdentifier_OtherOracleErrors_False()
        {
            Assert.False(OracleMetadata.IsInvalidIdentifier(NewOracleException(942, "ORA-00942: table or view does not exist")));
            Assert.False(OracleMetadata.IsInvalidIdentifier(NewOracleException(1031, "ORA-01031: insufficient privileges")));
        }

        [Theory]
        [InlineData("ORA-00904: \"ORACLE_MAINTAINED\": invalid identifier", true)]
        [InlineData("ORA-00904: \"ORACLE_MAINTAINED\": 부적합한 식별자", true)]
        [InlineData("ORA-06550: line 1, column 7:\nPL/SQL: ORA-00904: \"X\": invalid identifier", true)]
        [InlineData("ORA-00942: table or view does not exist", false)]
        [InlineData("ORA-01031: insufficient privileges", false)]
        [InlineData("invalid identifier", false)]
        [InlineData("", false)]
        public void IsInvalidIdentifier_PlainExceptionMessage(string message, bool expected)
        {
            Assert.Equal(expected, OracleMetadata.IsInvalidIdentifier(new InvalidOperationException(message)));
        }

        [Fact]
        public void IsInvalidIdentifier_UnwrapsNestedAggregateAndInnerExceptions()
        {
            var oracle = NewOracleException(904, "invalid identifier");
            var nested = new AggregateException(
                new InvalidOperationException("다른 오류"),
                new AggregateException(new TargetInvocationException(new Exception("감쌈", oracle))));

            Assert.True(OracleMetadata.IsInvalidIdentifier(nested));
            Assert.True(OracleMetadata.IsInvalidIdentifier(new Exception("바깥", new Exception("중간", oracle))));
            Assert.True(OracleMetadata.IsInvalidIdentifier(Task.FromException(oracle).Exception));
            Assert.True(OracleMetadata.IsInvalidIdentifier(new AggregateException(new Exception("ORA-00904: \"ORACLE_MAINTAINED\": invalid identifier"))));
        }

        [Fact]
        public void IsInvalidIdentifier_NullOrWrappedOtherErrors_False()
        {
            Assert.False(OracleMetadata.IsInvalidIdentifier(null));
            Assert.False(OracleMetadata.IsInvalidIdentifier(new AggregateException()));
            Assert.False(OracleMetadata.IsInvalidIdentifier(new AggregateException(
                new InvalidOperationException("x"),
                new Exception("바깥", NewOracleException(942, "ORA-00942: table or view does not exist")))));
        }

        // ---------- ALL_* 뷰를 흉내 낸 SQLite 표에 실행 ----------

        /// <summary>
        /// SCOTT: 테이블 EMP(파티션 행 있음)·DEPT(통계 없음)·휴지통 테이블, 뷰 EMP_V·EMPX, 시퀀스, 프로시저·함수·패키지(+BODY), 인덱스·동의어·클러스터.
        /// HR: 테이블 EMPLOYEES. SYS: DUAL, TEMP_UNDO. 사용자 SYSADM_APP은 내장이 아님.
        /// 행은 일부러 정렬하지 않고 넣는다(ORDER BY 확인).
        /// </summary>
        private static SqliteConnection OpenDictionary(bool hasOracleMaintained)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = (hasOracleMaintained
                        ? "CREATE TABLE ALL_USERS (USERNAME TEXT, ORACLE_MAINTAINED TEXT);"
                          + " INSERT INTO ALL_USERS VALUES ('SCOTT', 'N'), ('HR', 'N'), ('SYSADM_APP', 'N'), ('SYS', 'Y'), ('SYSTEM', 'Y'), ('MDSYS', 'Y');"
                        : "CREATE TABLE ALL_USERS (USERNAME TEXT);"     // 11g: ORACLE_MAINTAINED 열이 없다
                          + " INSERT INTO ALL_USERS VALUES ('SCOTT'), ('HR'), ('SYSADM_APP'), ('SYS'), ('SYSTEM'), ('MDSYS');")
                    + @"
CREATE TABLE ALL_OBJECTS (OWNER TEXT, OBJECT_NAME TEXT, OBJECT_TYPE TEXT, STATUS TEXT);
CREATE TABLE ALL_TABLES (OWNER TEXT, TABLE_NAME TEXT, NUM_ROWS INTEGER);
CREATE TABLE ALL_TAB_COLUMNS (OWNER TEXT, TABLE_NAME TEXT, COLUMN_NAME TEXT, DATA_TYPE TEXT, DATA_LENGTH INTEGER, DATA_PRECISION INTEGER,
    DATA_SCALE INTEGER, NULLABLE TEXT, COLUMN_ID INTEGER, CHAR_USED TEXT, CHAR_LENGTH INTEGER);
CREATE TABLE ALL_CONSTRAINTS (OWNER TEXT, CONSTRAINT_NAME TEXT, CONSTRAINT_TYPE TEXT, TABLE_NAME TEXT);
CREATE TABLE ALL_CONS_COLUMNS (OWNER TEXT, CONSTRAINT_NAME TEXT, TABLE_NAME TEXT, COLUMN_NAME TEXT, POSITION INTEGER);

INSERT INTO ALL_OBJECTS VALUES
    ('SCOTT', 'EMP', 'TABLE', 'VALID'),
    ('SCOTT', 'EMP', 'TABLE PARTITION', 'VALID'),
    ('SCOTT', 'DEPT', 'TABLE', 'VALID'),
    ('SCOTT', 'BIN$hz5PAF6GQl+8xYm4mXrD3A==$0', 'TABLE', 'VALID'),
    ('SCOTT', 'EMP_V', 'VIEW', 'VALID'),
    ('SCOTT', 'EMPX', 'VIEW', 'INVALID'),
    ('SCOTT', 'EMP_SEQ', 'SEQUENCE', 'VALID'),
    ('SCOTT', 'RAISE_SAL', 'PROCEDURE', 'INVALID'),
    ('SCOTT', 'EMP_PKG', 'PACKAGE', 'VALID'),
    ('SCOTT', 'EMP_PKG', 'PACKAGE BODY', 'VALID'),
    ('SCOTT', 'CALC_BONUS', 'FUNCTION', 'VALID'),
    ('SCOTT', 'PK_EMP', 'INDEX', 'VALID'),
    ('SCOTT', 'EMP_SYN', 'SYNONYM', 'VALID'),
    ('SCOTT', 'EMP_CLU', 'CLUSTER', 'VALID'),
    ('HR', 'EMPLOYEES', 'TABLE', 'VALID'),
    ('SYS', 'DUAL', 'TABLE', 'VALID'),
    ('SYS', 'TEMP_UNDO', 'TABLE', 'VALID');

INSERT INTO ALL_TABLES VALUES
    ('SCOTT', 'EMP', 14), ('SCOTT', 'DEPT', NULL), ('SCOTT', 'BIN$hz5PAF6GQl+8xYm4mXrD3A==$0', 3),
    ('HR', 'EMPLOYEES', 107), ('SYS', 'DUAL', 1), ('SYS', 'TEMP_UNDO', 0);

INSERT INTO ALL_TAB_COLUMNS VALUES
    ('SCOTT', 'EMP', 'SAL', 'NUMBER', 22, 7, 2, 'Y', 3, NULL, 0),
    ('SCOTT', 'EMP', 'EMPNO', 'NUMBER', 22, 4, 0, 'N', 1, NULL, 0),
    ('SCOTT', 'EMP', 'HIREDATE', 'DATE', 7, NULL, NULL, 'Y', 5, NULL, 0),
    ('SCOTT', 'EMP', 'ENAME', 'VARCHAR2', 10, NULL, NULL, 'Y', 2, 'B', 10),
    ('SCOTT', 'EMP', 'DEPTNO', 'NUMBER', 22, 2, 0, 'Y', 4, NULL, 0),
    ('SCOTT', 'DEPT', 'DNAME', 'VARCHAR2', 56, NULL, NULL, 'Y', 2, 'C', 14),
    ('SCOTT', 'DEPT', 'DEPTNO', 'NUMBER', 22, 2, 0, 'N', 1, NULL, 0),
    ('SCOTT', 'EMP_V', 'EMPNO', 'NUMBER', 22, 4, 0, 'N', 1, NULL, 0),
    ('SCOTT', 'EMP_V', 'ENAME', 'VARCHAR2', 10, NULL, NULL, 'Y', 2, 'B', 10),
    ('SCOTT', 'BIN$hz5PAF6GQl+8xYm4mXrD3A==$0', 'EMPNO', 'NUMBER', 22, 4, 0, 'N', 1, NULL, 0),
    ('SCOTT', 'EMP_CLU', 'EMPNO_KEY', 'NUMBER', 22, NULL, NULL, 'Y', 1, NULL, 0),
    ('SYS', 'TEMP_UNDO', 'EMP_ID', 'NUMBER', 22, NULL, NULL, 'Y', 1, NULL, 0),
    ('SYS', 'DUAL', 'DUMMY', 'VARCHAR2', 1, NULL, NULL, 'Y', 1, 'B', 1),
    ('HR', 'EMPLOYEES', 'LAST_NAME', 'VARCHAR2', 25, NULL, NULL, 'N', 2, 'B', 25),
    ('HR', 'EMPLOYEES', 'EMPLOYEE_ID', 'NUMBER', 22, 6, 0, 'N', 1, NULL, 0);

INSERT INTO ALL_CONSTRAINTS VALUES
    ('SCOTT', 'PK_EMP', 'P', 'EMP'), ('SCOTT', 'FK_DEPTNO', 'R', 'EMP'), ('SCOTT', 'SYS_C0012', 'C', 'EMP'),
    ('SCOTT', 'PK_DEPT', 'P', 'DEPT'), ('SCOTT', 'UK_DNAME', 'U', 'DEPT'), ('HR', 'EMP_ID_PK', 'P', 'EMPLOYEES');

INSERT INTO ALL_CONS_COLUMNS VALUES
    ('SCOTT', 'PK_EMP', 'EMP', 'EMPNO', 1), ('SCOTT', 'FK_DEPTNO', 'EMP', 'DEPTNO', 1), ('SCOTT', 'SYS_C0012', 'EMP', 'EMPNO', NULL),
    ('SCOTT', 'PK_DEPT', 'DEPT', 'DEPTNO', 1), ('SCOTT', 'UK_DNAME', 'DEPT', 'DNAME', 1), ('HR', 'EMP_ID_PK', 'EMPLOYEES', 'EMPLOYEE_ID', 1);
";
                command.ExecuteNonQuery();
            }
            return connection;
        }

        private static SqliteCommand Prepare(SqliteConnection connection, SqlQuery query)
        {
            var command = connection.CreateCommand();
            // SQLite에는 ROWNUM이 없어 같은 뜻의 LIMIT으로 바꾼다(감싸는 형태 자체는 위 SQL 텍스트 테스트에서 확인).
            command.CommandText = query.Sql.Replace(") WHERE ROWNUM <= :limit", ") LIMIT :limit");
            foreach (var parameter in query.Parameters)
                command.Parameters.AddWithValue(parameter.Key, parameter.Value);
            return command;
        }

        private static List<T> Run<T>(SqliteConnection connection, SqlQuery query, Func<IDataRecord, T> read)
        {
            using (var command = Prepare(connection, query))
            using (var reader = command.ExecuteReader())
            {
                var rows = new List<T>();
                while (reader.Read())
                    rows.Add(read(reader));
                return rows;
            }
        }

        [Fact]
        public void Simulated_GroupCounts_TreeTypesOnly_NoRecycleBinPartitionOrPackageBody()
        {
            using (var db = OpenDictionary(true))
            using (var command = Prepare(db, OracleMetadata.GroupCounts("SCOTT")))
            using (var reader = command.ExecuteReader())
            {
                var counts = OracleMetadata.ReadGroupCounts(reader);

                Assert.Equal(2, counts[TreeGroups.Table]);
                Assert.Equal(2, counts[TreeGroups.View]);
                Assert.Equal(1, counts[TreeGroups.Sequence]);
                Assert.Equal(3, counts[TreeGroups.Code]);
            }
        }

        [Fact]
        public void Simulated_ObjectsTable_NumRowsFromAllTables_ByName()
        {
            using (var db = OpenDictionary(true))
            {
                var objects = Run(db, OracleMetadata.Objects("SCOTT", TreeGroups.Table, 0), OracleMetadata.ReadObject);

                Assert.Equal(new[] { "DEPT", "EMP" }, objects.Select(o => o.Name));
                Assert.Equal(new long?[] { null, 14 }, objects.Select(o => o.NumRows));
                Assert.All(objects, o =>
                {
                    Assert.Equal("SCOTT", o.Owner);
                    Assert.Equal("TABLE", o.Type);
                    Assert.Equal("VALID", o.Status);
                });
            }
        }

        [Fact]
        public void Simulated_ObjectsCode_ProcedureFunctionPackage_ByName_NoNumRows()
        {
            using (var db = OpenDictionary(true))
            {
                var objects = Run(db, OracleMetadata.Objects("SCOTT", TreeGroups.Code, 0), OracleMetadata.ReadObject);

                Assert.Equal(new[] { "CALC_BONUS", "EMP_PKG", "RAISE_SAL" }, objects.Select(o => o.Name));
                Assert.Equal(new[] { "FUNCTION", "PACKAGE", "PROCEDURE" }, objects.Select(o => o.Type));
                Assert.Equal(new[] { "VALID", "VALID", "INVALID" }, objects.Select(o => o.Status));
                Assert.All(objects, o => Assert.Null(o.NumRows));
            }
        }

        [Fact]
        public void Simulated_ObjectsWithLimit_ReturnsOneExtraRowWhenMore()
        {
            using (var db = OpenDictionary(true))
            {
                var views = Run(db, OracleMetadata.Objects("SCOTT", TreeGroups.View, 1), OracleMetadata.ReadObject);
                var tables = Run(db, OracleMetadata.Objects("SCOTT", TreeGroups.Table, 2), OracleMetadata.ReadObject);

                Assert.Equal(new[] { "EMPX", "EMP_V" }, views.Select(o => o.Name));     // limit 1 + "더 있음" 판단용 1행
                Assert.Equal(new[] { "DEPT", "EMP" }, tables.Select(o => o.Name));     // 2개뿐이라 limit 2에서 더 없음
            }
        }

        [Fact]
        public void Simulated_Columns_ByColumnId_PrimaryKeyOnlyFromThatTablesPrimaryKey()
        {
            using (var db = OpenDictionary(true))
            {
                var emp = Run(db, OracleMetadata.Columns("SCOTT", "EMP"), OracleMetadata.ReadColumn);
                var dept = Run(db, OracleMetadata.Columns("SCOTT", "DEPT"), OracleMetadata.ReadColumn);

                Assert.Equal(new[] { "EMPNO", "ENAME", "SAL", "DEPTNO", "HIREDATE" }, emp.Select(c => c.Name));
                Assert.Equal(new[] { 1, 2, 3, 4, 5 }, emp.Select(c => c.Position));
                Assert.Equal(new[] { "NUMBER(4)", "VARCHAR2(10 BYTE)", "NUMBER(7,2)", "NUMBER(2)", "DATE" }, emp.Select(c => c.TypeLabel));
                Assert.Equal(new[] { false, true, true, true, true }, emp.Select(c => c.Nullable));
                // DEPTNO: EMP에서는 외래 키뿐이고, 같은 이름 열이 기본 키인 DEPT의 제약이 섞이면 안 된다. NOT NULL 검사 제약(C)도 기본 키 아님.
                Assert.Equal(new[] { true, false, false, false, false }, emp.Select(c => c.PrimaryKey));
                Assert.All(emp, c =>
                {
                    Assert.Equal("SCOTT", c.Owner);
                    Assert.Equal("EMP", c.ObjectName);
                });

                Assert.Equal(new[] { "DEPTNO", "DNAME" }, dept.Select(c => c.Name));
                Assert.Equal(new[] { true, false }, dept.Select(c => c.PrimaryKey));    // 고유 키(U)는 기본 키 아님
                Assert.Equal(new[] { "NUMBER(2)", "VARCHAR2(14 CHAR)" }, dept.Select(c => c.TypeLabel));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Simulated_SearchObjects_EscapedUnderscore_TreeTypesOnly_SystemFiltered(bool hasOracleMaintained)
        {
            using (var db = OpenDictionary(hasOracleMaintained))
            {
                // SqlScript.ContainsPattern("emp_")의 결과. '_'가 이스케이프되지 않으면 EMPX·EMPLOYEES도 걸린다.
                const string pattern = @"%EMP\_%";

                var all = Run(db, OracleMetadata.SearchObjects(pattern, true, hasOracleMaintained, 500), OracleMetadata.ReadObjectHit);
                var user = Run(db, OracleMetadata.SearchObjects(pattern, false, hasOracleMaintained, 500), OracleMetadata.ReadObjectHit);

                Assert.Equal(new[] { "SCOTT PACKAGE EMP_PKG", "SCOTT SEQUENCE EMP_SEQ", "SCOTT VIEW EMP_V", "SYS TABLE TEMP_UNDO" },
                    all.Select(h => h.Owner + " " + h.ObjectType + " " + h.ObjectName));
                Assert.All(all, h => Assert.Null(h.ColumnName));
                Assert.Equal(new[] { "EMP_PKG", "EMP_SEQ", "EMP_V" }, user.Select(h => h.ObjectName));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Simulated_SearchColumns_TablesAndViewsOnce_NoClusterOrRecycleBin_SystemFiltered(bool hasOracleMaintained)
        {
            using (var db = OpenDictionary(hasOracleMaintained))
            {
                var all = Run(db, OracleMetadata.SearchColumns("%EMP%", true, hasOracleMaintained, 500), OracleMetadata.ReadColumnHit);
                var user = Run(db, OracleMetadata.SearchColumns("%EMP%", false, hasOracleMaintained, 500), OracleMetadata.ReadColumnHit);

                Assert.Equal(new[] { "HR TABLE EMPLOYEES.EMPLOYEE_ID NUMBER(6)", "SCOTT TABLE EMP.EMPNO NUMBER(4)", "SCOTT VIEW EMP_V.EMPNO NUMBER(4)", "SYS TABLE TEMP_UNDO.EMP_ID NUMBER" },
                    all.Select(h => h.Owner + " " + h.ObjectType + " " + h.ObjectName + "." + h.ColumnName + " " + h.ColumnType));
                Assert.Equal(new[] { "EMPLOYEES", "EMP", "EMP_V" }, user.Select(h => h.ObjectName));
            }
        }

        [Fact]
        public void Simulated_SearchLimit_ReturnsLimitPlusOneRows()
        {
            using (var db = OpenDictionary(true))
            {
                var hits = Run(db, OracleMetadata.SearchColumns("%EMP%", true, true, 2), OracleMetadata.ReadColumnHit);

                Assert.Equal(new[] { "EMPLOYEES", "EMP", "EMP_V" }, hits.Select(h => h.ObjectName));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Simulated_SearchSchemas_FlagOrKnownList(bool hasOracleMaintained)
        {
            using (var db = OpenDictionary(hasOracleMaintained))
            {
                var all = Run(db, OracleMetadata.SearchSchemas("%SYS%", true, hasOracleMaintained, 500), r => OracleMetadata.ReadSchema(r, hasOracleMaintained));
                var user = Run(db, OracleMetadata.SearchSchemas("%SYS%", false, hasOracleMaintained, 500), r => OracleMetadata.ReadSchema(r, hasOracleMaintained));

                Assert.Equal(new[] { "MDSYS", "SYS", "SYSADM_APP", "SYSTEM" }, all.Select(s => s.Name));
                Assert.Equal(new[] { true, true, false, true }, all.Select(s => s.OracleMaintained));
                Assert.Equal(new[] { "SYSADM_APP" }, user.Select(s => s.Name));
            }
        }

        [Fact]
        public void Simulated_11gDictionary_OracleMaintainedQueryFails_FallbackQueryWorks()
        {
            using (var db = OpenDictionary(false))
            {
                Assert.ThrowsAny<SqliteException>(() => Run(db, OracleMetadata.Schemas(true), r => OracleMetadata.ReadSchema(r, true)));

                var schemas = Run(db, OracleMetadata.Schemas(false), r => OracleMetadata.ReadSchema(r, false));

                Assert.Equal(new[] { "HR", "MDSYS", "SCOTT", "SYS", "SYSADM_APP", "SYSTEM" }, schemas.Select(s => s.Name));
                Assert.Equal(new[] { false, true, false, true, false, true }, schemas.Select(s => s.OracleMaintained));
            }
        }
    }
}
