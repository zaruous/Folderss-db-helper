using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MyPlugin;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 테이블 정보(F4)·구조 편집을 실제 DB에서 한 바퀴 돈다(없으면 건너뜀): 테이블을 만들고 → 조회 → 편집 모델로 바꾸고 저장(DDL 실행) → 다시 조회해 확인 → 지운다.
    /// 환경 변수(호스트:포트/서비스·DB):
    ///   SCHEMA_IT_ORACLE (예: localhost:1521/xe), SCHEMA_IT_ORACLE_USER(system), SCHEMA_IT_ORACLE_PW(oracle)
    ///   SCHEMA_IT_PG (예: localhost:5432/postgres), SCHEMA_IT_PG_USER(postgres), SCHEMA_IT_PG_PW
    ///   SCHEMA_IT_MSSQL (예: localhost:1433/tempdb), SCHEMA_IT_MSSQL_USER(sa), SCHEMA_IT_MSSQL_PW
    /// 시험용 테이블 DBH_IT_PARENT·DBH_IT_CHILD를 접속 사용자의 기본 스키마에 만들고 끝나면 지운다.
    /// </summary>
    public class TableSchemaIT
    {
        private readonly ITestOutputHelper _output;
        public TableSchemaIT(ITestOutputHelper output) { _output = output; }

        internal sealed class DbFact : FactAttribute
        {
            public DbFact(string prefix)
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prefix)))
                    Skip = prefix + "가 없어 실제 DB 시험을 건너뜁니다.";
            }
        }

        internal static async Task<DbSession> OpenAsync(string prefix, string kind, string defaultUser)
        {
            var dsn = Environment.GetEnvironmentVariable(prefix).Trim();
            var hostPort = dsn.Split('/')[0];
            var profile = new OracleConnectionProfile
            {
                Id = kind, Name = kind + " 시험", Provider = kind,
                Host = hostPort.Split(':')[0], Port = int.Parse(hostPort.Split(':')[1]), ServiceName = dsn.Split('/')[1],
                UserId = Environment.GetEnvironmentVariable(prefix + "_USER") ?? defaultUser
            };
            var provider = DbProviders.For(profile);
            var cs = OracleConnectionStore.BuildConnectionString(profile, Environment.GetEnvironmentVariable(prefix + "_PW") ?? "", ShellLogic.ConnectTimeoutSeconds);
            var session = DbSession.Create(provider, cs);
            await session.OpenAsync();
            return session;
        }

        async Task Exec(DbSession session, string sql, bool ignoreError = false)
        {
            try { await ExecCommit(session, sql); }
            catch (Exception ex) when (ignoreError) { _output.WriteLine("(무시) " + ex.Message); }
        }

        /// <summary>DDL 하나를 실행하고 커밋 대기가 남으면(트랜잭션 DDL — PostgreSQL·SQL Server) 커밋한다 — 편집 창 [저장] 경로와 같다.</summary>
        static async Task ExecCommit(DbSession session, string sql)
        {
            await session.ExecuteAsync(SchemaRead.DdlStatement(sql), 0);
            if (session.HasPendingChanges)
                await session.CommitAsync();
        }

        async Task<TableDescription> Describe(DbSession session, string name)
        {
            string problem = null;
            var info = await session.Provider.TableSchema.DescribeAsync(session, null, name, CancellationToken.None, m => problem = m);
            Assert.True(info != null, problem);
            return info;
        }

        /// <summary>검증 → DDL 실행(순서대로, 성공한 것만 반영) — 편집 창 [저장]과 같은 순서.</summary>
        async Task Save(DbSession session, SchemaContext context)
        {
            var errors = SchemaPlan.Validate(context);
            Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Row.Name + "." + e.Field + ": " + e.Message)));
            foreach (var step in SchemaPlan.Build(context, row => { }))
            {
                _output.WriteLine(step.Sql.TrimEnd().TrimEnd(';') + ";");
                await ExecCommit(session, step.Sql);
                step.Applied();
            }
            Assert.DoesNotContain(context.All, r => r.Dirty && !r.Deleted);
        }

        sealed class Dialect
        {
            public string Parent, Child, Int, Text, Num, Fk;
            /// <summary>내림차순 인덱스 컬럼 시험(Oracle은 DESC 인덱스가 함수 기반 인덱스가 되어 컬럼 이름이 SYS_NC…로 보이므로 뺀다).</summary>
            public bool Descending = true;
            public Func<string, string> Comment;
        }

        async Task RoundTrip(DbSession session, Dialect d)
        {
            await Exec(session, "DROP TABLE " + d.Child, true);
            await Exec(session, "DROP TABLE " + d.Parent, true);
            try
            {
                await Exec(session, "CREATE TABLE " + d.Parent + " (ID " + d.Int + " NOT NULL PRIMARY KEY)");
                await Exec(session, "CREATE TABLE " + d.Child + " (ID " + d.Int + " NOT NULL, CODE " + d.Text + " NOT NULL, AMT " + d.Num + " DEFAULT 0, PARENT_ID " + d.Int + ","
                    + " CONSTRAINT PK_DBH_IT_CHILD PRIMARY KEY (ID), CONSTRAINT CK_DBH_IT_AMT CHECK (AMT >= 0),"
                    + " CONSTRAINT FK_DBH_IT_PARENT FOREIGN KEY (PARENT_ID) REFERENCES " + d.Parent + " (ID))");
                await Exec(session, "CREATE INDEX IX_DBH_IT_CODE ON " + d.Child + " (CODE)");
                await Exec(session, d.Comment("코드"));

                // 1) 조회
                var info = await Describe(session, d.Fk);
                _output.WriteLine(info.Owner + "." + info.Name + " " + info.Type + " rows=" + info.NumRows);
                Assert.Equal("TABLE", info.Type);
                Assert.Equal(new[] { "ID", "CODE", "AMT", "PARENT_ID" }, info.Columns.Select(c => c.Column.Name.ToUpperInvariant()));
                Assert.True(info.Columns[0].Column.PrimaryKey);
                Assert.False(info.Columns[1].Column.Nullable);
                Assert.Equal("코드", info.Columns[1].Comment);
                Assert.Equal("0", SchemaRead.Unwrap(info.Columns[2].Default));
                var index = info.Indexes.Single(i => i.Name.ToUpperInvariant() == "IX_DBH_IT_CODE");
                Assert.True(index.Recreatable);
                Assert.Contains(info.Constraints, c => c.Type == "P");
                var fk = info.Constraints.Single(c => c.Type == "R");
                Assert.Contains("DBH_IT_PARENT", fk.Reference.ToUpperInvariant());
                var check = info.Constraints.Single(c => c.Type == "C" && c.Name.ToUpperInvariant() == "CK_DBH_IT_AMT");
                Assert.Contains("AMT", check.Condition.ToUpperInvariant());

                // 2) 편집: 컬럼 추가·이름·형식·NULL·기본값·주석, 인덱스 다시 만들기·이름, 고유 제약 추가, 검사 제약 삭제
                var context = SchemaPlan.Load(info, session.Provider.TableSchema.Ddl);
                var code = context.Columns.Single(c => c.Name.ToUpperInvariant() == "CODE");
                var amt = context.Columns.Single(c => c.Name.ToUpperInvariant() == "AMT");
                var added = new ColumnRow(true, "NOTE_TEXT", d.Text, false, "'-'", "비고");
                context.Columns.Add(added);
                amt.Name = "AMOUNT"; amt.Default = "1"; amt.NotNull = true; amt.Comment = "금액";
                code.Comment = "";
                var ix = context.Indexes.Single(i => i.Name.ToUpperInvariant() == "IX_DBH_IT_CODE");
                ix.Columns = TableSchemaLogic.QuoteName(code.Name) + ", " + TableSchemaLogic.QuoteName("AMOUNT") + (d.Descending ? " DESC" : "");
                ix.Name = "IX_DBH_IT_CODE2";
                context.Constraints.Add(new ConstraintRow(true, "UK_DBH_IT_CODE", "U", TableSchemaLogic.QuoteName(code.Name)));
                context.Constraints.Single(c => c.Name.ToUpperInvariant() == "CK_DBH_IT_AMT").Deleted = true;
                var fkRow = context.Constraints.Single(c => c.Type == "R");
                if (context.Ddl.CanDisable("R")) fkRow.Enabled = false;
                await Save(session, context);

                // 3) 다시 조회해 반영 확인
                info = await Describe(session, d.Fk);
                var columns = info.Columns.ToDictionary(c => c.Column.Name.ToUpperInvariant());
                Assert.True(columns.ContainsKey("AMOUNT") && columns.ContainsKey("NOTE_TEXT"));
                Assert.False(columns["AMOUNT"].Column.Nullable);
                Assert.Equal("1", SchemaRead.Unwrap(columns["AMOUNT"].Default));
                Assert.Equal("금액", columns["AMOUNT"].Comment);
                Assert.Equal("비고", columns["NOTE_TEXT"].Comment);
                Assert.True(string.IsNullOrEmpty(columns["CODE"].Comment));
                var rebuilt = info.Indexes.Single(i => i.Name.ToUpperInvariant() == "IX_DBH_IT_CODE2");
                Assert.Equal(2, IndexColumnsLogic.Parse(rebuilt.Columns, info.Columns.Select(c => c.Column.Name)).Count);
                Assert.Equal(d.Descending, IndexColumnsLogic.Parse(rebuilt.Columns, info.Columns.Select(c => c.Column.Name))[1].Descending);
                Assert.Contains(info.Constraints, c => c.Type == "U" && c.Name.ToUpperInvariant() == "UK_DBH_IT_CODE");
                Assert.DoesNotContain(info.Constraints, c => c.Name.ToUpperInvariant() == "CK_DBH_IT_AMT");
                if (context.Ddl.CanDisable("R")) Assert.Equal("DISABLED", info.Constraints.Single(c => c.Type == "R").Status);

                // 4) 형식 바꾸기·컬럼·인덱스 삭제
                context = SchemaPlan.Load(info, session.Provider.TableSchema.Ddl);
                context.Columns.Single(c => c.Name.ToUpperInvariant() == "NOTE_TEXT").DataType = d.Text.Replace("100", "200");
                var ix2 = context.Indexes.Single(i => i.Name.ToUpperInvariant() == "IX_DBH_IT_CODE2");
                ix2.Deleted = true;
                if (ix2.ConstraintIndex)
                {
                    // Oracle: UK_DBH_IT_CODE가 앞 컬럼이 같은 IX_DBH_IT_CODE2를 가져다 쓴다 — 인덱스만 지우면 저장 전에 막는다.
                    _output.WriteLine("인덱스를 쓰는 제약: " + ix2.OwnerConstraint);
                    Assert.Contains(SchemaPlan.Validate(context), e => e.Row == ix2);
                    context.Constraints.Single(c => c.Name == ix2.OwnerConstraint).Deleted = true;
                }
                await Save(session, context);
                context = SchemaPlan.Load(await Describe(session, d.Fk), session.Provider.TableSchema.Ddl);
                Assert.Contains("200", context.Columns.Single(c => c.Name.ToUpperInvariant() == "NOTE_TEXT").DataType);
                context.Columns.Single(c => c.Name.ToUpperInvariant() == "NOTE_TEXT").Deleted = true;
                await Save(session, context);
                info = await Describe(session, d.Fk);
                Assert.DoesNotContain(info.Columns, c => c.Column.Name.ToUpperInvariant() == "NOTE_TEXT");
                Assert.DoesNotContain(info.Indexes, i => i.Name.ToUpperInvariant() == "IX_DBH_IT_CODE2");
            }
            finally
            {
                await Exec(session, "DROP TABLE " + d.Child, true);
                await Exec(session, "DROP TABLE " + d.Parent, true);
            }
        }

        [DbFact("SCHEMA_IT_PG")]
        public async Task Postgres_DescribeEditSave()
        {
            using (var session = await OpenAsync("SCHEMA_IT_PG", "postgres", "postgres"))
                await RoundTrip(session, new Dialect
                {
                    // 테이블은 따옴표로 대문자 이름, 컬럼·제약은 따옴표 없이(소문자로 저장) — 대소문자가 섞여도 편집이 이름을 그대로 쓰는지 본다
                    Parent = "\"DBH_IT_PARENT\"", Child = "\"DBH_IT_CHILD\"", Fk = "DBH_IT_CHILD",
                    Int = "integer", Text = "varchar(100)", Num = "numeric(10,2)",
                    Comment = c => "COMMENT ON COLUMN \"DBH_IT_CHILD\".code IS '" + c + "'"
                });
        }

        [DbFact("SCHEMA_IT_MSSQL")]
        public async Task SqlServer_DescribeEditSave()
        {
            using (var session = await OpenAsync("SCHEMA_IT_MSSQL", "mssql", "sa"))
                await RoundTrip(session, new Dialect
                {
                    Parent = "DBH_IT_PARENT", Child = "DBH_IT_CHILD", Fk = "DBH_IT_CHILD",
                    Int = "int", Text = "nvarchar(100)", Num = "decimal(10,2)",
                    Comment = c => "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'" + c + "', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'DBH_IT_CHILD', @level2type = N'COLUMN', @level2name = N'CODE'"
                });
        }

        [DbFact("SCHEMA_IT_ORACLE")]
        public async Task Oracle_DescribeEditSave()
        {
            using (var session = await OpenAsync("SCHEMA_IT_ORACLE", "oracle", "system"))
                await RoundTrip(session, new Dialect
                {
                    Parent = "DBH_IT_PARENT", Child = "DBH_IT_CHILD", Fk = "DBH_IT_CHILD",
                    Int = "NUMBER(10)", Text = "VARCHAR2(100)", Num = "NUMBER(10,2)", Descending = false,
                    Comment = c => "COMMENT ON COLUMN DBH_IT_CHILD.CODE IS '" + c + "'"
                });
        }
    }
}
