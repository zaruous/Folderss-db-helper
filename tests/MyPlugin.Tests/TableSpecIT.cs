using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using MyPlugin;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 테이블 정의서를 PostgreSQL·SQL Server에서 한 바퀴 돈다(없으면 건너뜀): 시험 스키마에 테이블·뷰를 만들고 → 대상 후보 → 정의서 데이터 → 기본 양식 엑셀 → 지운다.
    /// 환경 변수는 <see cref="TableSchemaIT"/>와 같다(SCHEMA_IT_PG·SCHEMA_IT_MSSQL 와 _USER·_PW).
    /// </summary>
    public class TableSpecIT
    {
        private const string Schema = "dbh_spec_it";
        private readonly ITestOutputHelper _output;
        public TableSpecIT(ITestOutputHelper output) { _output = output; }

        [TableSchemaIT.DbFact("SCHEMA_IT_PG")]
        public async Task Postgres_CandidatesLoadRender()
        {
            using (var session = await TableSchemaIT.OpenAsync("SCHEMA_IT_PG", "postgres", "postgres"))
            {
                await Exec(session, "DROP SCHEMA IF EXISTS " + Schema + " CASCADE");
                try
                {
                    await Exec(session, "CREATE SCHEMA " + Schema);
                    await Exec(session, "CREATE TABLE " + Schema + ".parent (id integer NOT NULL PRIMARY KEY)");
                    await Exec(session, "CREATE TABLE " + Schema + ".child (id integer NOT NULL, code varchar(20) NOT NULL, amt numeric(10,2) DEFAULT 0, parent_id integer,"
                        + " CONSTRAINT pk_child PRIMARY KEY (id), CONSTRAINT uk_child_code UNIQUE (code), CONSTRAINT ck_child_amt CHECK (amt >= 0),"
                        + " CONSTRAINT fk_child_parent FOREIGN KEY (parent_id) REFERENCES " + Schema + ".parent (id) ON DELETE CASCADE ON UPDATE CASCADE)");
                    await Exec(session, "CREATE INDEX ix_child_parent ON " + Schema + ".child (parent_id)");
                    await Exec(session, "COMMENT ON TABLE " + Schema + ".child IS '자식 테이블'");
                    await Exec(session, "COMMENT ON COLUMN " + Schema + ".child.code IS '코드'");
                    await Exec(session, "CREATE VIEW " + Schema + ".v_child AS SELECT id, code FROM " + Schema + ".child");
                    await RunAsync(session, "child", "parent", "v_child", "code", "fk_child_parent", "ix_child_parent", "PostgreSQL");
                }
                finally
                {
                    await Exec(session, "DROP SCHEMA IF EXISTS " + Schema + " CASCADE", true);
                }
            }
        }

        [TableSchemaIT.DbFact("SCHEMA_IT_MSSQL")]
        public async Task SqlServer_CandidatesLoadRender()
        {
            using (var session = await TableSchemaIT.OpenAsync("SCHEMA_IT_MSSQL", "mssql", "sa"))
            {
                await Cleanup(session);
                try
                {
                    await Exec(session, "CREATE SCHEMA " + Schema);
                    await Exec(session, "CREATE TABLE " + Schema + ".PARENT (ID int NOT NULL PRIMARY KEY)");
                    await Exec(session, "CREATE TABLE " + Schema + ".CHILD (ID int NOT NULL, CODE nvarchar(20) NOT NULL, AMT decimal(10,2) DEFAULT 0, PARENT_ID int,"
                        + " CONSTRAINT PK_CHILD PRIMARY KEY (ID), CONSTRAINT UK_CHILD_CODE UNIQUE (CODE), CONSTRAINT CK_CHILD_AMT CHECK (AMT >= 0),"
                        + " CONSTRAINT FK_CHILD_PARENT FOREIGN KEY (PARENT_ID) REFERENCES " + Schema + ".PARENT (ID) ON DELETE CASCADE ON UPDATE CASCADE)");
                    await Exec(session, "CREATE INDEX IX_CHILD_PARENT ON " + Schema + ".CHILD (PARENT_ID)");
                    await Exec(session, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'자식 테이블', @level0type = N'SCHEMA', @level0name = N'" + Schema + "', @level1type = N'TABLE', @level1name = N'CHILD'");
                    await Exec(session, "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'코드', @level0type = N'SCHEMA', @level0name = N'" + Schema + "', @level1type = N'TABLE', @level1name = N'CHILD', @level2type = N'COLUMN', @level2name = N'CODE'");
                    await Exec(session, "CREATE VIEW " + Schema + ".V_CHILD AS SELECT ID, CODE FROM " + Schema + ".CHILD");
                    await RunAsync(session, "CHILD", "PARENT", "V_CHILD", "CODE", "FK_CHILD_PARENT", "IX_CHILD_PARENT", "Microsoft SQL Server");
                }
                finally
                {
                    await Cleanup(session, true);
                }
            }
        }

        async Task Cleanup(DbSession session, bool ignoreError = false)
        {
            foreach (var sql in new[] { "DROP VIEW IF EXISTS " + Schema + ".V_CHILD", "DROP TABLE IF EXISTS " + Schema + ".CHILD", "DROP TABLE IF EXISTS " + Schema + ".PARENT",
                "IF SCHEMA_ID('" + Schema + "') IS NOT NULL EXEC('DROP SCHEMA " + Schema + "')" })
                await Exec(session, sql, ignoreError);
        }

        async Task RunAsync(DbSession session, string child, string parent, string view, string codeColumn, string fk, string index, string versionPrefix)
        {
            var meta = session.Provider.Metadata;
            Assert.True(meta.SupportsTableInfo);

            // 대상 선택 창의 후보 목록
            var candidates = await session.QueryAsync(meta.TableSpecCandidates(Schema), TableSpecQueries.ReadCandidate);
            _output.WriteLine("candidates: " + string.Join(", ", candidates.Select(c => c.Name + "/" + c.Type + "/" + c.NumRows + "/" + c.Comment)));
            Assert.Equal(new[] { child, parent, view }.OrderBy(n => n, StringComparer.Ordinal), candidates.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("TABLE", candidates.Single(c => c.Name == child).Type);
            Assert.Equal("VIEW", candidates.Single(c => c.Name == view).Type);
            Assert.Equal("자식 테이블", candidates.Single(c => c.Name == child).Comment);
            Assert.All(candidates, c => Assert.Equal(Schema, c.Owner));

            // 정의서 데이터(없는 대상 하나는 빠진다)
            var targets = TableSpecLogic.Order(candidates.Select(c => new TableKey(c.Owner, c.Name)).Concat(new[] { new TableKey(Schema, "no_such_table") }));
            var progress = new System.Collections.Generic.List<string>();
            var tables = await TableSpecQueries.LoadAsync(session, targets, new SyncProgress(progress.Add), CancellationToken.None);
            _output.WriteLine(string.Join("\n", progress));
            Assert.Equal(3, tables.Count);
            Assert.Equal(targets.Where(t => t.Name != "no_such_table").Select(t => t.Name), tables.Select(t => t.Name));

            var c1 = tables.Single(t => t.Name == child);
            Assert.Equal("TABLE", c1.Type);
            Assert.Equal("자식 테이블", c1.Comment);
            Assert.Equal(4, c1.Columns.Count);
            Assert.True(c1.Columns[0].Column.PrimaryKey);
            Assert.Equal("코드", c1.Columns.Single(c => c.Column.Name == codeColumn).Comment);
            Assert.Contains(c1.Constraints, k => k.Type == "P");
            Assert.Contains(c1.Constraints, k => k.Type == "U");
            Assert.Contains(c1.Constraints, k => k.Type == "C");
            var fkInfo = c1.Constraints.Single(k => k.Type == "R");
            _output.WriteLine("fk: " + fkInfo.Reference + " (" + string.Join(", ", fkInfo.ReferenceColumns) + ")");
            Assert.Equal(fk, fkInfo.Name);
            Assert.Equal("CASCADE", fkInfo.DeleteRule);
            Assert.Equal("CASCADE", fkInfo.UpdateRule);
            Assert.Null(c1.Constraints.Single(k => k.Type == "P").DeleteRule);
            Assert.Contains(c1.Indexes, i => i.Name == index);
            var v = tables.Single(t => t.Name == view);
            Assert.Equal("VIEW", v.Type);
            Assert.Equal(2, v.Columns.Count);

            var version = await TableSpecQueries.LoadServerVersionAsync(session, CancellationToken.None);
            _output.WriteLine("version: " + version);
            Assert.StartsWith(versionPrefix, version);

            // 기본 양식으로 엑셀
            var context = TableSpecModel.Context(new TableSpecDocument { Title = "IT" }, new OracleConnectionProfile { Name = "it", Provider = session.Provider.Kind }, tables, version);
            using (var output = new MemoryStream())
            {
                ExcelTemplate.Render(TableSpecTemplate.Create(), context, output);
                output.Position = 0;
                using (var book = new XLWorkbook(output))
                {
                    var text = string.Join("\n", book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
                    Assert.Contains(child, text);
                    Assert.Contains(view, text);
                    Assert.Contains(fk, text);
                    Assert.Contains("코드", text);
                }
            }
        }

        async Task Exec(DbSession session, string sql, bool ignoreError = false)
        {
            try { await session.ExecuteDdlCommittedAsync(SchemaRead.DdlStatement(sql)); }
            catch (Exception ex) when (ignoreError) { _output.WriteLine("(무시) " + ex.Message); }
        }

        private sealed class SyncProgress : IProgress<string>
        {
            private readonly Action<string> _report;
            public SyncProgress(Action<string> report) { _report = report; }
            public void Report(string value) { _report(value); }
        }
    }
}
