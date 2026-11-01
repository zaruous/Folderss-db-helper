using System;
using System.Data.Common;
using System.Threading.Tasks;
using MyPlugin;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.Tests
{
    /// <summary>
    /// DBMS별 DDL 트랜잭션 정책(#30)을 실제 DB에서 확인한다(없으면 건너뜀). 환경 변수는 <see cref="TableSchemaIT"/>와 같다(SCHEMA_IT_PG·MSSQL·ORACLE).
    /// PostgreSQL·SQL Server: DML 뒤 DDL을 실행해도 DML이 커밋되지 않고 [롤백]이 둘 다 되돌린다. 실패한 DDL은 그것만 되돌리고 앞선 DML은 커밋할 수 있다.
    /// 트랜잭션 안에서 못 하는 문장은 커밋 대기가 있으면 거부한다. Oracle: DDL이 앞선 DML을 커밋한다(그대로).
    /// 시험 테이블은 DBH_IT_DDLTX_xxxxxx(무작위)로 만들고 끝나면 지운다.
    /// </summary>
    public class DdlTransactionIT
    {
        private readonly ITestOutputHelper _output;
        public DdlTransactionIT(ITestOutputHelper output) { _output = output; }

        static string NewName() { return "DBH_IT_DDLTX_" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant(); }

        static Task<ExecuteResult> Run(DbSession session, string sql)
        {
            return session.ExecuteAsync(SqlScript.Parse(sql, session.Provider.Lex), 0);
        }

        static Task<long> Scalar(DbSession session, string sql)
        {
            return session.RunAsync((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            });
        }

        static Task<long> TableCount(DbSession session, string name)
        {
            return Scalar(session, "SELECT COUNT(*) FROM information_schema.tables WHERE LOWER(table_name) = '" + name.ToLowerInvariant() + "'");
        }

        async Task Drop(DbSession session, string name)
        {
            try
            {
                if (session.HasPendingChanges)
                    await session.RollbackAsync();
                await Run(session, "DROP TABLE " + name);
                if (session.HasPendingChanges)
                    await session.CommitAsync();
            }
            catch (Exception ex) { _output.WriteLine("(정리 무시) " + ex.Message); }
        }

        /// <param name="createDupEndsTransaction">이미 있는 테이블 CREATE 오류에 서버가 트랜잭션을 통째로 롤백하는지(SQL Server 2714는 그렇다, PostgreSQL은 세이브포인트로 되돌림).</param>
        async Task TransactionalDdl(DbSession session, string intType, string noTransactionSql, bool createDupEndsTransaction)
        {
            Assert.False(session.Provider.DdlAutoCommits);
            var t = NewName();
            var u = NewName();
            try
            {
                await Run(session, "CREATE TABLE " + t + " (A " + intType + ")");
                await session.CommitAsync();

                // 1) DML → DDL → 롤백: 둘 다 되돌아간다
                await Run(session, "INSERT INTO " + t + " (A) VALUES (1)");
                var ddl = await Run(session, "CREATE TABLE " + u + " (B " + intType + ")");
                _output.WriteLine("DDL → " + ddl.Summary + " / " + session.PendingText);
                Assert.False(ddl.TransactionEnded);
                Assert.True(session.HasPendingChanges);
                Assert.Equal("커밋 대기 1행 · DDL", session.PendingText);
                await session.RollbackAsync();
                Assert.Equal(0, await Scalar(session, "SELECT COUNT(*) FROM " + t));
                Assert.Equal(0, await TableCount(session, u));

                // 2) DML → 실패하는 DDL(이미 있는 컬럼): 그 DDL만 되돌리고 앞선 DML은 커밋할 수 있다(PostgreSQL은 세이브포인트 없으면 트랜잭션이 중단됨)
                await Run(session, "INSERT INTO " + t + " (A) VALUES (2)");
                var error = await Assert.ThrowsAnyAsync<DbException>(() => Run(session, "ALTER TABLE " + t + " ADD A " + intType));
                _output.WriteLine("실패한 DDL → " + error.Message);
                Assert.True(session.HasPendingChanges);
                Assert.False(session.IsBroken);
                await session.CommitAsync();
                Assert.Equal(1, await Scalar(session, "SELECT COUNT(*) FROM " + t));

                // 2b) DML → 이미 있는 테이블 CREATE: 서버가 트랜잭션을 통째로 롤백하면 대기를 지우고 그렇게 알린다
                await Run(session, "INSERT INTO " + t + " (A) VALUES (20)");
                var dup = await Assert.ThrowsAnyAsync<Exception>(() => Run(session, "CREATE TABLE " + t + " (A " + intType + ")"));
                _output.WriteLine("이미 있는 테이블 → " + dup.GetType().Name + ": " + dup.Message);
                if (createDupEndsTransaction)
                {
                    Assert.IsType<TransactionEndedException>(dup);
                    Assert.False(session.HasPendingChanges);
                    Assert.Equal(1, await Scalar(session, "SELECT COUNT(*) FROM " + t));
                }
                else
                {
                    Assert.True(session.HasPendingChanges);
                    await session.RollbackAsync();
                }

                // 3) 트랜잭션 안에서 못 하는 문장: 커밋 대기가 있으면 실행하지 않는다
                await Run(session, "INSERT INTO " + t + " (A) VALUES (3)");
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(session, noTransactionSql));
                _output.WriteLine("거부 → " + refused.Message);
                Assert.True(session.HasPendingChanges);
                await session.RollbackAsync();
                Assert.Equal(1, await Scalar(session, "SELECT COUNT(*) FROM " + t));

                // 4) 대기 없는 실패 DDL은 트랜잭션을 남기지 않는다
                await Assert.ThrowsAnyAsync<DbException>(() => Run(session, "CREATE TABLE " + t + " (A " + intType + ")"));
                Assert.False(session.HasPendingChanges);
            }
            finally
            {
                await Drop(session, u);
                await Drop(session, t);
            }
        }

        [TableSchemaIT.DbFact("SCHEMA_IT_PG")]
        public async Task Postgres_DdlIsTransactional()
        {
            using (var session = await TableSchemaIT.OpenAsync("SCHEMA_IT_PG", "postgres", "postgres"))
            {
                await TransactionalDdl(session, "integer", "VACUUM", false);
                // 대기가 없으면 트랜잭션 밖에서 실행된다(트랜잭션 안이면 25001 오류)
                var vacuum = await Run(session, "VACUUM");
                Assert.True(vacuum.TransactionEnded);
            }
        }

        [TableSchemaIT.DbFact("SCHEMA_IT_MSSQL")]
        public async Task SqlServer_DdlIsTransactional()
        {
            using (var session = await TableSchemaIT.OpenAsync("SCHEMA_IT_MSSQL", "mssql", "sa"))
                await TransactionalDdl(session, "int", "CREATE DATABASE DBH_IT_NEVER_CREATED", true);
        }

        [TableSchemaIT.DbFact("SCHEMA_IT_ORACLE")]
        public async Task Oracle_DdlCommitsPendingDml()
        {
            using (var session = await TableSchemaIT.OpenAsync("SCHEMA_IT_ORACLE", "oracle", "system"))
            {
                Assert.True(session.Provider.DdlAutoCommits);
                var t = NewName();
                var u = NewName();
                try
                {
                    await Run(session, "CREATE TABLE " + t + " (A NUMBER(10))");
                    await Run(session, "INSERT INTO " + t + " (A) VALUES (1)");
                    var ddl = await Run(session, "CREATE TABLE " + u + " (B NUMBER(10))");
                    Assert.True(ddl.TransactionEnded);
                    Assert.Equal("실행함 (DDL은 자동 커밋됨)", ddl.Summary);
                    Assert.False(session.HasPendingChanges);
                    await session.RollbackAsync();
                    // DDL 앞에서 커밋되어 롤백해도 남는다
                    Assert.Equal(1, await Scalar(session, "SELECT COUNT(*) FROM " + t));
                    Assert.Equal(1, await Scalar(session, "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '" + u + "'"));
                }
                finally
                {
                    await Drop(session, u);
                    await Drop(session, t);
                }
            }
        }
    }
}
