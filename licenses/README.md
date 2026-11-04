# 서드파티 라이선스

DB Helper 플러그인 zip(`zaruous.folderss-db-helper-<버전>.zip`)에 함께 들어가 배포되는 외부 라이브러리와 그 라이선스입니다.
이 플러그인 자체의 라이선스는 저장소 루트의 [`LICENSE`](../LICENSE)(MIT)입니다(플러그인 zip에서는 루트의 `LICENSE`).

버전은 `src/MyPlugin/MyPlugin.csproj`의 PackageReference와 전이 패키지(`dotnet list src/MyPlugin package --include-transitive`) 기준입니다.
시험 프로젝트(`tests/`)·미리보기 도구(`tools/`)에만 쓰는 패키지(xunit, Microsoft.NET.Test.Sdk 등)는 배포되지 않으므로 넣지 않았습니다.
.NET 런타임·WPF는 Folderss 본체의 것을 쓰므로 zip에 들어가지 않습니다.

## 직접 참조

| 패키지 | 버전 | 라이선스 | 용도 | 전문 |
|---|---|---|---|---|
| AvalonEdit | 6.3.0.90 | MIT | SQL 편집기 | [AvalonEdit.LICENSE.txt](AvalonEdit.LICENSE.txt) |
| ClosedXML | 0.105.1 | MIT | 엑셀 내보내기·가져오기·서식 | [ClosedXML.LICENSE.txt](ClosedXML.LICENSE.txt) |
| Microsoft.Data.SqlClient | 7.1.1 | MIT | SQL Server 접속 | [Microsoft.Data.SqlClient.LICENSE.txt](Microsoft.Data.SqlClient.LICENSE.txt) |
| Microsoft.Data.Sqlite | 8.0.10 | MIT | 성능 모니터 이력 저장(history.db) | [Microsoft.Data.Sqlite.LICENSE.txt](Microsoft.Data.Sqlite.LICENSE.txt) |
| Npgsql | 10.0.3 | PostgreSQL License | PostgreSQL 접속 | [Npgsql.LICENSE.txt](Npgsql.LICENSE.txt) |
| Oracle.ManagedDataAccess.Core | 23.26.301 | Oracle Free Distribution, Hosting, and Use Terms | Oracle 접속 | [Oracle.ManagedDataAccess.Core.LICENSE.txt](Oracle.ManagedDataAccess.Core.LICENSE.txt) |

## 전이 참조

