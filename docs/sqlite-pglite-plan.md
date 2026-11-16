# SQLite · PGlite 지원 계획 (초안)

작성일 2026-11-16. 상태: **결정 대기**(9절의 질문에 답이 오기 전에는 구현하지 않는다).

## 0. 요약

| | SQLite | PGlite |
|---|---|---|
| .NET에서 직접 열 수 있나 | 예 — `Microsoft.Data.Sqlite 8.0.10`이 이미 의존성에 있다(`MonitorStore`, 시험 세션) | **아니오** — PGlite는 WASM으로 빌드한 Postgres이고 JS(Node·브라우저) 안에서만 돈다. .NET 바인딩이 없다 |
| 현실적인 연결 경로 | 새 공급자 `SqliteProvider` | Node에서 `@electric-sql/pglite-socket`을 띄워 PG 와이어 프로토콜로 열고, 기존 `PostgresProvider`(Npgsql)로 붙는다 |
| 새 NuGet 의존성 | 없음 | 없음(대신 **사용자 PC에 Node + npm 패키지**가 필요) |
| 규모 | 중(공급자 1개 + 접속 화면 분기 + 비밀번호 흐름) | A안이면 소(문서 + 작은 호환 조정), B안이면 대(프로세스 관리) |

권장: **SQLite를 먼저 한다. PGlite는 A안(외부에서 띄운 pglite-socket에 PostgreSQL 접속으로 붙기)을 "실험적 지원"으로 문서화하고, 실제 호환 문제가 확인되면 그때 조정한다.** B안(플러그인이 Node를 띄움)은 Node를 번들하거나 설치를 강제해야 해서 권하지 않는다.

## 1. 현재 구조에서 붙는 자리

- `src/MyPlugin/Db/Providers/IDbProvider.cs` — 공급자 인터페이스(접속·방언·Lex·메타데이터·TableSchema·Monitor·트랜잭션 규칙·오류 판정).
- `src/MyPlugin/Db/Providers/DbProviders.cs` — `All`에 한 줄 등록. `For(DbConnection)`은 `SqliteConnection`을 **Generic**으로 돌려준다.
- `OracleConnectionProfile`(`src/MyPlugin/OracleConnections.cs`) — Host/Port/ServiceName/UserId/비밀번호 전제. `Copy`·`SameAs`에 필드를 손으로 나열.
- `ConnectionManagerDialog` — 호스트·포트·서비스·사용자·비밀번호 칸. SQL Server 전용 칸(`_sqlOnly`)을 공급자별로 보이고 숨기는 선례가 있다.
- `DbHelperView` 연결 흐름(892, 1260행 부근) — 저장된 비밀번호가 비면 **무조건 `PasswordPrompt.Ask`**. 연결 후 `Metadata.CurrentSchema()`가 비면 처리 분기.

## 2. SQLite 공급자 설계

### 2.1 접속 프로필
- `Provider = "sqlite"`. 파일 경로는 **`ServiceName`에 저장**(JSON 이름 `serviceName` 유지 — 새 필드를 만들지 않아 `Copy`/`SameAs`/저장 호환을 건드리지 않는다). 라벨 `ServiceLabel = "파일"`.
- Host·Port·UserId·비밀번호는 쓰지 않는다. `DefaultPort = 0`.
- `ReadOnly = true`면 연결 문자열 `Mode=ReadOnly`로 **DB 단에서도** 막는다(다른 공급자는 플러그인 차단만 됨 — SQLite는 더 강하게 할 수 있음).
- `Validate`: 경로 비어 있음, 파일 없음(새로 만들지 여부는 9절 질문). 호스트·포트·사용자 검사는 하지 않는다.
- 연결 문자열: `Data Source=<경로>; Mode=ReadWrite|ReadOnly; Pooling=False; Default Timeout=<초>`. **`Mode=ReadWriteCreate`는 기본으로 쓰지 않는다** — 경로 오타가 빈 DB 파일을 조용히 만든다.

### 2.2 화면(접속 관리)
- SQLite일 때 호스트·포트·사용자·비밀번호 칸을 숨기고, 파일 칸 옆에 [찾아보기…](OpenFileDialog) 하나.
- CLAUDE.md 규칙: `docs/controls/connection-manager.yml` 갱신 + `python3 scripts/check_controls_docs.py`.

### 2.3 비밀번호 흐름
- `IDbProvider`에 `bool UsesPassword`(SQLite false)를 더하고, `DbHelperView`·모니터 세션·접속 테스트의 "비밀번호 없으면 묻기"를 그 값으로 건너뛴다. 안 하면 SQLite 연결마다 비밀번호 창이 뜬다.
- 암호화 DB(SQLCipher)는 **범위 밖** — 번들된 `e_sqlite3`는 암호화를 지원하지 않는다. 암호화 파일은 "file is not a database" 오류로 끝난다(오류 문구를 알기 쉽게 바꿀지만 결정).

