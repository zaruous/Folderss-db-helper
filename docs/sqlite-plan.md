# SQLite 지원 계획 (초안)

작성일 2026-11-16. 상태: **결정 완료**(9절). 구현은 S1부터.

결정(2026-11-16): **SQLite만 진행한다. PGlite는 제외**(3절에 검토 기록만 남김).

## 0. 요약

- SQLite: `Microsoft.Data.Sqlite 8.0.10`이 이미 의존성에 있다(`MonitorStore`, 시험 세션). 새 공급자 `SqliteProvider` 하나를 더한다. 새 NuGet 의존성 없음.
- PGlite: 제외. .NET 바인딩이 없고, 외부 Node 서버(pglite-socket)를 거쳐야 해서 플러그인 안에서 완결되지 않는다(3절).

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
- `Validate`: 경로 비어 있음이면 오류. 호스트·포트·사용자 검사는 하지 않는다.
- 파일이 없으면(결정 2026-11-16) 연결 직전에 "새 DB 파일을 만들까요?"를 묻고, 예일 때만 그 한 번 `Mode=ReadWriteCreate`로 연다. `ReadOnly` 접속이면 묻지 않고 오류.
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
| `TableSchema` | **F4 정보 보기만**. 구조 편집 방언(`ITableSchemaProvider`의 편집 DDL 방언)은 **null** → 정보 창이 기존 seam대로 읽기 전용 | 결정 2026-11-16. 근거는 2.7 |

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

### 2.7 구조 편집을 막는 근거 — SQLite 제약과 DBeaver 사례
- SQLite `ALTER TABLE`은 이름 바꾸기·열 추가·열 삭제(3.35+)만 된다. 열 형식·NOT NULL·기본값·제약 변경은 **새 테이블을 만들어 데이터를 옮기는** 재작성 절차가 필요하다(SQLite 문서의 12단계).
- DBeaver(`plugins/org.jkiss.dbeaver.ext.sqlite`, devel 브랜치 소스 확인):
  - 열 삭제·FK 생성/변경/삭제는 재작성으로 처리: `CREATE TEMPORARY TABLE temp AS SELECT … ` → `DROP TABLE` → 새 DDL → `INSERT … SELECT` → `DROP TABLE temp` (`SQLiteTableManager.addTableRecreateActions`).
  - 그 경로에서 `PRAGMA foreign_keys` 처리, 인덱스·트리거·뷰 재생성을 하는 코드는 읽은 파일에서 찾지 못함(미확정). FK가 켜진 DB에서 부모 테이블 `DROP`은 자식 행의 `ON DELETE CASCADE`를 일으킬 수 있다 — **데이터 손실 경로**.
  - 열 형식 변경 SQL은 상위 클래스에 있어 확인 못 함. SQLite 포럼에 "DBeaver로 열 형식·순서 변경이 여러 번 실패해 수동 SQL로 했다"는 사용자 보고가 있다.
  - 관련 이슈: dbeaver/dbeaver#9176(열 삭제 시 SQL 구문 오류), #18650(SQLite alter table 오류), #14351(기존 테이블에 FK 추가 불가).
- 결론: 성숙한 도구도 이 절차에서 오류가 반복된다. 이 플러그인은 1차에서 구조 편집을 넣지 않는다. 열 추가·이름 바꾸기처럼 SQLite가 직접 지원하는 것만 나중에 따로 검토할 수 있다.

### 2.8 주석(테이블 정의서 설명 칸)
- SQLite에는 `COMMENT ON`도, 열 정의 안의 `COMMENT '…'`도 없다. 주석을 저장하는 메타데이터 자체가 없다.
- 남는 것은 `CREATE TABLE` 안에 적은 `-- 설명` 주석뿐이다. SQLite는 이 글을 `sqlite_schema.sql`에 그대로 보존하므로 **파싱하면 읽을 수는 있다**. 다만 표준이 아니고(도구마다 다르게 씀), `ALTER TABLE ADD/DROP COLUMN`이 이 글을 고쳐 쓰며 주석이 바뀌거나 사라질 수 있다.
- DBeaver도 SQLite 주석 저장·편집을 하는 코드를 찾지 못함(일반 `COMMENT ON` 경로만 있음, 미확정).
- 결정(2026-11-16): `SupportsTableInfo = true`, 설명(COMMENTS) 칸은 언제나 빈 값. `--` 주석 파싱은 하지 않는다.

