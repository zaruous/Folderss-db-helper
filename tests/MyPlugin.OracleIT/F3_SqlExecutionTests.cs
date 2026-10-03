using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace MyPlugin.OracleIT
{
    /// <summary>
    /// 기능 3. SQL 실행 — 문장 나누기·위험 판정 → 실행·결과 이어 읽기 → 트랜잭션(커밋 대기·롤백·커밋) → PL/SQL·컴파일 오류 → 취소.
    /// SqlWorkspace.RunAsync가 하는 것처럼 SqlScript로 문장을 만들고 DbSession.ExecuteAsync/FetchAsync로 실행한다.
    /// </summary>
    [Collection(OracleCollection.Name)]
    public sealed class F3_SqlExecutionTests : OracleTestBase
    {
        public F3_SqlExecutionTests(OracleFixture db, ITestOutputHelper output) : base(db, output, "F3 SQL") { }

        [OracleFact]
        public async Task F3_1_ScriptSplit_KindsDangers_AndOdpAcceptsEachText()
        {
            var script =
                "-- 조회\n" +
                "SELECT ORDER_ID FROM ORDERS WHERE ORDER_ID < 3;\n" +
                "UPDATE ORDERS SET AMOUNT = 0;\n" +
                "\n" +
                "BEGIN\n" +
                "  P_TOUCH(1);\n" +
                "END;\n" +
                "/\n" +
                "DELETE FROM ORDERS WHERE ORDER_ID = -1;\n" +
                "DROP TABLE ORDER_ITEMS;\n";
            var statements = SqlScript.Split(script);
            Log("문장 " + statements.Count + "개: " + string.Join(" | ", statements.Select(s => s.Kind + "/" + s.Verb + (s.Danger != null ? "(위험)" : ""))));
            Assert.Equal(new[] { SqlKind.Query, SqlKind.Dml, SqlKind.PlSql, SqlKind.Dml, SqlKind.Ddl }, statements.Select(s => s.Kind).ToArray());
            Assert.Null(statements[0].Danger);
            Assert.NotNull(statements[1].Danger);   // WHERE 없는 UPDATE
            Assert.Null(statements[3].Danger);
            Assert.NotNull(statements[4].Danger);   // DROP
            Assert.EndsWith("END;", statements[2].Text); // 블록은 ';' 유지, '/' 줄 제외
            Assert.False(statements[0].Text.EndsWith(";"));

            // 커서 위치로 문장 고르기(Ctrl+Enter)
            var caret = script.IndexOf("DELETE", StringComparison.Ordinal) + 3;
            Assert.Equal("DELETE", SqlScript.AtCaret(script, caret).Verb);

            // 위험하지 않은 문장 텍스트가 ODP.NET에서 그대로 실행되는지(';' 때문에 ORA-00911이 나지 않는지)
            using (var session = await Db.OpenSessionAsync())
            {
                foreach (var s in statements.Where(s => s.Danger == null))
                {
                    var r = await session.ExecuteAsync(s, 200);
                    Log("  실행 " + s.Verb + " → " + r.Summary);
                }
                await session.RollbackAsync();
                Assert.False(session.HasPendingChanges);
            }
        }

        [OracleFact]
        public async Task F3_2_Query_FirstPage_FetchMore_ValueFormatting()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var st = Sql("SELECT ORDER_ID, CUSTOMER_NAME, AMOUNT, NOTE, CREATED_AT, MEMO FROM ORDERS ORDER BY ORDER_ID;");
                Assert.True(st.StrippedSemicolon);
                var w = Stopwatch.StartNew();
                var result = await session.ExecuteAsync(st, 200);
                var firstMs = w.ElapsedMilliseconds;
                var cursor = result.Cursor;
                Log("첫 페이지: " + result.Summary + " (" + firstMs + "ms)");
                Log("열 머리: " + string.Join(", ", cursor.Columns.Select(c => c.Name + " " + c.TypeLabel + (c.IsNumeric ? " [숫자]" : ""))));
                Assert.Equal("200행 · 더 있음", result.Summary);
                Assert.Equal(200, result.Rows.Count);
                Assert.True(cursor.HasMore);
                Assert.True(cursor.Columns.Single(c => c.Name == "AMOUNT").IsNumeric);
                Assert.False(cursor.Columns.Single(c => c.Name == "CUSTOMER_NAME").IsNumeric);

                var r1 = result.Rows[0];
                Log("1행: " + string.Join(" | ", r1.Take(5).Select(v => v ?? "(NULL)")) + " | MEMO " + (r1[5]?.Length ?? 0) + "자 …" + r1[5]?.Substring(Math.Max(0, (r1[5]?.Length ?? 0) - 12)));
                Assert.Equal("1", r1[0]);
                Assert.Equal("고객1", r1[1]);
                Assert.Equal("1.5", r1[2]);
                Assert.Equal("메모1", r1[3]);
                Assert.Equal("2026-01-02 01:00:00", r1[4]);
                Assert.StartsWith(new string('x', 4000), r1[5]);
                Assert.EndsWith("…(전체 6000자)", r1[5]);
                Assert.Null(result.Rows[9][3]); // ORDER_ID 10의 NOTE는 NULL
                Assert.Null(result.Rows[1][5]);

                // [다음 행 가져오기]: 다시 조회하지 않고 같은 커서에서 이어 읽는다
                w.Restart();
                var next = await session.FetchAsync(cursor, 200);
                Log("다음 200행: " + next.First()[0] + "~" + next.Last()[0] + " (" + w.ElapsedMilliseconds + "ms), 누적 " + cursor.Fetched);
                Assert.Equal("201", next.First()[0]);
                Assert.Equal("400", next.Last()[0]);
                Assert.True(cursor.HasMore);

                w.Restart();
                var rest = await session.FetchAsync(cursor, 5000);
                Log("나머지: " + rest.Count + "행 (" + w.ElapsedMilliseconds + "ms), HasMore=" + cursor.HasMore + ", 커서 닫힘=" + cursor.IsClosed);
                Assert.Equal(600, rest.Count);
                Assert.Equal("1000", rest.Last()[0]);
                Assert.False(cursor.HasMore);
                Assert.True(cursor.IsClosed);
                Assert.Equal(1000, cursor.Fetched);
                Assert.Empty(await session.FetchAsync(cursor, 200));

                // 결과 복사(TSV)
                var tsv = WorkspaceLogic.ToTsv(cursor.Columns.Select(c => c.Name), result.Rows.Take(2));
                Log("TSV 첫 줄: " + tsv.Split('\n')[0].TrimEnd());
            }
        }

        [OracleFact]
        public async Task F3_3_Dml_PendingInvisibleToOthers_Rollback_ThenCommit()
        {
            using (var session = await Db.OpenSessionAsync())
            using (var other = await Db.OpenSessionAsync())
            {
                const string sumSql = "SELECT SUM(AMOUNT) FROM ORDERS WHERE ORDER_ID <= 5";
                Func<DbSession, Task<decimal>> sum = async s => (await s.QueryAsync(Query(sumSql), r => r.GetDecimal(0))).Single();
                var before = await sum(other);

                var upd = await session.ExecuteAsync(Sql("UPDATE ORDERS SET AMOUNT = 0 WHERE ORDER_ID <= 5"), 200);
                Log("UPDATE → " + upd.Summary + ", 상태: " + session.PendingText);
                Assert.Equal(5, upd.RecordsAffected);
                Assert.True(session.HasPendingChanges);
                Assert.Equal("커밋 대기 5행", session.PendingText);
                Assert.Equal(0m, await sum(session));
                Assert.Equal(before, await sum(other)); // 커밋 전 — 다른 세션에는 안 보임

                await session.RollbackAsync();
                Assert.False(session.HasPendingChanges);
                Assert.Equal(before, await sum(session));
                Log("롤백 후 합계 " + await sum(session) + " (원래 " + before + ")");

                // 편집기에서 INSERT 후 'COMMIT' 문장 실행
                var ins = await session.ExecuteAsync(Sql("INSERT INTO ORDERS (ORDER_ID, CUSTOMER_NAME, AMOUNT) VALUES (5001, '실측', 9.99)"), 200);
                Assert.Equal("커밋 대기 1행", session.PendingText);
                var commit = await session.ExecuteAsync(Sql("COMMIT;"), 200);
                Log("INSERT → " + ins.Summary + " / COMMIT → " + commit.Summary + ", TransactionEnded=" + commit.TransactionEnded);
                Assert.True(commit.TransactionEnded);
                Assert.False(session.HasPendingChanges);
                var seen = await other.QueryAsync(Query("SELECT CUSTOMER_NAME FROM ORDERS WHERE ORDER_ID = :id", ("id", 5001)), r => r.GetString(0));
                Assert.Equal("실측", seen.Single());

                // [커밋] 버튼 경로로 정리
                await session.ExecuteAsync(Sql("DELETE FROM ORDERS WHERE ORDER_ID = 5001"), 200);
                await session.CommitAsync();
                Assert.False(session.HasPendingChanges);
                Assert.Empty(await other.QueryAsync(Query("SELECT 1 FROM ORDERS WHERE ORDER_ID = 5001"), r => 1));
                Log("다른 세션에서 커밋 결과 확인 · [커밋] 버튼 경로로 정리 완료");
            }
        }

        [OracleFact]
        public async Task F3_4_ForUpdateLock_AndPlSqlBlockTransactionProbe()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                // SELECT … FOR UPDATE: 잠금 보유 표시, 롤백하면 풀림
                var fu = await session.ExecuteAsync(Sql("SELECT ORDER_ID FROM ORDERS WHERE ORDER_ID = 1 FOR UPDATE"), 200);
                Log("FOR UPDATE → " + fu.Summary + ", 상태: " + session.PendingText);
                Assert.Equal("커밋 대기(잠금 보유)", session.PendingText);
                await session.RollbackAsync();
                Assert.False(session.HasPendingChanges);

                // 블록 안에서 변경만: 서버 트랜잭션이 남음 → 대기(행 수 모름)
                var b1 = await session.ExecuteAsync(SqlScript.Split("BEGIN\n  P_TOUCH(1);\nEND;\n/\n").Single(), 200);
                Log("BEGIN P_TOUCH END → " + b1.Summary + ", 상태: " + session.PendingText);
                Assert.Equal(SqlKind.PlSql, b1.Kind);
                Assert.True(session.HasPendingChanges);
                Assert.True(session.PendingCountUnknown);

                // 블록 안에서 COMMIT: DBMS_TRANSACTION 확인으로 대기가 끝났음을 안다
                var b2 = await session.ExecuteAsync(Sql("BEGIN P_TOUCH(2); COMMIT; END;"), 200);
                Log("BEGIN P_TOUCH; COMMIT END → " + b2.Summary + ", 대기=" + session.HasPendingChanges);
                Assert.False(session.HasPendingChanges);
                Assert.True(b2.TransactionEnded);

                // EXEC 줄임 → BEGIN … END; 로 바꿔 실행
                var exec = await session.ExecuteAsync(Sql("EXEC P_TOUCH(3)"), 200);
                Log("EXEC P_TOUCH(3) → 텍스트 \"" + Sql("EXEC P_TOUCH(3)").Text + "\", " + exec.Summary);
                await session.RollbackAsync();
            }
        }

        [OracleFact]
        public async Task F3_5_CompileError_IsReportedAsWarningWithErrorLines()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                try
                {
                    var st = SqlScript.Split("CREATE OR REPLACE PROCEDURE P_BROKEN AS\nBEGIN\n  v := ;\nEND;\n/\n").Single();
                    Assert.Equal(SqlKind.Ddl, st.Kind);
                    var r = await session.ExecuteAsync(st, 200);
                    Log("컴파일 오류 프로시저 → " + r.Summary);
                    Log("  경고: " + (r.Warning ?? "(없음)"));
                    foreach (var d in r.WarningDetails)
                        Log("  " + d);
                    Assert.NotNull(r.Warning);
                    Assert.Contains("ORA-24344", r.Warning);
                    Assert.NotEmpty(r.WarningDetails);
                    Assert.Contains(r.WarningDetails, d => d.Contains("PLS-"));

                    var fix = await session.ExecuteAsync(SqlScript.Split("CREATE OR REPLACE PROCEDURE P_BROKEN AS\nBEGIN\n  NULL;\nEND;\n/\n").Single(), 200);
                    Log("고친 뒤 → " + fix.Summary + ", 경고=" + (fix.Warning ?? "없음"));
                    Assert.Null(fix.Warning);
                }
                finally
                {
                    // 실패해도 P_BROKEN이 남아 F2 묶음 개수를 흐리지 않게
                    try { await session.ExecuteAsync(Sql("DROP PROCEDURE P_BROKEN"), 200); } catch (Exception) { }
                }
            }
        }

        // 회귀 시험: OOB 취소는 Docker 포트 포워딩을 지나지 못해 10분 넘게 멈추지 않았다 → DbSession.CreateOracle이 in-band break로 바꾼다.
        // 시험 준비물(SYSTEM 연결)이 먼저 연결을 연 뒤에 바꿔도 적용되는지도 함께 확인된다.
        [OracleFact]
        public async Task F3_6_LongQuery_BusyGuard_Cancel_SessionStillUsable()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var running = session.ExecuteAsync(Sql("SELECT COUNT(*) FROM ALL_OBJECTS a, ALL_OBJECTS b, ALL_OBJECTS c"), 200);
                await Task.Delay(1500);
                Assert.True(session.IsBusy);
                // 같은 DB는 한 번에 한 문장 — 기다리지 않고 바로 거절
                await Assert.ThrowsAsync<SessionBusyException>(() => session.ExecuteAsync(Sql("SELECT 1 FROM DUAL"), 200));

                var w = Stopwatch.StartNew();
                session.Cancel();
                var oob = Oracle.ManagedDataAccess.Client.OracleConfiguration.DisableOOB ? "in-band break" : "OOB break";
                if (await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(20))) != running)
                {
                    Log("취소(" + oob + ") 요청 후 20초가 지나도 실행이 끝나지 않음 — IsBusy=" + session.IsBusy + ". 서버에서 세션을 끊어 정리");
                    Db.KillActiveSessions();
                    try { await running; } catch (Exception killed) { Log("  강제 종료 뒤 사용자 메시지: " + DbSession.DescribeError(killed) + ", IsBroken=" + session.IsBroken); }
                    Assert.Fail("Cancel()이 20초 안에 실행을 멈추지 못함(" + oob + ")");
                }
                Log("취소 방식: " + oob);
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => running);
                Log("취소 요청 → 끝날 때까지 " + w.ElapsedMilliseconds + "ms, 메시지: " + DbSession.DescribeError(ex) + " (원문: " + ex.GetType().Name + " " + ex.Message.Split('\n')[0] + ")");
                Assert.True(DbSession.IsCancellation(ex));
                Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(ex));
                Assert.False(session.IsBroken);

                var after = await session.ExecuteAsync(Sql("SELECT 'ok' FROM DUAL"), 200);
                Assert.Equal("ok", after.Rows.Single()[0]);
                Log("취소 뒤 같은 세션 재사용 OK");
            }
        }

        [OracleFact]
        public async Task F3_7_SqlError_IsReadable_AndSessionKeepsWorking()
        {
            using (var session = await Db.OpenSessionAsync())
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.ExecuteAsync(Sql("SELECT NO_SUCH_COL FROM ORDERS"), 200));
                var described = DbSession.DescribeError(ex);
                Log("없는 열 → " + described + " / 힌트: " + (WorkspaceLogic.ErrorHint(described) ?? "없음"));
                Assert.Contains("ORA-00904", described);
                var ex2 = await Assert.ThrowsAnyAsync<Exception>(() => session.ExecuteAsync(Sql("SELECT * FROM NO_SUCH_TABLE"), 200));
                Log("없는 테이블 → " + DbSession.DescribeError(ex2));
                Assert.Contains("ORA-00942", DbSession.DescribeError(ex2));
                Assert.False(session.IsBroken);
                Assert.Equal("1", (await session.ExecuteAsync(Sql("SELECT 1 FROM DUAL"), 200)).Rows.Single()[0]);
            }
        }
    }
}
