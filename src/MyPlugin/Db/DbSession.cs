using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>
    /// 조회 결과의 열린 커서(DbDataReader). 다음 행은 <see cref="DbSession.FetchAsync"/>로 이어서 읽는다(다시 조회하지 않음).
    /// "더 있음"은 한 행을 미리 읽어 판단하므로 미리 읽은 행은 다음 Fetch에서 먼저 돌려준다(행을 잃지 않음).
    /// </summary>
    public sealed class QueryCursor
    {
        public List<ResultColumn> Columns { get; } = new List<ResultColumn>();

        /// <summary>지금까지 돌려준 행 수.</summary>
        public int Fetched { get; internal set; }

        /// <summary>더 읽을 행이 있으면 true(커서 열림). 마지막 행까지 읽으면 false가 되고 커서는 닫힌다.</summary>
        public bool HasMore { get; internal set; }

        /// <summary>닫혔으면 true(끝까지 읽음, 새 실행, 세션 닫힘, CloseCursorAsync).</summary>
        public bool IsClosed { get; internal set; }
    }

    public sealed class ExecuteResult
    {
        public SqlKind Kind { get; set; }

        /// <summary>Query만. 다음 행을 가져올 때 쓴다(HasMore가 false면 이미 닫힘).</summary>
        public QueryCursor Cursor { get; set; }

        /// <summary>Query의 첫 묶음. 값은 ValueFormatter.Format 결과(null = NULL).</summary>
        public List<string[]> Rows { get; set; }

        /// <summary>DML 영향 행 수. 모르면 -1.</summary>
        public int RecordsAffected { get; set; } = -1;

        public TimeSpan Elapsed { get; set; }

        /// <summary>COMMIT·ROLLBACK·DDL(자동 커밋)로 트랜잭션이 끝났으면 true.</summary>
        public bool TransactionEnded { get; set; }

        /// <summary>메시지 탭에 쓸 한 줄. 예: "200행 · 더 있음", "3행 변경됨 (커밋 전)", "커밋함", "실행함(DDL은 자동 커밋됨)".</summary>
        public string Summary { get; set; }
    }

    /// <summary>같은 세션에서 다른 실행이 진행 중일 때(DB마다 세션 1개, 한 번에 한 문장).</summary>
    public sealed class SessionBusyException : InvalidOperationException
    {
        public SessionBusyException() : base("같은 DB에서 다른 실행이 진행 중입니다. 끝난 뒤 다시 실행하세요.") { }
    }

    /// <summary>
    /// DB(접속) 하나의 세션. 연결 1개를 오래 유지하고 모든 연결 사용을 내부 잠금(SemaphoreSlim) 아래에서 한다.
    /// - ExecuteAsync·FetchAsync·CommitAsync·RollbackAsync·CloseCursorAsync: 잠금을 바로 못 얻으면 <see cref="SessionBusyException"/>(기다리지 않음).
    /// - RunAsync(트리·검색 같은 메타데이터 조회): 잠금을 기다린다(ct로 기다림 취소).
    /// - 실제 DB 호출은 Task.Run 안에서 동기 ADO.NET으로 한다(공급자의 async가 동기로 동작해도 UI가 멈추지 않게).
    /// - 자동 커밋 끔: DML·PL/SQL·SELECT FOR UPDATE·SAVEPOINT 전에 트랜잭션을 시작하고(없으면), 그 뒤 모든 명령의 Transaction에 넣는다.
    /// - DDL: 트랜잭션이 있으면 먼저 Commit(Oracle의 묵시적 커밋과 같은 결과를 모든 공급자에서 보장) 후 트랜잭션 없이 실행. TransactionEnded = true.
    /// - COMMIT·ROLLBACK 문장은 SQL로 보내지 않고 CommitAsync·RollbackAsync와 같게 처리한다.
    /// - Query: fetchCount+1행을 읽어 HasMore 판단. 끝까지 읽었으면 리더를 닫는다. 같은 연결의 다른 열린 커서는 그대로 둔다.
    /// - 값: OracleDataReader면 GetProviderSpecificValue(큰 NUMBER·TIMESTAMP WITH TIME ZONE 보존), 아니면 GetValue → ValueFormatter.Format.
    ///   IDisposable 값(OracleClob·OracleBlob 등)은 형식화 직후 Dispose한다.
    /// - OracleCommand면 BindByName = true, InitialLOBFetchSize = ValueFormatter.MaxTextLength. CommandTimeout = 0(사용자가 취소).
    /// - Cancel: 실행 중인 명령의 DbCommand.Cancel()(다른 스레드에서 호출 가능). 취소된 실행은 예외로 끝나고 세션은 계속 쓸 수 있다.
    /// - 연결이 끊긴 오류(ORA-03113, 03114, 03135, 12570, 12571, 02396, 01012 등)가 나면 IsBroken = true.
    /// 테스트는 SQLite(Microsoft.Data.Sqlite) 연결로 한다 — Oracle 전용 코드는 형식 검사(is OracleCommand)로 분기한다.
    /// </summary>
    public sealed class DbSession : IDisposable
    {
        /// <summary>연결을 받아 세션을 만든다(아직 안 열림). 세션이 연결의 수명을 맡는다.</summary>
        public DbSession(DbConnection connection)
        {
            throw new NotImplementedException();
        }

        /// <summary>OracleConnection으로 세션을 만든다.</summary>
        public static DbSession CreateOracle(string connectionString)
        {
            throw new NotImplementedException();
        }

        public bool IsOpen { get { throw new NotImplementedException(); } }

        /// <summary>연결이 끊긴 오류가 났음. 다시 연결해야 한다.</summary>
        public bool IsBroken { get { throw new NotImplementedException(); } }

        /// <summary>실행·가져오기·커밋 등이 진행 중(잠금을 잡고 있음).</summary>
        public bool IsBusy { get { throw new NotImplementedException(); } }

        /// <summary>커밋하지 않은 변경(또는 FOR UPDATE 잠금)이 있음.</summary>
        public bool HasPendingChanges { get { throw new NotImplementedException(); } }

        /// <summary>커밋 대기 DML 영향 행 수의 합(표시용).</summary>
        public int PendingRowCount { get { throw new NotImplementedException(); } }

        /// <summary>PL/SQL 실행 등으로 대기 행 수를 알 수 없음.</summary>
        public bool PendingCountUnknown { get { throw new NotImplementedException(); } }

        /// <summary>표시용. 대기가 없으면 null, "커밋 대기 3행", 수를 모르면 "커밋 대기(행 수 모름)".</summary>
        public string PendingText { get { throw new NotImplementedException(); } }

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>문장 하나를 실행한다. 이 세션에서 다른 작업 중이면 SessionBusyException.</summary>
        public Task<ExecuteResult> ExecuteAsync(SqlStatement statement, int fetchCount, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>열린 커서에서 다음 count행. 끝에 닿으면 cursor.HasMore = false, 커서를 닫는다. 닫힌 커서면 빈 목록.</summary>
        public Task<List<string[]>> FetchAsync(QueryCursor cursor, int count, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>커서를 닫는다(이미 닫혔으면 아무것도 안 함).</summary>
        public Task CloseCursorAsync(QueryCursor cursor)
        {
            throw new NotImplementedException();
        }

        /// <summary>메타데이터 조회 등: 잠금을 기다린 뒤 work(연결, 현재 트랜잭션 또는 null)를 Task.Run에서 실행한다.</summary>
        public Task<T> RunAsync<T>(Func<DbConnection, DbTransaction, T> work, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>실행 중인 명령을 취소한다. 실행 중이 아니면 아무것도 안 함.</summary>
        public void Cancel()
        {
            throw new NotImplementedException();
        }

        public Task CommitAsync()
        {
            throw new NotImplementedException();
        }

        public Task RollbackAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>닫기: 실행 중이면 취소하고 끝나기를 기다린 뒤, commit이면 커밋·아니면 롤백하고 모든 커서와 연결을 닫는다.</summary>
        public Task CloseAsync(bool commit)
        {
            throw new NotImplementedException();
        }

        /// <summary>즉시 정리(앱 종료 등): 취소 → 커서 닫기 → 롤백 → 연결 닫기. 예외를 밖으로 내지 않는다.</summary>
        public void Dispose()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// 사람이 읽을 오류 문장. AggregateException·TargetInvocationException을 벗긴다.
        /// ORA-01013(취소) → "실행을 취소했습니다.", 연결 끊김 → "DB 연결이 끊겼습니다. 다시 연결하세요. (ORA-…)", 그 밖은 예외 메시지(ORA-번호 포함) 첫 줄.
        /// </summary>
        public static string DescribeError(Exception ex)
        {
            throw new NotImplementedException();
        }

        /// <summary>취소로 끝난 실행인지(ORA-01013, OperationCanceledException 등).</summary>
        public static bool IsCancellation(Exception ex)
        {
            throw new NotImplementedException();
        }
    }
}
