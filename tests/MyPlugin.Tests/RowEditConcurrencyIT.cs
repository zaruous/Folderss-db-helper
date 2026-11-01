using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyPlugin;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.Tests
{
    /// <summary>
    /// 결과 편집 저장(낙관적 동시성·저장점)을 실제 DB 두 세션으로 확인한다(없으면 건너뜀). 환경 변수는 <see cref="TableSchemaIT"/>와 같다:
    ///   SCHEMA_IT_ORACLE·SCHEMA_IT_PG·SCHEMA_IT_MSSQL (+ _USER·_PW).
    /// 세션 A가 조회한 뒤 세션 B가 같은 행을 바꾸고 커밋 → A의 저장은 0행(충돌)으로 멈추고 저장점까지 되돌린다(저장 전 A의 커밋 대기는 남음).
    /// 시험마다 DBH_IT_ED_xxxxxx 테이블을 만들고 끝나면 지운다.
    /// </summary>
    public class RowEditConcurrencyIT
    {
        private readonly ITestOutputHelper _output;
        public RowEditConcurrencyIT(ITestOutputHelper output) { _output = output; }

        sealed class DbFact : FactAttribute
        {
            public DbFact(string prefix)
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(prefix)))
                    Skip = prefix + "가 없어 실제 DB 시험을 건너뜁니다.";
            }
        }

        sealed class Dialect
        {
            public string Prefix, Kind, User, Int, Text, Num, Ts, Char, Float;
            public bool Lower;
            public string Ts1, Ts2;
            /// <summary>DBMS 고유 형식 열(마지막 열, 없으면 null): "이름 형식"과 넣을 값.</summary>
            public string Extra, ExtraValue;
        }

        static readonly Dialect Oracle = new Dialect
        {
            Prefix = "SCHEMA_IT_ORACLE", Kind = "oracle", User = "system", Int = "NUMBER(10)", Text = "VARCHAR2(20)", Num = "NUMBER(10,2)",
            Ts = "TIMESTAMP(6)", Char = "CHAR(3)", Float = "BINARY_DOUBLE",
            Ts1 = "TIMESTAMP '2024-01-02 03:04:05.123456'", Ts2 = "TIMESTAMP '2024-05-06 07:08:09'"
        };

        static readonly Dialect Postgres = new Dialect
        {
            Prefix = "SCHEMA_IT_PG", Kind = "postgres", User = "postgres", Int = "integer", Text = "varchar(20)", Num = "numeric(10,2)",
            Ts = "timestamp(6)", Char = "char(3)", Float = "double precision", Lower = true,
            Ts1 = "TIMESTAMP '2024-01-02 03:04:05.123456'", Ts2 = "TIMESTAMP '2024-05-06 07:08:09'"
        };

        static readonly Dialect SqlServer = new Dialect
        {
            Prefix = "SCHEMA_IT_MSSQL", Kind = "mssql", User = "sa", Int = "int", Text = "nvarchar(20)", Num = "decimal(10,2)",
            Ts = "datetime2(7)", Char = "nchar(3)", Float = "float",
            Ts1 = "'2024-01-02 03:04:05.1234567'", Ts2 = "'2024-05-06 07:08:09'",
            // datetime(1/300초): 읽은 값(.0033333)과 datetime2 바인드 비교가 맞아야 한다
            Extra = "OLD datetime", ExtraValue = "'2024-01-02 03:04:05.003'"
        };

        [DbFact("SCHEMA_IT_PG")]
        public Task Postgres_ConflictAndSavepoint() { return ConflictAndSavepoint(Postgres); }

        [DbFact("SCHEMA_IT_MSSQL")]
        public Task SqlServer_ConflictAndSavepoint() { return ConflictAndSavepoint(SqlServer); }

        [DbFact("SCHEMA_IT_ORACLE")]
        public Task Oracle_ConflictAndSavepoint() { return ConflictAndSavepoint(Oracle); }

        static async Task<DbSession> OpenAsync(Dialect d)
        {
            var dsn = Environment.GetEnvironmentVariable(d.Prefix).Trim();
            var hostPort = dsn.Split('/')[0];
            var profile = new OracleConnectionProfile
            {
                Id = d.Kind, Name = d.Kind + " 시험", Provider = d.Kind,
                Host = hostPort.Split(':')[0], Port = int.Parse(hostPort.Split(':')[1]), ServiceName = dsn.Split('/')[1],
                UserId = Environment.GetEnvironmentVariable(d.Prefix + "_USER") ?? d.User
            };
            var cs = OracleConnectionStore.BuildConnectionString(profile, Environment.GetEnvironmentVariable(d.Prefix + "_PW") ?? "", ShellLogic.ConnectTimeoutSeconds);
            var session = DbSession.Create(DbProviders.For(profile), cs);
            await session.OpenAsync();
            return session;
        }

        static Task Ddl(DbSession session, string sql)
        {
            return session.ExecuteAsync(SchemaRead.DdlStatement(sql), 0);
        }

        static async Task<int> Dml(DbSession session, string sql)
        {
            var result = await session.ExecuteAsync(new SqlStatement { Text = sql, Kind = SqlKind.Dml, Verb = sql.Split(' ')[0].ToUpperInvariant() }, 0);
            return result.RecordsAffected;
        }

        /// <summary>앱의 빠른 조회처럼 읽는다: 열(형식 표시 포함)과 행(표시 글). 커서는 닫는다.</summary>
        static async Task<KeyValuePair<List<ResultColumn>, List<string[]>>> Select(DbSession session, string sql)
        {
            var result = await session.ExecuteAsync(new SqlStatement { Text = sql, Kind = SqlKind.Query, Verb = "SELECT" }, 100);
            var columns = new List<ResultColumn>(result.Cursor.Columns);
            await session.CloseCursorAsync(result.Cursor);
            return new KeyValuePair<List<ResultColumn>, List<string[]>>(columns, result.Rows);
        }

        static string[] With(string[] values, int index, string value)
        {
            var copy = (string[])values.Clone();
            copy[index] = value;
            return copy;
        }

        async Task ConflictAndSavepoint(Dialect d)
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            var table = d.Lower ? "dbh_it_ed_" + suffix : "DBH_IT_ED_" + suffix.ToUpperInvariant();
            string N(string column) { return d.Lower ? column.ToLowerInvariant() : column; }
            var a = await OpenAsync(d);
            var b = await OpenAsync(d);
            try
            {
                await Ddl(a, "CREATE TABLE " + table + " (" + N("ID") + " " + d.Int + " NOT NULL PRIMARY KEY, " + N("NAME") + " " + d.Text + ", "
                    + N("QTY") + " " + d.Num + ", " + N("TS") + " " + d.Ts + ", " + N("NOTE") + " " + d.Text + ", " + N("CODE") + " " + d.Char + ", " + N("RATE") + " " + d.Float + (d.Extra != null ? ", " + d.Extra : "") + ")");
                for (var i = 1; i <= 5; i++)
                {
                    await Dml(a, "INSERT INTO " + table + " VALUES (" + i + ", 'n" + i + "', " + i + ".50, " + (i % 2 == 0 ? d.Ts2 : d.Ts1) + ", NULL, 'c" + i + "', 0.1" + (d.Extra != null ? ", " + d.ExtraValue : "") + ")");
                }
                await a.CommitAsync();

                // A가 조회(원래 값)
                var read = await Select(a, "SELECT * FROM " + table + " ORDER BY " + N("ID"));
                var columns = read.Key;
                var rows = read.Value;
                _output.WriteLine(string.Join(", ", columns.Select(c => c.Name + ":" + c.TypeLabel)));
                foreach (var row in rows)
                    _output.WriteLine(string.Join(" | ", row.Select(v => v ?? "NULL")));
                var meta = columns.Select((c, i) => new ColumnInfo { Name = c.Name, PrimaryKey = i == 0, TypeLabel = c.TypeLabel, Position = i + 1 }).ToList();
                var edit = RowEditLogic.Describe(null, table, columns, meta, RowEditLogic.ParameterPrefix(d.Kind));
                edit.ProviderKind = d.Kind;
                Assert.True(edit.CanEdit, edit.DisabledReason);
                const int Name = 1, Qty = 2, Note = 4;

                // B가 1행 NAME, 3행 NOTE(NULL → 값)를 바꾸고 커밋
                Assert.Equal(1, await Dml(b, "UPDATE " + table + " SET " + N("NAME") + " = 'other' WHERE " + N("ID") + " = 1"));
                Assert.Equal(1, await Dml(b, "UPDATE " + table + " SET " + N("NOTE") + " = 'x' WHERE " + N("ID") + " = 3"));
                await b.CommitAsync();

                // 저장 전 A의 커밋 대기 변경: 2행 QTY(바뀌지 않은 행 — 시각 초 미만·CHAR·NULL 비교가 거짓 충돌을 내지 않음)
                var early = await a.ExecuteDmlBatchAsync(new[] { RowEditLogic.UpdateStatement(edit, rows[1], With(rows[1], Qty, "22.25")) }, 1);
                Assert.True(early.Succeeded, early.Error?.Message);

                // 1) UPDATE 충돌: 4행(성공) 다음 1행(B가 NAME을 바꿈) → 0행, 이번 저장(4행 포함) 되돌림
                var update4 = RowEditLogic.UpdateStatement(edit, rows[3], With(rows[3], Name, "mine4"));
                var update1 = RowEditLogic.UpdateStatement(edit, rows[0], With(rows[0], Name, "mine1"));
                _output.WriteLine(RowEditLogic.Describe(update1));
                var batch = await a.ExecuteDmlBatchAsync(new[] { update4, update1 }, 1);
                Assert.Equal(1, batch.FailedIndex);
                Assert.Equal(new[] { 1, 0 }, batch.Affected);
                Assert.True(batch.RolledBack, batch.RollbackError?.Message);

                // 2) DELETE 충돌: 5행(바뀌지 않음 — 모든 열 비교가 맞음) 다음 3행(B가 NULL이던 NOTE를 채움) → 0행
                var delete5 = RowEditLogic.DeleteStatement(edit, rows[4]);
                var delete3 = RowEditLogic.DeleteStatement(edit, rows[2]);
                _output.WriteLine(RowEditLogic.Describe(delete3));
                batch = await a.ExecuteDmlBatchAsync(new[] { delete5, delete3 }, 1);
                Assert.Equal(1, batch.FailedIndex);
                Assert.Equal(new[] { 1, 0 }, batch.Affected);
                Assert.True(batch.RolledBack, batch.RollbackError?.Message);

                // 3) 오류: 4행 UPDATE 다음 없는 테이블 → 오류, 되돌림(PostgreSQL은 중단된 트랜잭션이 저장점으로 풀려야 다음 문장이 된다)
                batch = await a.ExecuteDmlBatchAsync(new[] { update4, new SqlQuery { Sql = "UPDATE no_such_table_" + suffix + " SET x = 1" } }, 1);
                Assert.Equal(1, batch.FailedIndex);
                Assert.NotNull(batch.Error);
                Assert.True(batch.RolledBack, batch.RollbackError?.Message);

                // A에서 본 상태: 저장 전 변경(2행)만 남음
                Assert.True(a.HasPendingChanges);
                Assert.Equal(1, a.PendingRowCount);
                var mid = (await Select(a, "SELECT * FROM " + table + " ORDER BY " + N("ID"))).Value;
                Assert.Equal(5, mid.Count);
                Assert.Equal("22.25", mid[1][Qty]);
                Assert.Equal("n4", mid[3][Name]);

                // 4) 충돌 없는 저장은 그대로 된다(5행 삭제·4행 수정) → 커밋
                batch = await a.ExecuteDmlBatchAsync(new[] { delete5, update4 }, 1);
                Assert.True(batch.Succeeded, batch.Error?.Message);
                await a.CommitAsync();

                var final = (await Select(b, "SELECT * FROM " + table + " ORDER BY " + N("ID"))).Value;
                Assert.Equal(new[] { "1", "2", "3", "4" }, final.Select(r => r[0]));
                Assert.Equal("other", final[0][Name]);
                Assert.Equal("22.25", final[1][Qty]);
                Assert.Equal("x", final[2][Note]);
                Assert.Equal("mine4", final[3][Name]);
            }
            finally
            {
                try { await a.RollbackAsync(); } catch (Exception ex) { _output.WriteLine("(무시) " + ex.Message); }
                try { await b.RollbackAsync(); } catch (Exception ex) { _output.WriteLine("(무시) " + ex.Message); }
                try { await Ddl(b, "DROP TABLE " + table); } catch (Exception ex) { _output.WriteLine("(무시) " + ex.Message); }
                a.Dispose();
                b.Dispose();
            }
        }

        /// <summary>Oracle ROWID 키 테이블(기본 키 없음): 조회 뒤 다른 세션이 바꾼 행의 UPDATE·DELETE는 0행.</summary>
        [DbFact("SCHEMA_IT_ORACLE")]
        public async Task Oracle_RowIdKey_DetectsConflict()
        {
            var table = "DBH_IT_ED_" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
            var a = await OpenAsync(Oracle);
            var b = await OpenAsync(Oracle);
            try
            {
                await Ddl(a, "CREATE TABLE " + table + " (NAME VARCHAR2(20), QTY NUMBER)");
                await Dml(a, "INSERT INTO " + table + " VALUES ('a', 1)");
                await Dml(a, "INSERT INTO " + table + " VALUES ('b', NULL)");
                await a.CommitAsync();
                var read = await Select(a, "SELECT " + table + ".*, ROWIDTOCHAR(ROWID) AS \"" + RowEditLogic.RowIdAlias + "\" FROM " + table + " ORDER BY NAME");
                var columns = read.Key.Take(2).ToList();
                var rows = read.Value;
                var edit = RowEditLogic.Describe(null, table, columns, columns.Select(c => new ColumnInfo { Name = c.Name, Nullable = true, TypeLabel = c.TypeLabel }).ToList(), ":",
                    new RowKey { Kind = RowKeyKind.RowId });
                edit.ProviderKind = "oracle";

                Assert.Equal(1, await Dml(b, "UPDATE " + table + " SET QTY = 9 WHERE NAME = 'a'"));
                Assert.Equal(1, await Dml(b, "UPDATE " + table + " SET QTY = 5 WHERE NAME = 'b'"));
                await b.CommitAsync();

                var update = RowEditLogic.UpdateStatement(edit, rows[0].Take(2).ToArray(), new[] { "a", "2" }, rows[0][2]);
                var delete = RowEditLogic.DeleteStatement(edit, rows[1].Take(2).ToArray(), rows[1][2]);
                _output.WriteLine(RowEditLogic.Describe(update));
                _output.WriteLine(RowEditLogic.Describe(delete));
                var batch = await a.ExecuteDmlBatchAsync(new[] { update }, 1);
                Assert.Equal(new[] { 0 }, batch.Affected);
                Assert.True(batch.RolledBack);
                batch = await a.ExecuteDmlBatchAsync(new[] { delete }, 1);
                Assert.Equal(new[] { 0 }, batch.Affected);
                Assert.True(batch.RolledBack);
                Assert.False(a.HasPendingChanges);
            }
            finally
            {
                try { await a.RollbackAsync(); } catch (Exception ex) { _output.WriteLine("(무시) " + ex.Message); }
                try { await Ddl(b, "DROP TABLE " + table); } catch (Exception ex) { _output.WriteLine("(무시) " + ex.Message); }
                a.Dispose();
                b.Dispose();
            }
        }
    }
}
