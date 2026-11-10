using System;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>구조 편집 DDL 방언(ISchemaDdl) — Oracle·PostgreSQL·SQL Server.</summary>
    public class TableSchemaTests
    {
        static readonly SchemaTarget T = new SchemaTarget("O", "T");
        static readonly OracleSchemaDdl Oracle = new OracleSchemaDdl();
        static readonly PostgresSchemaDdl Postgres = new PostgresSchemaDdl();
        static readonly SqlServerSchemaDdl SqlServer = new SqlServerSchemaDdl();
        static ColumnSpec Column(string name = "A", string type = "NUMBER", bool notNull = false, string @default = "") => new ColumnSpec { Name = name, DataType = type, NotNull = notNull, Default = @default };
        static IndexColumnChoice[] Keys(params string[] names) => names.Select(n => new IndexColumnChoice { Name = n.Replace(" DESC", ""), Descending = n.EndsWith(" DESC") }).ToArray();

        [Fact]
        public void Oracle_Statements()
        {
            Assert.Equal("ALTER TABLE \"O\".\"T\" ADD (\"A\" VARCHAR2(10) DEFAULT 'X' NOT NULL)", Oracle.AddColumn(T, Column(type: "VARCHAR2(10)", notNull: true, @default: "'X'")));
            Assert.Equal("ALTER TABLE \"O\".\"T\" MODIFY (\"A\" VARCHAR2(100 CHAR))", Oracle.ChangeType(T, Column(type: "VARCHAR2(100 CHAR)")));
            Assert.Equal("ALTER TABLE \"O\".\"T\" MODIFY (\"A\" NOT NULL)", Oracle.ChangeNullability(T, Column(notNull: true)));
            Assert.Equal("ALTER TABLE \"O\".\"T\" MODIFY (\"A\" NULL)", Oracle.ChangeNullability(T, Column()));
            Assert.Equal("ALTER TABLE \"O\".\"T\" MODIFY (\"A\" DEFAULT NULL)", Oracle.ChangeDefault(T, Column()));
            Assert.Equal("COMMENT ON COLUMN \"O\".\"T\".\"A\" IS 'Bob''s'", Oracle.CommentColumn(T, "A", "Bob's"));
            Assert.Equal("ALTER TABLE \"O\".\"T\" RENAME COLUMN \"A\" TO \"B\"", Oracle.RenameColumn(T, "A", "B"));
            Assert.Equal("ALTER TABLE \"O\".\"T\" DROP COLUMN \"ID\"", Oracle.DropColumn(T, "ID"));
            Assert.Equal("CREATE UNIQUE INDEX \"O\".\"I\" ON \"O\".\"T\" (\"ID\", \"CREATED\" DESC)", Oracle.CreateIndex(T, "I", true, Keys("ID", "CREATED DESC")));
            Assert.Equal("DROP INDEX \"O\".\"I\"", Oracle.DropIndex(T, "I"));
            Assert.Equal("ALTER INDEX \"O\".\"I\" RENAME TO \"J\"", Oracle.RenameIndex(T, "I", "J"));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ADD CONSTRAINT \"PK\" PRIMARY KEY (\"ID\", \"LINE_NO\")", Oracle.AddConstraint(T, "PK", new ConstraintSpec { Type = "P", Columns = Keys("ID", "LINE_NO") }));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ENABLE VALIDATE CONSTRAINT \"C\"", Oracle.SetConstraintEnabled(T, "C", "P", true));
            Assert.Equal("ALTER TABLE \"O\".\"T\" DISABLE CONSTRAINT \"C\"", Oracle.SetConstraintEnabled(T, "C", "P", false));
            Assert.Contains("\"A\"\"B\"", Oracle.ChangeNullability(T, Column(name: "A\"B")));
            Assert.True(Oracle.CanDisable("P"));
        }

        [Fact]
        public void Postgres_Statements()
        {
            Assert.Equal("ALTER TABLE \"O\".\"T\" ADD COLUMN \"A\" integer DEFAULT 0 NOT NULL", Postgres.AddColumn(T, Column(type: "integer", notNull: true, @default: "0")));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ALTER COLUMN \"A\" TYPE bigint USING \"A\"::bigint", Postgres.ChangeType(T, Column(type: "bigint")));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ALTER COLUMN \"A\" SET NOT NULL", Postgres.ChangeNullability(T, Column(notNull: true)));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ALTER COLUMN \"A\" DROP NOT NULL", Postgres.ChangeNullability(T, Column()));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ALTER COLUMN \"A\" SET DEFAULT now()", Postgres.ChangeDefault(T, Column(@default: "now()")));
            Assert.Equal("ALTER TABLE \"O\".\"T\" ALTER COLUMN \"A\" DROP DEFAULT", Postgres.ChangeDefault(T, Column()));
            Assert.Equal("COMMENT ON COLUMN \"O\".\"T\".\"A\" IS NULL", Postgres.CommentColumn(T, "A", ""));
            Assert.Equal("CREATE INDEX \"I\" ON \"O\".\"T\" (\"A\" DESC)", Postgres.CreateIndex(T, "I", false, Keys("A DESC")));
            Assert.Equal("DROP INDEX \"O\".\"I\"", Postgres.DropIndex(T, "I"));
            Assert.Equal("ALTER TABLE \"O\".\"T\" RENAME CONSTRAINT \"A\" TO \"B\"", Postgres.RenameConstraint(T, "A", "B"));
            Assert.False(Postgres.CanDisable("R"));
            Assert.Throws<NotSupportedException>(() => Postgres.SetConstraintEnabled(T, "C", "C", false));
            Assert.Equal(63, Postgres.MaxNameLength);
        }

        [Fact]
        public void SqlServer_Statements()
        {
            var t = new SchemaTarget("dbo", "T");
            Assert.Equal("ALTER TABLE [dbo].[T] ADD [A] int NOT NULL DEFAULT (0)", SqlServer.AddColumn(t, Column(type: "int", notNull: true, @default: "0")));
            // 형식·NULL 변경은 ALTER COLUMN에 둘 다 적는다
            Assert.Equal("ALTER TABLE [dbo].[T] ALTER COLUMN [A] bigint NOT NULL", SqlServer.ChangeType(t, Column(type: "bigint", notNull: true)));
            Assert.Equal("ALTER TABLE [dbo].[T] ALTER COLUMN [A] int NULL", SqlServer.ChangeNullability(t, Column(type: "int")));
            Assert.Equal("EXEC sp_rename N'[dbo].[T].[A]', N'B', N'COLUMN'", SqlServer.RenameColumn(t, "A", "B"));
            Assert.Equal("EXEC sp_rename N'[dbo].[T].[I]', N'J', N'INDEX'", SqlServer.RenameIndex(t, "I", "J"));
            Assert.Equal("EXEC sp_rename N'[dbo].[C]', N'D', N'OBJECT'", SqlServer.RenameConstraint(t, "C", "D"));
            Assert.Equal("DROP INDEX [I] ON [dbo].[T]", SqlServer.DropIndex(t, "I"));
            Assert.Equal("CREATE UNIQUE INDEX [I] ON [dbo].[T] ([A], [B] DESC)", SqlServer.CreateIndex(t, "I", true, Keys("A", "B DESC")));
            Assert.Equal("ALTER TABLE [dbo].[T] NOCHECK CONSTRAINT [FK]", SqlServer.SetConstraintEnabled(t, "FK", "R", false));
            Assert.Equal("ALTER TABLE [dbo].[T] WITH CHECK CHECK CONSTRAINT [FK]", SqlServer.SetConstraintEnabled(t, "FK", "R", true));
            Assert.False(SqlServer.CanDisable("P"));
            Assert.Equal("[a]]b]", SqlServer.Quote("a]b"));

            // 기본값·컬럼 삭제는 기존 기본값 제약을 찾아 지우는 배치(다시 실행해도 안전)
            var change = SqlServer.ChangeDefault(t, Column(@default: "getdate()"));
            Assert.Contains("FROM sys.default_constraints", change);
            Assert.Contains("IF @df IS NOT NULL BEGIN DECLARE @drop nvarchar(max) = N'ALTER TABLE [dbo].[T] DROP CONSTRAINT ' + QUOTENAME(@df); EXEC(@drop); END;", change);
            Assert.EndsWith("ALTER TABLE [dbo].[T] ADD DEFAULT (getdate()) FOR [A]", change);
            Assert.DoesNotContain("ADD DEFAULT", SqlServer.ChangeDefault(t, Column()));
            Assert.EndsWith("ALTER TABLE [dbo].[T] DROP COLUMN [A]", SqlServer.DropColumn(t, "A"));

            var comment = SqlServer.CommentColumn(t, "A", "설명 'x'");
            Assert.Contains("sp_dropextendedproperty", comment);
            Assert.Contains("sp_addextendedproperty @name = N'MS_Description', @value = N'설명 ''x'''", comment);
            Assert.DoesNotContain("sp_addextendedproperty", SqlServer.CommentColumn(t, "A", ""));
        }

        [Theory]
        [InlineData("NUMBER; DROP TABLE X")]
        [InlineData("NUMBER --comment")]
        [InlineData("NUMBER /*comment*/")]
        [InlineData("")]
        public void RejectMultipleStatements(string value)
        {
            Assert.Throws<ArgumentException>(() => Oracle.ChangeType(T, Column(type: value)));
            Assert.Throws<ArgumentException>(() => Postgres.ChangeType(T, Column(type: value)));
            Assert.Throws<ArgumentException>(() => SqlServer.ChangeType(T, Column(type: value)));
        }

        [Fact]
        public void ConstraintDefinitions()
        {
            Assert.Throws<ArgumentException>(() => Oracle.AddConstraint(T, "PK", new ConstraintSpec { Type = "P" }));
            Assert.EndsWith("FOREIGN KEY ([A]) REFERENCES dbo.P (ID)", SqlServer.AddConstraint(new SchemaTarget("dbo", "T"), "FK", new ConstraintSpec { Type = "R", Columns = Keys("A"), Reference = "dbo.P (ID)" }));
            Assert.EndsWith("CHECK (a >= 0)", Postgres.AddConstraint(T, "CK", new ConstraintSpec { Type = "C", Condition = "a >= 0" }));
        }

        [Fact]
        public void MetadataHelpers()
        {
            Assert.Equal("0", SchemaRead.Unwrap("((0))"));
            Assert.Equal("getdate()", SchemaRead.Unwrap("(getdate())"));
            Assert.Equal("(a) + (b)", SchemaRead.Unwrap("(a) + (b)"));
            Assert.Equal("'(x'", SchemaRead.Unwrap("('(x')"));
            Assert.Equal("amount >= 0::numeric", PostgresTableSchema.CheckCondition("CHECK ((amount >= 0::numeric)) NOT VALID"));
            Assert.Equal("\"A\"\"B\"", TableSchemaLogic.QuoteName("A\"B"));
            var statement = SchemaRead.DdlStatement("ALTER TABLE T ADD (A NUMBER)");
            Assert.Equal(SqlKind.Ddl, statement.Kind);
            Assert.Equal("ALTER", statement.Verb);
        }

        [Fact]
        public void ProvidersExposeTableSchema()
        {
            Assert.NotNull(DbProviders.Oracle.TableSchema.Ddl);
            Assert.IsType<PostgresSchemaDdl>(DbProviders.Postgres.TableSchema.Ddl);
            Assert.IsType<SqlServerSchemaDdl>(DbProviders.SqlServer.TableSchema.Ddl);
            Assert.Null(DbProviders.Generic.TableSchema);
            Assert.Contains("current_schema()", PostgresTableSchema.Resolve(null, "emp").Sql);
            Assert.Contains("lower(n.nspname) = lower(:owner)", PostgresTableSchema.Resolve("hr", "emp").Sql);
            Assert.Contains("SCHEMA_NAME()", SqlServerTableSchema.Resolve(null, "EMP").Sql);
            Assert.Contains("sys.synonyms", SqlServerTableSchema.Resolve("dbo", "EMP").Sql);
        }

        [Fact]
        public void BatchQueries_BindNameListOnce()
        {
            // 정의서 일괄 조회: 이름 목록은 t0, t1, …로 한 번만 바인드(SQL Server 제약 조회는 같은 목록을 세 번 참조)
            var pg = PostgresTableSchema.ConstraintColumns("hr", new[] { "emp", "dept" });
            Assert.Contains("t.relname IN (:t0, :t1)", pg.Sql);
            Assert.Equal(new[] { "owner", "t0", "t1" }, pg.Parameters.Select(p => p.Key));
            var ms = SqlServerTableSchema.ConstraintColumns("dbo", new[] { "EMP", "DEPT" });
            Assert.Equal(3, ms.Sql.Split(new[] { "o.name IN (@t0, @t1)" }, StringSplitOptions.None).Length - 1);
            Assert.Equal(new[] { "owner", "t0", "t1" }, ms.Parameters.Select(p => p.Key));
            // F4(이름 하나)도 같은 SQL
            Assert.Contains("c.relname IN (:t0)", PostgresTableSchema.Summary("hr", "emp").Sql);
            Assert.Throws<ArgumentException>(() => PostgresTableSchema.Columns("hr", new string[0]));
            Assert.Throws<ArgumentException>(() => SqlServerTableSchema.Columns("dbo", Enumerable.Range(0, TableSpecQueries.ChunkSize + 1).Select(i => "T" + i).ToList()));
        }
    }
}
