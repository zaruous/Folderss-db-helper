using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>
    /// SQLite 공급자: 실제 임시 파일 DB에 공급자를 명시해 연 세션으로 접속·트랜잭션·데이터 사전·테이블 정보(F4)·정의서를 확인한다.
    /// (DbSessionTests의 SQLite 세션은 공급자 없이 열어 Generic 규칙을 쓴다 — 그 시험의 뜻은 바뀌지 않는다.)
    /// </summary>
    public class SqliteProviderTests : IDisposable
    {
        private static readonly IDbProvider Lite = DbProviders.Sqlite;
        private readonly string _dir;

        public SqliteProviderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dbh-sqlite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private OracleConnectionProfile Profile(string file = "app.db", bool readOnly = false)
        {
            return new OracleConnectionProfile { Id = "s1", Name = "로컬", Provider = "sqlite", ServiceName = Path.Combine(_dir, file), ReadOnly = readOnly, Port = 0 };
        }

        private static async Task<DbSession> OpenAsync(OracleConnectionProfile profile)
        {
            var session = DbSession.Create(Lite, Lite.BuildConnectionString(profile, null, 5));
            await session.OpenAsync();
            return session;
        }

        private static SqlStatement One(string sql)
        {
            return SqlScript.Parse(sql, Lite.Lex);
        }

        private static async Task RunAsync(DbSession session, string script)
        {
            foreach (var statement in SqlScript.SplitForRun(script, Lite.Lex))
                await session.ExecuteAsync(statement, 100);
        }

        private const string Schema =
            "CREATE TABLE dept (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);\n"
            + "CREATE TABLE emp (\n  id INTEGER NOT NULL,\n  code TEXT NOT NULL,\n  dept_id INTEGER REFERENCES dept(id) ON DELETE CASCADE,\n"
            + "  salary NUMERIC DEFAULT 0,\n  email TEXT,\n  PRIMARY KEY (id, code)\n);\n"
            + "CREATE UNIQUE INDEX emp_email ON emp(email);\n"
            + "CREATE UNIQUE INDEX emp_lower ON emp(lower(email));\n"
            + "CREATE INDEX emp_salary ON emp(salary DESC) WHERE salary > 0;\n"
            + "CREATE VIEW v_emp AS SELECT id, code FROM emp;\n"
            + "CREATE TRIGGER emp_ins AFTER INSERT ON emp\nBEGIN\n  UPDATE emp SET salary = CASE WHEN new.salary IS NULL THEN 0 ELSE new.salary END WHERE id = new.id;\n\n  SELECT 1;\nEND;\n"
            + "INSERT INTO dept VALUES (1, 'dev');\n"
            + "INSERT INTO emp VALUES (1, 'a', 1, 100, 'a@x');\n"
            + "COMMIT;";

        // ---------- 등록·접속 ----------

        [Fact]
        public void Registered_WithFacts()
        {
            Assert.Same(Lite, DbProviders.For("sqlite"));
            Assert.Contains(Lite, DbProviders.All);
            Assert.Equal("SQLite", Lite.DisplayName);
            Assert.Equal("파일", Lite.ServiceLabel);
            Assert.False(Lite.UsesPassword);
            Assert.False(Lite.DdlAutoCommits);
            Assert.Same(SqlLexRules.Sqlite, Lite.Lex);
            Assert.Same(NoMonitor.Instance, Lite.Monitor);
            Assert.Null(Lite.TableSchema.Ddl);
            Assert.True(Lite.Metadata.SupportsTableInfo);
            // 연결 형식으로 고르는 경로는 그대로 Generic(시험 세션 호환)
            Assert.Same(DbProviders.Generic, DbProviders.For(new SqliteConnection("Data Source=:memory:")));
        }

        [Fact]
        public void Validate_OnlyFileRequired()
        {
            var empty = Profile();
            empty.ServiceName = " ";
            Assert.Equal(new[] { "'로컬': 파일을 입력하세요." }, Lite.Validate(empty, "'로컬'"));
            // 호스트·포트·사용자는 없어도 된다, 파일이 없어도 검증 오류는 아니다(연결할 때 묻는다)
            Assert.Empty(Lite.Validate(Profile("none.db"), "'로컬'"));
            Assert.Equal(Path.Combine(_dir, "app.db"), Lite.Address(Profile()));
        }

        [Fact]
        public async Task MissingFile_IsNotCreatedSilently_OnlyByCreateDatabaseFile()
        {
            var profile = Profile("new.db");
            Assert.True(SqliteProvider.NeedsCreate(profile));
            var session = DbSession.Create(Lite, Lite.BuildConnectionString(profile, null, 5));
            await Assert.ThrowsAnyAsync<Exception>(() => session.OpenAsync());
            session.Dispose();
            Assert.False(File.Exists(profile.ServiceName));

            SqliteProvider.CreateDatabaseFile(profile.ServiceName);
            Assert.True(File.Exists(profile.ServiceName));
            Assert.False(SqliteProvider.NeedsCreate(profile));
            using (var opened = await OpenAsync(profile))
                Assert.True(opened.IsOpen);
        }

        [Fact]
        public void NeedsCreate_SkipsMemoryAndUri()
        {
            var p = Profile();
            p.ServiceName = ":memory:";
            Assert.False(SqliteProvider.NeedsCreate(p));
            p.ServiceName = "file:x.db?mode=memory";
            Assert.False(SqliteProvider.NeedsCreate(p));
        }

        [Fact]
        public async Task ReadOnlyProfile_DatabaseRejectsWrites()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            using (var rw = await OpenAsync(Profile()))
                await RunAsync(rw, "CREATE TABLE t (a INTEGER);\nCOMMIT;");
            using (var ro = await OpenAsync(Profile(readOnly: true)))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => ro.ExecuteAsync(One("INSERT INTO t VALUES (1)"), 10));
                Assert.True(Lite.IsPermissionDenied(Unwrap(ex)));
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SqliteException)
                    return e;
            }
            return ex;
        }

        // ---------- 트랜잭션 ----------

        [Fact]
        public async Task Ddl_IsTransactional_RollbackRemovesTable()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            using (var session = await OpenAsync(Profile()))
            {
                var result = await session.ExecuteAsync(One("CREATE TABLE gone (a INTEGER)"), 10);
                Assert.True(session.HasPendingChanges);
                Assert.False(result.TransactionEnded);
                await session.RollbackAsync();
                var names = await session.QueryAsync(new SqlQuery { Sql = "SELECT name FROM sqlite_master WHERE name = 'gone'" }, r => r.GetString(0));
                Assert.Empty(names);
            }
        }

        [Fact]
        public async Task Vacuum_RefusedWhilePending_RunsOutsideTransactionOtherwise()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            using (var session = await OpenAsync(Profile()))
            {
                await RunAsync(session, "CREATE TABLE t (a INTEGER);\nCOMMIT;");
                await session.ExecuteAsync(One("INSERT INTO t VALUES (1)"), 10);
                Assert.True(session.HasPendingChanges);
                await Assert.ThrowsAnyAsync<Exception>(() => session.ExecuteAsync(One("VACUUM"), 10));
                Assert.True(session.HasPendingChanges);
                await session.CommitAsync();
                await session.ExecuteAsync(One("VACUUM"), 10);
                Assert.False(session.HasPendingChanges);
            }
        }

        [Theory]
        [InlineData("VACUUM", true)]
        [InlineData("ATTACH DATABASE 'x.db' AS aux", true)]
        [InlineData("DETACH aux", true)]
        [InlineData("PRAGMA journal_mode = WAL", true)]
        [InlineData("PRAGMA main.foreign_keys=ON", true)]
        [InlineData("PRAGMA table_info(t)", false)]
        [InlineData("CREATE TABLE t (a)", false)]
        public void NoTransactionRules(string sql, bool expected)
        {
            Assert.Equal(expected, Lite.RequiresNoTransaction(One(sql)));
        }

        [Fact]
        public async Task Attach_RunsAndAppearsAsSchema()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            SqliteProvider.CreateDatabaseFile(Profile("aux.db").ServiceName);
            using (var session = await OpenAsync(Profile()))
            {
                await session.ExecuteAsync(One("ATTACH DATABASE '" + Profile("aux.db").ServiceName.Replace("'", "''") + "' AS aux"), 10);
                await RunAsync(session, "CREATE TABLE aux.side (k TEXT PRIMARY KEY);\nCOMMIT;");
                var schemas = await session.QueryAsync(Lite.Metadata.Schemas(false), r => Lite.Metadata.ReadSchema(r, false).Name);
                // temp는 임시 객체를 만들기 전에는 pragma_database_list에 없다
                Assert.Equal(new[] { "main", "aux" }, schemas);
                var tables = await session.QueryAsync(Lite.Metadata.Objects("aux", TreeGroups.Table, 0), Lite.Metadata.ReadObject);
                Assert.Equal(new[] { "side" }, tables.Select(t => t.Name));
                Assert.All(tables, t => Assert.Equal("aux", t.Owner));
            }
        }

        [Fact]
        public async Task Trigger_Script_RunsAsOneStatement()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            using (var session = await OpenAsync(Profile()))
            {
                await RunAsync(session, Schema);
                var triggers = await session.QueryAsync(new SqlQuery { Sql = "SELECT name FROM sqlite_master WHERE type = 'trigger'" }, r => r.GetString(0));
                Assert.Equal(new[] { "emp_ins" }, triggers);
                // 트리거가 실제로 돈다
                await session.ExecuteAsync(One("INSERT INTO emp (id, code, dept_id, salary) VALUES (2, 'b', 1, NULL)"), 10);
                var salary = await session.QueryAsync(new SqlQuery { Sql = "SELECT salary FROM emp WHERE id = 2" }, r => Convert.ToInt64(r.GetValue(0)));
                Assert.Equal(new[] { 0L }, salary);
                await session.RollbackAsync();
            }
        }

        // ---------- 데이터 사전 ----------

        private async Task<DbSession> SeededAsync()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            var session = await OpenAsync(Profile());
            await RunAsync(session, Schema);
            return session;
        }

        [Fact]
        public async Task Metadata_TreeAndColumnsAndKeys()
        {
            using (var session = await SeededAsync())
            {
                var md = Lite.Metadata;
                Assert.Equal(new[] { "main" }, await session.QueryAsync(md.CurrentSchema(), r => r.GetString(0)));
                var counts = TreeLoaderLogic.GroupCounts(await session.QueryAsync(md.GroupCounts("main"), TreeLoaderLogic.ReadGroupCount));
                Assert.Equal(2, counts[TreeGroups.Table]);
                Assert.Equal(1, counts[TreeGroups.View]);
                Assert.Equal(0, counts[TreeGroups.Sequence]);
                Assert.Equal(0, counts[TreeGroups.Code]);

                var tables = await session.QueryAsync(md.Objects("main", TreeGroups.Table, 1), md.ReadObject);
                Assert.Equal(new[] { "dept", "emp" }, tables.Select(t => t.Name));   // limit+1행
                Assert.All(tables, t => Assert.Equal("TABLE", t.Type));
                Assert.Empty(await session.QueryAsync(md.Objects("main", TreeGroups.Code, 0), md.ReadObject));

                var columns = await session.QueryAsync(md.Columns("main", "emp"), md.ReadColumn);
                Assert.Equal(new[] { "id", "code", "dept_id", "salary", "email" }, columns.Select(c => c.Name));
                Assert.Equal(new[] { true, true, false, false, false }, columns.Select(c => c.PrimaryKey));
                Assert.False(columns[1].Nullable);
                Assert.True(columns[2].Nullable);
                Assert.Equal("NUMERIC", columns[3].TypeLabel);
                Assert.Equal(new[] { 1, 2, 3, 4, 5 }, columns.Select(c => c.Position));

                // 고유 키: emp_email만(식 인덱스 emp_lower, 부분 인덱스, 기본 키 제외). dept는 UNIQUE 제약(autoindex)
                var keys = await session.QueryAsync(md.UniqueKeys("main", "emp"), md.ReadUniqueKey);
                Assert.Equal(new[] { ("emp_email", "email", 1) }, keys.Select(k => (k.KeyName, k.ColumnName, k.Position)));
                var deptKeys = await session.QueryAsync(md.UniqueKeys("main", "dept"), md.ReadUniqueKey);
                Assert.Equal(new[] { "name" }, deptKeys.Select(k => k.ColumnName));
                Assert.Null(md.RowIdTable("main", "emp"));
            }
        }

        [Fact]
        public async Task Metadata_SearchAndVersionAndSource()
        {
            using (var session = await SeededAsync())
            {
                var md = Lite.Metadata;
                var hits = await session.QueryAsync(md.SearchObjects(SqlScript.ContainsPattern("emp"), false, false, 50), md.ReadObjectHit);
                Assert.Equal(new[] { ("main", "TABLE", "emp"), ("main", "VIEW", "v_emp") }, hits.Select(h => (h.Owner, h.ObjectType, h.ObjectName)));
                var tables = await session.QueryAsync(md.SearchTables(SqlScript.ContainsPattern("E"), 50), md.ReadObjectHit);
                Assert.Equal(new[] { "dept", "emp" }, tables.Select(h => h.ObjectName));
                var cols = await session.QueryAsync(md.SearchColumns(SqlScript.ContainsPattern("mail"), false, false, 50), md.ReadColumnHit);
                Assert.Equal(new[] { ("emp", "email", "TEXT") }, cols.Select(h => (h.ObjectName, h.ColumnName, h.ColumnType)));
                var schemas = await session.QueryAsync(md.SearchSchemas(SqlScript.ContainsPattern("mai"), false, false, 50), r => md.ReadSchema(r, false).Name);
                Assert.Equal(new[] { "main" }, schemas);
                var version = (await session.QueryAsync(md.ServerVersion(), md.ReadServerVersion)).Single();
                Assert.StartsWith("SQLite 3.", version);
                var parts = await session.QueryAsync(md.Source("main", "VIEW", "v_emp"), ObjectSource.Read);
                Assert.Equal("CREATE VIEW v_emp AS SELECT id, code FROM emp;", md.BuildSource("main", "VIEW", "v_emp", parts).TrimEnd());
            }
        }

        [Fact]
        public void Metadata_SchemaNameIsQuoted()
        {
            Assert.Equal("\"a\"\"b\".sqlite_master", SqliteMetadata.Schema("a\"b"));
            Assert.Contains("FROM \"x\"\"; DROP\".sqlite_master", Lite.Metadata.GroupCounts("x\"; DROP").Sql);
        }

        // ---------- 테이블 정보(F4)·정의서 ----------

        [Fact]
        public async Task TableInfo_Describe_ColumnsIndexesConstraints()
        {
            using (var session = await SeededAsync())
            {
                string error = null;
                var info = await Lite.TableSchema.DescribeAsync(session, null, "EMP", CancellationToken.None, e => error = e);
                Assert.Null(error);
                Assert.Equal(("main", "emp", "TABLE"), (info.Owner, info.Name, info.Type));
                Assert.Null(info.Comment);
                Assert.Equal("0", info.Columns.Single(c => c.Column.Name == "salary").Default);
                Assert.All(info.Columns, c => Assert.Null(c.Comment));

                var indexes = info.Indexes.ToDictionary(i => i.Name);
                Assert.Equal("\"email\"", indexes["emp_email"].Columns);
                Assert.True(indexes["emp_email"].Unique);
                Assert.Equal("(식)", indexes["emp_lower"].Columns);
                Assert.Equal("BTREE (부분)", indexes["emp_salary"].Type);
                Assert.Equal("\"salary\" DESC", indexes["emp_salary"].Columns);
                Assert.Equal("PRIMARY KEY", indexes.Values.Single(i => i.Name.StartsWith("sqlite_autoindex_emp", StringComparison.Ordinal)).OwnerConstraint);

                var pk = info.Constraints.Single(c => c.Type == "P");
                Assert.Equal(new[] { "id", "code" }, pk.ColumnNames);
                var fk = info.Constraints.Single(c => c.Type == "R");
                Assert.Equal("FK1", fk.Name);
                Assert.Equal(new[] { "dept_id" }, fk.ColumnNames);
                Assert.Equal("\"main\".\"dept\"", fk.Reference);
                Assert.Equal(new[] { "\"id\"" }, fk.ReferenceColumns);
                Assert.Equal("CASCADE", fk.DeleteRule);

                var dept = await Lite.TableSchema.DescribeAsync(session, "main", "dept", CancellationToken.None, e => error = e);
                Assert.Equal(new[] { "name" }, dept.Constraints.Single(c => c.Type == "U").ColumnNames);

                var view = await Lite.TableSchema.DescribeAsync(session, null, "v_emp", CancellationToken.None, e => error = e);
                Assert.Equal("VIEW", view.Type);
                Assert.Equal(new[] { "id", "code" }, view.Columns.Select(c => c.Column.Name));

                var missing = await Lite.TableSchema.DescribeAsync(session, null, "nope", CancellationToken.None, e => error = e);
                Assert.Null(missing);
                Assert.NotNull(error);
            }
        }

        [Fact]
        public async Task TableSpec_CandidatesAndDescribeMany_EmptyComments()
        {
            using (var session = await SeededAsync())
            {
                var candidates = await session.QueryAsync(Lite.Metadata.TableSpecCandidates("main"), TableSpecQueries.ReadCandidate);
                Assert.Equal(new[] { ("dept", "TABLE"), ("emp", "TABLE"), ("v_emp", "VIEW") }, candidates.Select(c => (c.Name, c.Type)));
                Assert.All(candidates, c => Assert.Null(c.Comment));

                var many = await Lite.TableSchema.DescribeManyAsync(session, "main", new[] { "v_emp", "emp", "dept", "nope" }, CancellationToken.None);
                Assert.Equal(new[] { "v_emp", "emp", "dept" }, many.Select(t => t.Name));
                Assert.Equal(5, many[1].Columns.Count);
                Assert.Equal(2, many[1].Constraints.Count);
                Assert.Empty(many[0].Indexes);
            }
        }

        // ---------- 값 ----------

        [Fact]
        public async Task MixedTypeColumn_ReadsEveryRow()
        {
            SqliteProvider.CreateDatabaseFile(Profile().ServiceName);
            using (var session = await OpenAsync(Profile()))
            {
                await RunAsync(session, "CREATE TABLE m (v);\nINSERT INTO m VALUES (1), ('text'), (2.5), (NULL), (x'0102');\nCOMMIT;");
                var result = await session.ExecuteAsync(One("SELECT v FROM m ORDER BY rowid"), 10);
                Assert.Equal(5, result.Rows.Count);
                Assert.Equal("1", result.Rows[0][0]);
                Assert.Equal("text", result.Rows[1][0]);
                Assert.Equal("2.5", result.Rows[2][0]);
                Assert.Null(result.Rows[3][0]);
            }
        }
    }
}
