# DB Helper

[Folderss](https://github.com/zaruous/Folderss) 파일 관리자 안에서 Oracle DB에 접속해 객체를 찾아보고 SQL을 실행하는 플러그인입니다.
`⋯ 메뉴 > 플러그인 > DB Helper`를 누르면 팝업 창으로 열립니다.

## 설치

1. Folderss `설정 > 플러그인 > GitHub에서 설치…`에 이 저장소 주소(`https://github.com/zaruous/Folderss-oracle-db-helper`)를 넣습니다.
   GitHub 설치 기능이 없는 Folderss라면 [Releases](https://github.com/zaruous/Folderss-oracle-db-helper/releases)에서 zip을 받아 `플러그인 찾기…`로 등록합니다.
2. `⋯ 메뉴 > 플러그인 > DB Helper`를 열고 `접속 관리…`에서 접속을 추가합니다(호스트·포트·서비스명·사용자·비밀번호).
3. 트리의 DB 앞 ▶를 누르거나 DB를 두 번 눌러 연결합니다.

## 기능

| 영역 | 내용 |
|---|---|
| 접속 관리 | 추가·복제·삭제·편집, 접속 테스트, 비밀번호는 DPAPI로 암호화해 저장(저장 안 하고 연결 때마다 입력도 가능), 읽기 전용, 색 표시(개발 초록 · 검증 노랑 · 운영 빨강) |
| DB 트리 | DB → 스키마(내 스키마 먼저, 내장 스키마는 숨김) → 테이블·뷰·시퀀스·프로시저/함수/패키지 → 열. 펼칠 때 불러오고, 처음 1,000개를 보인 뒤 [더 보기]로 더 불러옴 |
| 트리 검색 | 서버에서 스키마·객체(·열) 이름을 찾아 일치하는 계층만 보여 줌. Enter·Shift+Enter로 이동, Esc로 지우기 |
| 여러 DB | 여러 DB에 동시에 연결. SQL 탭마다 대상 DB를 정하고, 트리에서 다른 DB의 테이블을 두 번 누르면 그 DB용 탭으로 SELECT를 넣음 |
| SQL 실행 | 커서가 있는 문장 또는 선택 영역을 `Ctrl+Enter`로 실행, 취소, 결과는 처음 200행(1,000·5,000 선택)만 가져오고 [다음 행 가져오기]로 이어 읽기(다시 조회하지 않음) |
| 트랜잭션 | 자동 커밋 끔. DML은 커밋 전까지 "커밋 대기"로 표시하고 [커밋]·[롤백]으로 끝냄. 연결을 끊거나 창을 닫을 때 커밋 대기가 있으면 물어봄 |
| 안전장치 | WHERE 없는 UPDATE·DELETE, DROP·TRUNCATE·ALTER 등은 확인 체크 후 실행. 커밋 대기 중 DDL 실행(자동 커밋됨) 경고. 컴파일 오류가 있는 PL/SQL은 메시지 탭에 오류 목록 표시 |
| 결과 | NULL·숫자 구분, 열 머리에 Oracle 형식, TSV 복사(`Ctrl+C`·[복사]), 메시지·실행 기록 |

## 알아 둘 점

- **DB마다 세션 1개**: 같은 DB를 대상으로 하는 탭들은 트랜잭션을 함께 씁니다(한 탭의 커밋이 다른 탭의 변경도 커밋). 같은 DB에서는 한 번에 한 문장만 실행합니다.
- **읽기 전용은 플러그인 쪽 차단**입니다. 운영 DB는 DB 계정 권한도 읽기 전용으로 두세요.
- **비밀번호**는 이 PC의 이 Windows 사용자만 풀 수 있게 저장합니다. 다른 PC로 옮기면 다시 입력해야 합니다.
- **문장 구분**: `;`, 빈 줄, `/`만 있는 줄. PL/SQL 블록(BEGIN·DECLARE·CREATE PROCEDURE 등)은 `/` 줄로 끝내세요.
- **접속 방식**: 호스트·포트·서비스명(EZConnect)만 지원합니다. TNS 별칭과 Wallet(mTLS)은 아직 지원하지 않습니다.
- **확인 상태**: 문장 분석·조회 SQL·세션·트리 로직은 단위 테스트(900여 개, Linux·Windows)로, 접속·트리 조회·SQL 실행·트랜잭션·취소는 실제 Oracle 12.1(Docker)에 붙는 시험(`tests/MyPlugin.OracleIT`)으로 확인했습니다. Windows 화면은 아직 확인 전입니다.
- **취소**: [취소]는 in-band break로 보냅니다. 기본값(OOB, TCP 긴급 데이터)은 Docker Desktop·일부 방화벽·NAT를 지나지 못해 서버가 취소를 받지 못합니다.

### 실제 Oracle로 시험

`ORACLE_IT_DSN`이 없으면 모든 시험을 건너뜁니다(CI는 단위 테스트만 실행). SYSTEM 계정으로 시험 스키마(`DBH_IT`)를 만들고 끝나면 지웁니다.

```powershell
docker start oracle-12c            # 예: truevoly/oracle-12c (system/oracle, 서비스 xe)
$env:ORACLE_IT_DSN = "localhost:1521/xe"
$env:ORACLE_IT_SYS_PW = "oracle"   # 기본값 oracle
dotnet test tests/MyPlugin.OracleIT
```

측정값(연결·조회 시간, 사용자에게 보이는 오류 문장 등)은 `tests/MyPlugin.OracleIT/bin/Debug/net8.0/oracle-it-report.txt`에 남습니다.

---

# 플러그인 개발 안내

이 저장소는 Folderss 플러그인 템플릿에서 시작했습니다. 아래는 그 템플릿의 개발 안내입니다(프로젝트·클래스 이름은 아직 템플릿 이름 `MyPlugin`).

[Folderss](https://github.com/zaruous/Folderss) 파일 관리자의 플러그인을 바로 만들 수 있는 기본 틀입니다.
이 저장소를 복사해 이름만 바꾸고 `dotnet publish`하면, Folderss에 등록할 수 있는 플러그인 zip이 만들어집니다.

```
결과: src/MyPlugin/bin/Release/net8.0-windows/win-x64/MyPlugin.zip
├── plugin.json         ← 메뉴 이름, 진입점 정보
├── MyPlugin.dll        ← 플러그인 코드
├── MyPlugin.deps.json  ← 의존성 목록 (Folderss가 이걸로 DLL을 찾음)
└── (NuGet 패키지 DLL)  ← PackageReference로 추가한 라이브러리
```

Folderss에서 `⋯ 메뉴 > 플러그인 > My Plugin`을 누르면 플러그인 화면이 팝업 창으로 뜹니다.

---

## 요구 사항

| 항목 | 내용 |
|---|---|
| OS | Windows 10/11 (실행). 컴파일은 Linux·macOS에서도 됩니다 |
| SDK | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Folderss | 플러그인 기능이 들어간 버전 (계약 1.0.0) |
| IDE (선택) | Visual Studio 2022 또는 VS Code + C# Dev Kit |

---

## 빠른 시작 (5분)

### 1. 저장소 복사

GitHub에서 **Use this template**를 누르거나, 직접 복제합니다.

```powershell
git clone https://github.com/zaruous/Folderss-oracle-db-helper.git MyFolderssPlugin
cd MyFolderssPlugin
```

### 2. 이름 바꾸기

처음에는 **`plugin.json`의 `id`와 `name`만** 바꿔도 동작합니다.

```json
{
  "id": "yourname.my-plugin",
  "name": "My Plugin",
  ...
}
```

| 키 | 바꿀 값 | 규칙 |
|---|---|---|
| `id` | 예: `hong.order-form` | 영문·숫자로 시작, 영문·숫자·`.`·`_`·`-`만, 최대 64자. 다른 플러그인과 겹치면 안 되고, **배포한 뒤에는 바꾸지 마세요**(설정이 이어지지 않음) |
| `name` | 예: `주문서` | `⋯ 메뉴 > 플러그인`에 보이는 메뉴 이름이자 팝업 창 제목 |

프로젝트·클래스 이름까지 바꾸려면 [이름 전체 바꾸기](#이름-전체-바꾸기)를 보세요.

### 3. 빌드

```powershell
dotnet publish src/MyPlugin
```

마지막 줄 근처에 zip 경로가 나옵니다. `dotnet build`만 해서는 zip이 만들어지지 않습니다.

```
Folderss 플러그인: bin/Release/net8.0-windows/win-x64/MyPlugin.zip
```

### 4. Folderss에 등록하고 실행

1. Folderss에서 `⋯ 메뉴 > 설정 > 플러그인 > 플러그인 찾기…`를 누릅니다.
2. 3단계의 `MyPlugin.zip`을 고르고, 권한 경고를 확인한 뒤 `예`를 누릅니다.
3. `⋯ 메뉴 > 플러그인 > My Plugin`을 누르면 "My Plugin" 타이틀이 있는 팝업이 뜹니다.

### 5. 코드를 고친 뒤

1. `dotnet publish src/MyPlugin`
2. `플러그인 찾기…`로 같은 zip을 다시 등록합니다(같은 `id`라 교체됩니다).
3. **Folderss를 재시작합니다.** 한 번 로드한 플러그인 DLL은 종료할 때까지 내릴 수 없어서, 재시작해야 새 버전이 로드됩니다.

---

## 폴더 구조

```
.
├── FolderssPluginTemplate.sln
├── LICENSE                      ← MIT
├── .github/workflows/build.yml  ← 빌드·릴리스 자동화
├── src/
│   └── MyPlugin/
│       ├── MyPlugin.csproj      ← publish하면 zip까지 만든다 (PackPlugin 타깃)
│       ├── plugin.json          ← 플러그인 정보
│       └── MyPlugin.cs          ← 진입점 (IFolderssPlugin 구현)
└── contract/
    └── Folderss.PluginContract/ ← Folderss 계약(인터페이스) 사본. 컴파일용이며 zip에 넣지 않음
        ├── Folderss.PluginContract.csproj
        └── PluginContracts.cs
```

**여러분이 고칠 곳은 `src/MyPlugin/`뿐입니다.** `contract/`는 Folderss가 계약을 갱신할 때만 교체합니다([계약 사본 관리](#계약-사본-관리)).

---

## 플러그인이 동작하는 방식

| 시점 | 일어나는 일 |
|---|---|
| 등록 | Folderss가 `plugin.json`을 검사하고 zip을 `%LOCALAPPDATA%\Folderss\plugins\<id>.zip`으로 복사합니다. 코드는 실행되지 않습니다 |
| `⋯ 메뉴 > 플러그인` 열기 | `plugin.json`의 `name`으로 메뉴를 만듭니다. 코드는 실행되지 않습니다 |
| **처음 실행** | zip 해제 → DLL 로드 → `Initialize(manager)` → `CreateView()` → 팝업 |
| 다시 실행 | `CreateView()` → 새 팝업 (창이 하나 더 뜹니다) |

```csharp
public sealed class MyPlugin : IFolderssPlugin
{
    public void Initialize(IPluginManager manager) { ... }   // 처음 한 번
    public FrameworkElement CreateView() { ... }             // 실행할 때마다, 매번 새 요소를 돌려줌
}
```

팝업 창은 Folderss가 만듭니다. 제목은 `name`, 크기는 900×600이고 비모달입니다. 배경·글자색·글꼴은 현재 테마를 따릅니다.

---

## 자주 쓰는 기능

`Initialize`에서 받은 `IPluginManager`로 본체 기능을 씁니다.

### 플러그인 설정 저장·읽기

```csharp
_manager.SetSetting("lastOrderNo", "A-0001");      // 바로 파일에 저장. 실패하면 예외
var last = _manager.GetSetting("lastOrderNo");      // 없으면 null
```

저장 위치는 `%LOCALAPPDATA%\Folderss\plugin-data\<id>\settings.json`입니다. 파일을 직접 쓰려면 `_manager.DataDirectory` 폴더를 쓰세요.

### 본체 설정 읽기 (읽기 전용)

```csharp
string theme;
_manager.GetAppSettings().TryGetValue("theme", out theme);   // "Black", "Light", ...
```

키 목록: `theme`, `git.*`, `diff.*`, `console.*`. 전체 목록은 `contract/Folderss.PluginContract/PluginContracts.cs`의 `GetAppSettings` 주석에 있습니다. 다른 플러그인의 설정은 들어 있지 않습니다.

### 폴더 패널 넣기

```csharp
var panel = _manager.CreateFolderPanel(@"C:\work");
root.Children.Add(panel.View);
panel.PathChanged += (s, e) => title.Text = panel.CurrentPath;
```

파일을 더블클릭하면 Folderss 메인 창의 뷰어 탭에서 열립니다.

### 설정 창에 탭 추가

`src/MyPlugin/MySettingsPage.cs`를 새로 만듭니다.

```csharp
using Folderss.Plugins;
using System.Windows;
using System.Windows.Controls;

namespace MyPlugin
{
    public sealed class MySettingsPage : IPluginSettingsPage
    {
        private readonly IPluginManager _manager;
        private TextBox _greeting;

        public MySettingsPage(IPluginManager manager) { _manager = manager; }

        public string Title { get { return "My Plugin"; } }

        public FrameworkElement CreateView()          // 설정 창을 열 때마다 새로 만든다
        {
            _greeting = new TextBox { Text = _manager.GetSetting("greeting") ?? "" };
            return _greeting;
        }

        public void Save()                            // 설정 창 [저장]을 누를 때
        {
            _manager.SetSetting("greeting", _greeting.Text);
        }
    }
}
```

그런 다음 두 곳을 고칩니다.
- `MyPlugin.cs`의 `Initialize`에 있는 `manager.AddSettingsPage(...)` 주석을 풉니다.
- `plugin.json`의 `"hasSettings"`를 `true`로 바꿉니다. 그러면 플러그인을 실행하기 전에도 설정 창에 안내 탭이 보입니다.

설정 탭은 **플러그인을 한 번 실행한 뒤** 설정 창을 열어야 나타납니다. 플러그인은 사용자가 메뉴에서 실행할 때만 로드되기 때문입니다.

### NuGet 패키지 쓰기

`PackageReference`만 추가하면 됩니다. publish할 때 패키지 DLL과 `.deps.json`이 zip에 함께 들어갑니다.

```powershell
dotnet add src/MyPlugin package Oracle.ManagedDataAccess.Core
```

- 플러그인마다 따로 로드되므로, Folderss나 다른 플러그인이 쓰는 같은 라이브러리의 버전과 관계없이 동작합니다.
- 패키지는 **.NET 8(`net8.0`) 이하를 지원**해야 합니다. Folderss가 .NET 8에서 실행되기 때문입니다.
- `csproj`의 `RuntimeIdentifier`(`win-x64`)는 지우지 마세요. 패키지의 Windows용 DLL(`runtimes/win/...`)이 이 설정으로 골라집니다.
- 패키지의 형식(예: `OracleConnection`)은 플러그인 안에서만 쓰세요. 다른 플러그인과 같은 형식으로 취급되지 않습니다.

### 테마 색 맞추기

색을 고정값으로 넣지 말고 Folderss 테마 리소스 키에 연결하세요.

```csharp
grid.SetResourceReference(Control.BackgroundProperty, "PanelBackground");
grid.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
```

| 용도 | 키 |
|---|---|
| 배경 | `WindowBackground`, `PanelBackground`, `SurfaceBackground`, `ControlBackground` |
| 글자 | `PrimaryText`, `SecondaryText`, `DisabledTextBrush` |
| 테두리·강조·선택 | `BorderBrush`, `AccentBrush`, `AccentHoverBrush`, `SelectionBrush`, `RowHoverBrush` |
| 마우스 상태 | `ControlHoverBrush`, `ControlPressedBrush` |
| 글꼴 | `AppFontFamily` |

`DataGrid`는 Folderss 테마에 스타일이 없어서, 연결하지 않으면 어두운 테마에서도 흰 배경으로 나옵니다.

---

## 꼭 지켜야 할 것

플러그인은 Folderss와 **같은 프로세스**에서 실행됩니다. 플러그인이 잘못하면 Folderss 전체가 종료될 수 있습니다.

| 하지 마세요 | 이유 / 대신 |
|---|---|
| `Task.Run`, `Thread`, 타이머 안에서 예외를 던진 채 두기 | **Folderss가 종료됩니다.** 반드시 `try/catch`로 감싸세요. `async void` 이벤트 처리기도 마찬가지입니다 |
| `Application.Current.Shutdown()`, `Environment.Exit()` | Folderss가 종료됩니다(막을 수 없음). 원인은 로그에 남고, 다음 시작 때 사용자에게 알려집니다 |
| `CreateView()`에서 같은 요소를 다시 돌려주기 | WPF 예외가 납니다. 매번 새로 만드세요 |
| `PluginDirectory`(압축을 푼 폴더)에 파일 쓰기 | zip을 교체하면 새 폴더에 풀려서 쓴 파일이 사라집니다. 쓰기는 `DataDirectory`에 하세요 |
| Folderss 본체 DLL(`Folderss.exe`) 참조 | 본체가 바뀌면 깨집니다. `Folderss.Plugins` 인터페이스만 쓰세요 |

UI 스레드에서 난 예외와 `Initialize`/`CreateView`의 예외는 Folderss가 잡아서 메시지로 보여 주고 계속 실행합니다.
팝업을 코드로 닫으려면 `Window.GetWindow(view)?.Close()`를 쓰세요.

플러그인은 사용자 권한으로 파일을 읽고 쓰고 프로그램을 실행할 수 있습니다. 사용자는 등록할 때 이 경고를 봅니다. 필요한 일만 하도록 만들어 주세요.

---

## 이름 전체 바꾸기

`MyPlugin` 대신 원하는 이름(예: `OrderForm`)을 쓰려면 아래를 모두 바꿉니다. **하나라도 어긋나면 실행할 때 "플러그인을 열지 못했습니다"가 뜹니다.**

| 바꿀 곳 | 예 |
|---|---|
| 폴더 `src/MyPlugin/` → `src/OrderForm/` | |
| 파일 `MyPlugin.csproj` → `OrderForm.csproj` | 어셈블리 이름(DLL·zip 이름)이 이 파일 이름을 따릅니다 |
| `FolderssPluginTemplate.sln`의 프로젝트 경로 | `dotnet sln remove src/MyPlugin/MyPlugin.csproj` 후 `dotnet sln add src/OrderForm/OrderForm.csproj` |
| `MyPlugin.cs`의 `namespace MyPlugin`, `class MyPlugin` | `namespace OrderForm`, `class OrderFormPlugin` |
| `plugin.json`의 `"assembly"` | `"OrderForm.dll"` |
| `plugin.json`의 `"type"` | `"OrderForm.OrderFormPlugin"` (네임스페이스.클래스) |

---

## 계약 사본 관리

`contract/Folderss.PluginContract/PluginContracts.cs`는 Folderss 저장소의
[`Folderss.PluginContract/PluginContracts.cs`](https://github.com/zaruous/Folderss/blob/master/Folderss.PluginContract/PluginContracts.cs) 사본입니다.

- **컴파일용**입니다. 실행할 때는 Folderss에 들어 있는 같은 이름의 DLL이 쓰이므로 zip에 넣지 않습니다(`Private="false"`).
- 어셈블리 이름(`Folderss.PluginContract`), 네임스페이스(`Folderss.Plugins`), 형식 이름은 **절대 바꾸지 마세요.** 바꾸면 Folderss가 플러그인을 인식하지 못합니다.
- **Folderss가 계약을 갱신하면** 이 파일을 원본으로 교체하고 `.csproj`의 `<Version>`도 맞추세요.
  - 사본이 사용자의 Folderss보다 **새 버전**이면, 새 기능을 쓰는 플러그인은 구버전 Folderss에서 로드되지 않거나 `MissingMethodException`이 납니다.
  - 사본이 **구버전**이면 문제없습니다. 새 기능만 못 쓸 뿐입니다.

사본 대신 설치된 Folderss의 DLL을 직접 참조해도 됩니다. `MyPlugin.csproj`의 `ProjectReference`를 아래로 바꾸세요(경로는 설치 위치에 맞게).

```xml
<ItemGroup>
  <Reference Include="Folderss.PluginContract">
    <HintPath>C:\Program Files\Folderss\Folderss.PluginContract.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

---

## 릴리스 (GitHub Actions)

`.github/workflows/build.yml`이 빌드와 배포를 자동으로 합니다.

| 언제 | 하는 일 |
|---|---|
| `main` 푸시, PR | publish하고 플러그인 zip을 Actions 실행 결과의 **Artifacts**에 올린다 (확인용) |
| `v*` 태그 푸시 | `plugin.json`의 `version`과 태그가 같은지 확인한 뒤, **GitHub 릴리스**를 만들고 `<id>-<version>.zip`을 첨부한다 |

릴리스 순서:

```powershell
# 1. plugin.json의 "version"을 올린다 (예: 1.1.0) → 커밋·푸시
# 2. 같은 버전으로 태그
git tag v1.1.0
git push origin v1.1.0
```

- 태그와 `version`이 다르면 빌드가 실패하고 릴리스는 만들어지지 않습니다. `plugin.json`을 고친 뒤 태그를 다시 붙이세요.
- 플러그인 폴더 이름을 바꿔도(`src/<이름>/`) 그대로 동작합니다. 다만 `src` 아래 `plugin.json`은 하나만 있어야 합니다.
- 빌드는 Ubuntu 러너에서 합니다. WPF 플러그인이지만 `EnableWindowsTargeting` 설정 덕분에 컴파일은 Linux에서도 됩니다. 실행은 Windows에서만 됩니다.

---

## 디버깅

1. `dotnet publish src/MyPlugin -c Debug`로 만든 zip(`bin/Debug/net8.0-windows/win-x64/MyPlugin.zip`)을 등록합니다.
2. Visual Studio에서 **디버그 > 프로세스에 연결 > `Folderss.exe`** 를 고릅니다.
3. `⋯ 메뉴 > 플러그인`에서 실행하면 중단점에 걸립니다. 심볼이 안 잡히면 `MyPlugin.csproj`의 `PluginFiles`에서 `.pdb`를 빼는 조건을 지우세요.

로그와 파일 위치(`%LOCALAPPDATA%\Folderss\` 기준):

| 경로 | 내용 |
|---|---|
| `plugin-log.txt` | 플러그인 오류, 비정상 종료 기록 |
| `plugins\<id>.zip` | 등록된 플러그인 |
| `plugins\extracted\<id>-<해시>\` | 압축을 푼 폴더 |
| `plugin-data\<id>\` | 플러그인 설정·데이터 |

---

## 문제 해결

| 증상 | 확인할 것 |
|---|---|
| 등록할 때 "플러그인 파일이 아닙니다" | zip 루트에 `plugin.json`이 있는지, JSON 형식, `assembly`의 DLL이 zip에 있는지 |
| "플러그인을 열지 못했습니다 … TypeLoadException" | `plugin.json`의 `type`이 `네임스페이스.클래스`와 정확히 같은지 |
| "… IFolderssPlugin을 구현하지 않습니다" | 클래스가 `IFolderssPlugin`을 구현하는지, 계약 사본의 어셈블리 이름·버전을 바꾸지 않았는지 |
| 메뉴에 플러그인이 안 보임 | 설정 > 플러그인 목록. "읽지 못한 zip n개"가 있으면 툴팁에 이유가 있음 |
| 고친 코드가 반영되지 않음 | zip을 다시 등록하고 **Folderss를 재시작**했는지 |
| 어두운 테마에서 일부 컨트롤만 흰색 | 테마 리소스 키를 연결했는지 ([테마 색 맞추기](#테마-색-맞추기)) |

---

## 더 읽을거리

- [Folderss 플러그인 개발 가이드](https://github.com/zaruous/Folderss/blob/master/docs/plugin-development.md): plugin.json 전체, API 표, 오류 처리 범위
- [튜토리얼: 주문서 플러그인 만들기](https://github.com/zaruous/Folderss/blob/master/docs/plugin-tutorial-order-form.md): 타이틀 + 그리드 화면을 단계별로
- [예제: HelloPlugin](https://github.com/zaruous/Folderss/tree/master/samples/HelloPlugin): 폴더 패널, 본체 설정, 설정 탭

---

## 라이선스

[MIT](LICENSE)

이 템플릿으로 만든 플러그인은 원하는 라이선스로 배포해도 됩니다. 새 저장소를 만들면 `LICENSE`의 저작권자(`Copyright (c) 2026 zaruous`)를 본인으로 바꾸거나, 다른 라이선스로 교체하세요.