| 패키지 | 버전 | 라이선스 | 전문 |
|---|---|---|---|
| ClosedXML.Parser | 2.0.0 | MIT | [ClosedXML.Parser.LICENSE.txt](ClosedXML.Parser.LICENSE.txt) |
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework | 3.1.1 | MIT | [DocumentFormat.OpenXml.LICENSE.txt](DocumentFormat.OpenXml.LICENSE.txt) |
| ExcelNumberFormat | 1.1.0 | MIT | [ExcelNumberFormat.LICENSE.txt](ExcelNumberFormat.LICENSE.txt) |
| RBush.Signed | 4.0.0 | MIT | [RBush.Signed.LICENSE.txt](RBush.Signed.LICENSE.txt) |
| SixLabors.Fonts | 1.0.0 | Apache-2.0 | [SixLabors.Fonts.LICENSE.txt](SixLabors.Fonts.LICENSE.txt) |
| Microsoft.Data.SqlClient.SNI.runtime | 7.1.0 | Microsoft Software License Terms (재배포 가능 코드) | [Microsoft.Data.SqlClient.SNI.LICENSE.txt](Microsoft.Data.SqlClient.SNI.LICENSE.txt) |
| Microsoft.Data.SqlClient.Extensions.Abstractions, Microsoft.Data.SqlClient.Internal.Logging | 7.1.1 | MIT | [Microsoft.Data.SqlClient.LICENSE.txt](Microsoft.Data.SqlClient.LICENSE.txt) |
| Microsoft.SqlServer.Server | 1.0.0 | MIT | [Microsoft.Data.SqlClient.LICENSE.txt](Microsoft.Data.SqlClient.LICENSE.txt) |
| Microsoft.Data.Sqlite.Core | 8.0.10 | MIT | [Microsoft.Data.Sqlite.LICENSE.txt](Microsoft.Data.Sqlite.LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .lib.e_sqlite3, .provider.e_sqlite3 | 2.1.6 | Apache-2.0 | [SQLitePCL.raw.LICENSE.txt](SQLitePCL.raw.LICENSE.txt) |
| SQLite (e_sqlite3 네이티브, SQLitePCLRaw.lib.e_sqlite3에 포함) | — | Public Domain | [SQLite.PUBLIC-DOMAIN.txt](SQLite.PUBLIC-DOMAIN.txt) |
| Microsoft.IdentityModel.Abstractions, .JsonWebTokens, .Logging, .Protocols, .Protocols.OpenIdConnect, .Tokens, System.IdentityModel.Tokens.Jwt | 8.16.0 | MIT | [Microsoft.IdentityModel.LICENSE.txt](Microsoft.IdentityModel.LICENSE.txt) |
| Microsoft.Bcl.Cryptography | 8.0.0 | MIT | [dotnet-runtime.LICENSE.txt](dotnet-runtime.LICENSE.txt) |
| Microsoft.Extensions.Caching.Abstractions 8.0.0, Caching.Memory 8.0.1, DependencyInjection.Abstractions 8.0.2, Logging.Abstractions 8.0.2, Options 8.0.2, Primitives 8.0.0 | | MIT | [dotnet-runtime.LICENSE.txt](dotnet-runtime.LICENSE.txt) |
| System.Collections.Immutable 10.0.3, System.Configuration.ConfigurationManager 8.0.1, System.Diagnostics.DiagnosticSource 9.0.11, System.Diagnostics.EventLog 8.0.1, System.Diagnostics.PerformanceCounter 8.0.0, System.DirectoryServices.Protocols 8.0.2, System.Formats.Asn1 8.0.1, System.IO.Packaging 8.0.1, System.Security.Cryptography.Pkcs 8.0.1, System.Security.Cryptography.ProtectedData 8.0.0, System.Threading.RateLimiting 8.0.0 | | MIT | [dotnet-runtime.LICENSE.txt](dotnet-runtime.LICENSE.txt) |
| System.Memory | 4.6.3 | MIT | [dotnet-maintenance-packages.LICENSE.txt](dotnet-maintenance-packages.LICENSE.txt) |

dotnet/runtime 패키지가 함께 배포하는 서드파티 고지는 [dotnet-runtime.THIRD-PARTY-NOTICES.txt](dotnet-runtime.THIRD-PARTY-NOTICES.txt)에 있습니다.

## 배포할 때 지켜야 할 조건

- **Oracle.ManagedDataAccess.Core**: 배포물에 이 라이선스 사본을 넣어야 하고, 배포·사용에 별도 요금을 받을 수 없으며(가치를 더한 유료 제품의 일부로는 가능), 수정하지 않은 상태로만 재배포할 수 있습니다. 리버스 엔지니어링 금지, 미국 수출 규정 준수.
- **Microsoft.Data.SqlClient.SNI**: 애플리케이션의 일부로만 배포(단독 배포 불가)하고, 배포받는 쪽도 이 조건만큼 보호하는 약관에 동의하게 해야 합니다.
- **MIT·Apache-2.0·PostgreSQL License**: 저작권 고지와 라이선스 전문을 함께 배포합니다.

## 갱신 방법

패키지를 추가·변경하면 `dotnet list src/MyPlugin package --include-transitive`로 목록을 확인하고, 각 패키지의 `.nuspec`(`%USERPROFILE%\.nuget\packages\<이름>\<버전>`)에 적힌 라이선스를 보고 이 표와 전문 파일을 맞춥니다.