## 3. PGlite — 검토 후 제외

PGlite는 WASM으로 빌드한 Postgres라 JS(Node·브라우저) 안에서만 돈다. 붙이려면 사용자가 Node로 `@electric-sql/pglite-socket` 서버를 띄우고 PostgreSQL 접속으로 붙어야 한다. 제외 이유:
- 사용자 PC에 Node가 필요하다(또는 플러그인이 Node를 번들·실행 — 배포·프로세스 관리 부담).
- 기본 동시 연결 1개 — 성능 모니터가 별도 세션을 열면 막힌다. 멀티플렉서는 모든 경우를 다루지 않는다고 스스로 밝힘.
- Npgsql 호환이 검증되지 않았다.

다시 검토한다면: pglite-socket을 띄우고 기존 `PostgresIT`를 그대로 돌려 실패 목록부터 뽑는다.

## 4. 단계와 완료 조건

| 단계 | 내용 | 완료 조건(검증 가능) |
|---|---|---|
| S1 | `SqlLexRules.Sqlite` | 트리거 `BEGIN…END;`·`[ident]`·`` `ident` `` 분할/강조 시험 통과, 기존 Lex 시험 무변경 통과 |
| S2 | `SqliteProvider`(접속·검증·연결 문자열·오류 판정·DDL 규칙) + `DbProviders.All` 등록 | `SqliteProviderTests`: 파일 없음 검증 오류, ReadOnly에서 INSERT가 DB 오류로 실패, DDL 롤백 시 테이블이 사라짐, VACUUM이 커밋 대기 중 거부됨 |
| S3 | `IMetadataProvider` 구현 | 임시 파일 DB(테이블·뷰·인덱스·트리거·ATTACH)로 트리 그룹 개수·열·고유 키·소스 시험 |
| S4 | 비밀번호 흐름 `UsesPassword` | 비밀번호 없는 SQLite 프로필 연결 시 프롬프트가 안 뜸(헤드리스 UI 시험) |
| S5 | 접속 관리 화면 분기 + 파일 찾아보기 | `ConnectionManagerHeadlessTests`에 SQLite 칸 표시/숨김, `docs/controls/connection-manager.yml` 갱신, `check_controls_docs.py` 통과 |
| S6 | F4 정보 보기(편집 방언 null) / 결과 편집 키 | F4 창에 열·인덱스·제약이 보이고 편집 버튼이 비활성(헤드리스 시험), 혼합 형식 열·WITHOUT ROWID·rowid 테이블 편집 시험 |
| S6b | 테이블 정의서(`TableSpecCandidates` + `DescribeManyAsync`) | 임시 파일 DB로 정의서 모델 생성 시험: 테이블·뷰 목록, 열·형식·PK/FK, 설명 칸은 빈 값 |
| S7 | 도움말·README·plugin.json 버전 | `HelpTests` 통과 |

S1~S5만으로 "SQLite 파일 열고 조회·실행·트리 보기"가 된다. S6 이후는 선택.

## 5. 범위 밖(이번에 하지 않음)
- SQLite 구조 편집(ALTER 재작성 절차, 2.7), SQLCipher 암호화 DB, SQLite 성능 모니터.
- PGlite(3절).
- Folderss 파일 목록에서 `.db` 더블클릭으로 바로 열기(편하지만 요청에 없음 — 원하면 별도로).

## 9. 결정이 필요한 질문
1. ~~PGlite 방식~~ → 제외로 결정(2026-11-16).
2. ~~파일이 없을 때~~ → 만들지 묻기로 결정(2026-11-16, 2.1).
3. ~~테이블 정의서~~ → 켜되 설명 칸은 비워 둔다(2026-11-16, 2.8). `--` 주석 파싱은 하지 않는다.
4. ~~F4 범위~~ → 정보 보기만, 구조 편집 비활성으로 결정(2026-11-16, 2.7).