### 2.4 공급자 속성
| 속성 | 값 | 근거·위험 |
|---|---|---|
| `Dialect` | Generic(`LIMIT`) | 이미 있음 |
| `Lex` | 새 `SqlLexRules.Sqlite`: `[식별자]`·`` `식별자` ``·`"식별자"`, 블록 없음, `CREATE TRIGGER … BEGIN … END;`는 **하나의 문장**으로 묶어야 함 | 트리거 본문의 `;`에서 문장을 자르면 실행이 깨진다 — 반드시 재현 시험 먼저 |
| `DdlAutoCommits` | **false** (SQLite DDL은 트랜잭션 안에서 롤백 가능) | |
| `RequiresNoTransaction` | `VACUUM`, `ATTACH`/`DETACH`(트랜잭션 안 불가), `PRAGMA journal_mode=…` 등 | 목록을 `DdlTransactionRules`에 추가 |
| `SupportsMultipleOpenReaders` | true | 실측 확인 |
| `IsCancellation` | `SqliteException` SQLITE_INTERRUPT(9) | **확인 필요**: Microsoft.Data.Sqlite의 `Cancel()`/토큰 취소가 긴 단일 문장(대량 집계)을 실제로 멈추는지. 안 멈추면 [취소] 버튼이 무력 — 문서화하거나 `sqlite3_interrupt` 사용 검토 |
| `IsBroken` | 거의 false(파일 삭제·이동 정도) | |
| `IsPermissionDenied` | SQLITE_READONLY(8)·SQLITE_PERM(3) | |
| `Monitor` | `NoMonitor.Instance` | 성능 모니터·전체 상태·일일 점검 메뉴는 비활성 |
| `TableSchema` | 1차: **읽기 전용 F4**(열·인덱스·FK 보기). 구조 편집은 안 함 | SQLite `ALTER TABLE`은 열 변경·제약 변경이 없어 "새 테이블 만들고 복사" 12단계 절차가 필요 — 데이터 손실 위험이 커서 1차 범위 밖 |

### 2.5 데이터 사전(IMetadataProvider)
- 스키마 = `PRAGMA database_list`(main, temp, ATTACH한 DB). `CurrentSchema()` = `'main'`.
- 객체: `<schema>.sqlite_schema`(type = table/view/index/trigger). `sqlite_%` 내부 테이블은 시스템으로 분류.
- 열: `pragma_table_xinfo(name, schema)`. 고유 키: `pragma_index_list` + `pragma_index_info`(`partial = 0`, 식 인덱스 제외).
- `RowIdTable`: `WITHOUT ROWID`가 아닌 테이블만. **위험**: `INTEGER PRIMARY KEY`가 없는 테이블의 rowid는 `VACUUM` 때 바뀔 수 있다 → 결과 편집 중 VACUUM이 끼면 엉뚱한 행을 고칠 수 있다. 기본 키·고유 키가 있으면 그걸 우선(지금 로직이 그런지 확인).
- 소스: 뷰·트리거·인덱스의 `sqlite_schema.sql`.
- 주석(COMMENTS)이 없다 → 테이블 정의서는 설명 칸이 전부 빈다. `SupportsTableInfo`를 켤지 결정 필요.
- **바인드 이름**: 데이터 사전 SQL은 `@name`/`$name`. 스키마 이름은 바인드가 안 되는 자리(`"main".sqlite_schema`)가 있어 **식별자 따옴표 처리**를 따로 한다(SQL 주입 경로 주의 — ATTACH 이름은 사용자 입력).
- 동적 형식: 같은 열이 행마다 다른 형식일 수 있다. `ResultGrid`/`ValueFormatter`/Excel 내보내기가 열 형식을 첫 행 기준으로 고정한다면 깨질 수 있음 → 혼합 형식 열 재현 시험 필요.

### 2.6 가장 큰 회귀 위험: 시험 1,200여 건
`DbSessionTests` 등은 `SqliteConnection`을 `DbSession(DbConnection)`에 넘기고, `DbProviders.For(DbConnection)` → **Generic**(Oracle Lex 규칙, `DdlAutoCommits = true`)을 전제로 쓴다.
**`For(DbConnection)`에서 `SqliteConnection` → `SqliteProvider` 매핑을 추가하면 이 시험들의 의미가 바뀐다.** 그래서:
- `For(DbConnection)`은 손대지 않는다. 실제 SQLite 접속은 항상 `DbSession.Create(provider, …)`로 공급자를 명시해서 연다.
- `SqliteProvider` 전용 시험은 새 파일(`SqliteProviderTests.cs`)에서 공급자를 명시해 만든다.

## 3. PGlite 선택지

