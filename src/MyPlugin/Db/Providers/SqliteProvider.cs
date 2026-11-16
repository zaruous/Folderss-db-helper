using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MyPlugin
{
    /// <summary>
    /// SQLite 공급자(Microsoft.Data.Sqlite). 접속 프로필의 ServiceName은 DB 파일 경로다. 호스트·포트·사용자·비밀번호는 쓰지 않는다.
    /// 지원: 접속, 트리(스키마 = main·temp·ATTACH한 DB, 테이블·뷰·열), 검색(main·temp), SQL 실행, F4 정보 보기(편집 없음 — SQLite ALTER 제약), 테이블 정의서(설명 칸 없음).
    /// 성능 모니터 없음. 연결 형식으로 공급자를 고르는 <see cref="DbProviders.For(DbConnection)"/>는 SqliteConnection을 계속 Generic으로 본다(시험 세션 호환) —
    /// 실제 SQLite 접속은 언제나 이 공급자를 명시해 연다(<see cref="DbSession.Create"/>).
    /// </summary>
    internal sealed class SqliteProvider : IDbProvider
    {
        public const string KindName = "sqlite";

        // SQLite 기본 결과 코드(확장 코드의 아래 8비트)
        private const int SqliteInterrupt = 9;
        private const int SqlitePerm = 3;
        private const int SqliteReadOnly = 8;
        private const int SqliteAuth = 23;

        private readonly IMetadataProvider _metadata = new SqliteMetadata();

        public string Kind { get { return KindName; } }
        public string DisplayName { get { return "SQLite"; } }
        public int DefaultPort { get { return 0; } }
        public string ServiceLabel { get { return "파일"; } }
        public ISqlDialect Dialect { get { return SqlDialects.Generic; } }
        public SqlLexRules Lex { get { return SqlLexRules.Sqlite; } }
        public IMetadataProvider Metadata { get { return _metadata; } }
        public IMonitorProvider Monitor { get { return NoMonitor.Instance; } }
        public ITableSchemaProvider TableSchema { get { return SqliteTableSchema.Instance; } }
        public bool UsesPassword { get { return false; } }

        /// <summary>파일 경로만 필수. 파일이 없는 것은 오류가 아니다 — 연결할 때 만들지 묻는다(<see cref="NeedsCreate"/>).</summary>
        public List<string> Validate(OracleConnectionProfile profile, string label)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(profile.ServiceName))
                errors.Add(label + ": 파일을 입력하세요.");
            return errors;
        }

        /// <summary>
        /// 파일이 있어야 연다(ReadWrite·ReadOnly) — 경로 오타가 빈 DB 파일을 조용히 만들지 않게. 새 파일은 확인을 받고 <see cref="CreateDatabaseFile"/>로 만든다.
        /// 읽기 전용 접속은 SQLite 자체도 읽기 전용(Mode=ReadOnly)으로 연다. 비밀번호는 쓰지 않는다(암호화 DB는 지원하지 않음).
        /// Default Timeout은 명령 시간 제한이자 잠금 대기(busy timeout)다.
        /// </summary>
        public string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = PathOf(profile),
                Mode = profile.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = timeoutSeconds
            };
            return builder.ConnectionString;
        }

        public DbConnection CreateConnection(string connectionString)
        {
            return new SqliteConnection(connectionString);
        }

        public void ConfigureProcess()
        {
        }

        public string ServerVersionOf(DbConnection connection)
        {
            // 화면이 제품 이름(DisplayName)을 앞에 붙인다 → "SQLite 3.46.1"
            return connection.ServerVersion;
        }

        /// <summary>파일 경로.</summary>
        public string Address(OracleConnectionProfile profile)
        {
            return profile == null ? "" : PathOf(profile);
        }

        public bool SupportsMultipleOpenReaders { get { return true; } }

        /// <summary>SQLite DDL은 트랜잭션 안에서 실행되고 롤백된다.</summary>
        public bool DdlAutoCommits { get { return false; } }

        public bool RequiresNoTransaction(SqlStatement statement) { return DdlTransactionRules.SqliteNoTransaction(statement); }

        /// <summary>SQLITE_INTERRUPT.</summary>
        public bool IsCancellation(Exception ex)
        {
            var lite = ex as SqliteException;
            return lite != null && (lite.SqliteErrorCode & 0xFF) == SqliteInterrupt;
        }

        /// <summary>파일 DB는 연결이 "끊기지" 않는다.</summary>
        public bool IsBroken(Exception ex)
        {
            return false;
        }

        /// <summary>SQLITE_PERM·SQLITE_READONLY·SQLITE_AUTH.</summary>
        public bool IsPermissionDenied(Exception ex)
        {
            var lite = ex as SqliteException;
            if (lite == null)
                return false;
            var code = lite.SqliteErrorCode & 0xFF;
            return code == SqlitePerm || code == SqliteReadOnly || code == SqliteAuth;
        }

        /// <summary>
        /// 실행 중인 문장을 멈춘다(sqlite3_interrupt — 다른 스레드에서 불러도 된다). 멈춘 문장은 SQLITE_INTERRUPT 오류로 끝난다(<see cref="IsCancellation"/>).
        /// 연결이 SqliteConnection이 아니거나 열려 있지 않으면 아무것도 안 함.
        /// </summary>
        public static void Interrupt(DbConnection connection)
        {
            var lite = connection as SqliteConnection;
            if (lite != null && lite.Handle != null)
                SQLitePCL.raw.sqlite3_interrupt(lite.Handle);
        }

        /// <summary>
        /// 바인드할 숫자: 소수 부분이 없고 long 범위면 long(INTEGER), 아니면 double(REAL). Microsoft.Data.Sqlite는 decimal을 글('2.5')로 묶어
        /// 형식(친화성) 없는 열에 든 숫자와 비교하면 같지 않다(결과 편집의 원래 값 비교가 거짓 충돌이 됨).
        /// </summary>
        public static object NumberValue(decimal value)
        {
            if (value == decimal.Truncate(value) && value >= long.MinValue && value <= long.MaxValue)
                return (long)value;
            return (double)value;
        }

        // ---------- 파일 ----------

        public static string PathOf(OracleConnectionProfile profile)
        {
            return (profile.ServiceName ?? "").Trim();
        }

        /// <summary>파일이 없어 연결 전에 만들지 물어야 하는지. 메모리 DB(:memory:)·URI(file:)는 묻지 않는다.</summary>
        public static bool NeedsCreate(OracleConnectionProfile profile)
        {
            var path = PathOf(profile);
            if (path.Length == 0 || path == ":memory:" || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return false;
            return !File.Exists(path);
        }

        /// <summary>빈 DB 파일을 만든다(폴더는 있어야 한다). 이미 있으면 그대로 둔다.</summary>
        public static void CreateDatabaseFile(string path)
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
            using (var connection = new SqliteConnection(builder.ConnectionString))
            {
                connection.Open();
            }
        }
    }
}