### A안 (권장): 외부 pglite-socket에 PostgreSQL 접속으로 붙기
사용자가 직접 `npx pglite-server --db=<데이터 폴더> --port=5432`(패키지 `@electric-sql/pglite-socket`)를 띄우고, 접속 관리에서 **PostgreSQL**로 `localhost:5432` 접속.
- 코드 변경은 거의 없다. 필요하면 `Provider = "pglite"`로 `PostgresProvider`를 상속한 얇은 공급자(표시 이름·모니터 끔·동시 연결 경고)만.
- 알려진 제약(조사 결과, **Npgsql로는 아직 검증 안 함**):
  - 기본 동시 연결 수 1. 0.2.x부터 `--max-connections`로 늘릴 수 있지만 쿼리 큐 멀티플렉서라 모든 경우를 다루지 않는다고 스스로 밝힘. 이 플러그인은 SQL 탭 세션 외에 **성능 모니터가 별도 세션**을 연다(`DbHelperView.OpenMonitorSessionAsync`) → 연결 1개 설정이면 모니터를 여는 순간 막히거나 대기.
  - PGlite는 단일 사용자 Postgres라 `pg_stat_activity`·잠금·세션 섹션이 의미가 없다 → 모니터는 끄는 게 맞다.
  - 인증·SSL 없음. 비밀번호 없이 접속되므로 2.3의 `UsesPassword`와 같이 처리.
  - 트랜잭션 중 다른 연결의 쿼리가 끼어들 때의 동작(멀티플렉서) 미검증 → 커밋 대기 상태에서 모니터 조회가 같은 트랜잭션에 섞일 위험이 있다. **동시 연결 1 강제 + 모니터 끔**이 가장 안전.
- 검증 방법: Node + pglite-socket을 띄운 상태에서 기존 `PostgresIT` 시험을 그대로 돌려 실패 목록을 뽑는다(실패가 곧 할 일 목록).

### B안: 플러그인이 Node 프로세스를 직접 띄움
데이터 폴더를 고르면 플러그인이 `node` + pglite-socket을 자식 프로세스로 실행, 빈 포트 할당, 종료 시 정리.
- 위험: 사용자 PC에 Node 설치를 강제하거나 Node 런타임(수십 MB)·npm 패키지를 zip에 번들해야 함(라이선스·업데이트·보안 공급망 책임), 프로세스 고아화·포트 충돌·크래시 복구·백신 오탐. 플러그인 성격(DB 클라이언트)에 비해 운영 부담이 크다.

### C안: .NET 안에서 WASM 실행(Wasmtime 등)
PGlite의 JS 글루(파일시스템 에뮬레이션·와이어 프로토콜)를 다시 구현해야 함. 비현실적 — 제외.

## 4. 단계와 완료 조건

| 단계 | 내용 | 완료 조건(검증 가능) |
|---|---|---|
| S1 | `SqlLexRules.Sqlite` | 트리거 `BEGIN…END;`·`[ident]`·`` `ident` `` 분할/강조 시험 통과, 기존 Lex 시험 무변경 통과 |
| S2 | `SqliteProvider`(접속·검증·연결 문자열·오류 판정·DDL 규칙) + `DbProviders.All` 등록 | `SqliteProviderTests`: 파일 없음 검증 오류, ReadOnly에서 INSERT가 DB 오류로 실패, DDL 롤백 시 테이블이 사라짐, VACUUM이 커밋 대기 중 거부됨 |
| S3 | `IMetadataProvider` 구현 | 임시 파일 DB(테이블·뷰·인덱스·트리거·ATTACH)로 트리 그룹 개수·열·고유 키·소스 시험 |
| S4 | 비밀번호 흐름 `UsesPassword` | 비밀번호 없는 SQLite 프로필 연결 시 프롬프트가 안 뜸(헤드리스 UI 시험) |
| S5 | 접속 관리 화면 분기 + 파일 찾아보기 | `ConnectionManagerHeadlessTests`에 SQLite 칸 표시/숨김, `docs/controls/connection-manager.yml` 갱신, `check_controls_docs.py` 통과 |
| S6 | F4 읽기 전용 / 결과 편집 키 | 혼합 형식 열·WITHOUT ROWID·rowid 테이블 편집 시험 |
| S7 | 도움말·README·plugin.json 버전 | `HelpTests` 통과 |
| P1 | PGlite A안 검증 | pglite-socket 대상 `PostgresIT` 실행 결과 표 → 실패 항목만 별도 이슈로 |

S1~S5만으로 "SQLite 파일 열고 조회·실행·트리 보기"가 된다. S6 이후는 선택.

## 5. 범위 밖(이번에 하지 않음)
- SQLite 구조 편집(ALTER 재작성 절차), SQLCipher 암호화 DB, SQLite 성능 모니터.
- PGlite B안·C안.
- Folderss 파일 목록에서 `.db` 더블클릭으로 바로 열기(편하지만 요청에 없음 — 원하면 별도로).

## 9. 결정이 필요한 질문
1. PGlite는 A안(사용자가 pglite-server를 직접 띄우고 PostgreSQL로 접속)으로 충분한가, 아니면 플러그인이 Node를 띄우는 B안까지 원하는가?
2. SQLite 파일이 없을 때: 오류로 막을지, "새 DB 파일을 만들까요?"를 물을지.
3. SQLite 테이블 정의서(주석이 없어 설명 칸이 빔)를 켤지.
4. SQLite F4는 읽기 전용으로 충분한가(구조 편집 제외)?
