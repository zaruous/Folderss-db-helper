<#
  헤드리스 UI 시험: 플러그인만(Folderss 없이) 숨은 데스크톱에서 띄워 tests/MyPlugin.UiTests를 돌린다.
  - 숨은 데스크톱은 CreateDesktop으로 만들고 사용자 화면은 바꾸지 않는다(SwitchDesktop 안 함) — 창·마우스·포커스를 건드리지 않는다.
  - 도는 동안 사용자 데스크톱(Default)에 시험 프로세스(testhost)의 창이 하나라도 뜨는지 감시해, 뜨면 실패로 알린다.
  - 실제 Oracle 시험은 FMON_UI_ORACLE(host:port/service)·FMON_UI_ORACLE_USER·FMON_UI_ORACLE_PW가 있을 때만.
    그 시험에는 [Trait("Requires", "Oracle")]이 붙어 있어 -Filter "Requires!=Oracle" / "Requires=Oracle"로 나눌 수 있다.
  - 결과는 <Work>\results\ui-tests-<시각>.trx로 남기고, 끝에 실행·통과·실패·건너뜀 수와 건너뛴 시험·이유를 보인다.
    GITHUB_STEP_SUMMARY가 있으면(GitHub Actions) 같은 요약을 단계 요약에도 쓴다.

  종료 코드: 0 성공 / dotnet test의 코드(시험 실패) / 2 사용자 화면에 시험 창이 뜸 / 3 실제로 돈 시험이 0개(모두 건너뜀·필터 불일치)
            / 4 -FailOnSkip인데 건너뛴 시험이 있음 / 5 trx 결과를 찾지 못함

  사용: pwsh scripts/ui-test.ps1 [-Filter <dotnet test 필터>] [-NoPublish] [-Work <폴더>] [-FailOnSkip]
#>
param(
    [string]$Filter = '',
    [switch]$NoPublish,
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) 'folderss-uitest'),
    # 건너뛴 시험이 하나라도 있으면 실패(CI: 그 단계에서 돌아야 할 시험이 환경 탓에 빠지는 것을 잡는다)
    [switch]$FailOnSkip,
    # 요약 제목(CI 단계 요약에서 단계를 구분)
    [string]$Title = 'UI 시험'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'tests\MyPlugin.UiTests'

if (-not $NoPublish) {
    dotnet publish (Join-Path $repo 'src\MyPlugin') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '플러그인 publish 실패' }
}
dotnet build $project -c Debug --nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'UI 시험 프로젝트 빌드 실패' }

New-Item -ItemType Directory -Force $Work | Out-Null
$env:FMON_UI_WORK = $Work
$env:FMON_UI_PLUGIN_ZIP = Join-Path $repo 'src\MyPlugin\bin\Release\net8.0-windows\win-x64\MyPlugin.zip'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class HiddenDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    /// 숨은 데스크톱에서 명령을 실행하고 끝날 때까지 기다린다. 그동안 사용자 데스크톱(이 스크립트가 있는 곳)에 testhost 창이 보이면 leaks에 남긴다.
    public static int Run(string desktopName, string commandLine, string directory, List<string> leaks)
    {
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("CreateDesktop 실패: " + Marshal.GetLastWin32Error());
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = desktopName };
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, directory, ref si, out pi))
                throw new InvalidOperationException("CreateProcess 실패: " + Marshal.GetLastWin32Error());
            try
            {
                while (WaitForSingleObject(pi.hProcess, 500) != 0)
                {
                    EnumWindows((h, l) =>
                    {
                        if (!IsWindowVisible(h)) return true;
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        try
                        {
                            var name = Process.GetProcessById((int)pid).ProcessName;
                            if (name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
                            {
                                var text = new StringBuilder(256); GetWindowTextW(h, text, 256);
                                var entry = name + ":" + text;
                                if (!leaks.Contains(entry)) leaks.Add(entry);
                            }
                        }
                        catch (ArgumentException) { }
                        return true;
                    }, IntPtr.Zero);
                }
                uint code; GetExitCodeProcess(pi.hProcess, out code);
                return (int)code;
            }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }
        finally { CloseDesktop(desktop); }
    }
}
'@

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $Work "ui-test-$stamp.log"
$results = Join-Path $Work 'results'
$trx = Join-Path $results "ui-tests-$stamp.trx"
New-Item -ItemType Directory -Force $results | Out-Null
$filterArg = if ($Filter) { " --filter `"$Filter`"" } else { '' }
$cmd = "cmd.exe /c dotnet test `"$project`" -c Debug --no-build --nologo --logger `"console;verbosity=normal`" --logger `"trx;LogFileName=$trx`" --results-directory `"$results`"$filterArg > `"$log`" 2>&1"
$leaks = New-Object 'System.Collections.Generic.List[string]'
$desktopName = 'folderss-uitest-' + $PID
Write-Host "숨은 데스크톱 '$desktopName'에서 실행 — 기록: $log"
$code = [HiddenDesktop]::Run($desktopName, $cmd, $repo, $leaks)
Get-Content $log | Select-String -Pattern '통과|실패|건너뜀|Passed|Failed|Skipped|error|오류|캡처|\[' | ForEach-Object { $_.Line }

# trx에서 결과 세기 — "모두 건너뜀"이나 "0개 실행"을 성공으로 보이지 않게 한다
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("### $Title")
$verdict = $code
if (-not (Test-Path $trx)) {
    $summary.Add("- :x: trx 결과가 없습니다($trx) — dotnet test가 시험을 시작하지 못했습니다. 기록: ``$log``")
    if ($verdict -eq 0) { $verdict = 5 }
}
else {
    [xml]$doc = Get-Content $trx -Raw -Encoding UTF8
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $rows = @(Select-Xml -Xml $doc -XPath '//t:UnitTestResult' -Namespace $ns | ForEach-Object { $_.Node })
    $passed = @($rows | Where-Object outcome -eq 'Passed')
    $failed = @($rows | Where-Object outcome -eq 'Failed')
    $skipped = @($rows | Where-Object { $_.outcome -ne 'Passed' -and $_.outcome -ne 'Failed' })
    $line = "전체 $($rows.Count) · 통과 $($passed.Count) · 실패 $($failed.Count) · 건너뜀 $($skipped.Count)" + $(if ($Filter) { " (필터: $Filter)" } else { '' })
    Write-Host $line
    $summary.Add("- $line")
    foreach ($r in $failed) { $summary.Add("- :x: 실패: ``$($r.testName)``") }
    foreach ($r in $skipped) {
        # xUnit의 Skip 이유는 Output/StdOut 또는 TextMessages에 남는다
        $reason = (@($r.Output.StdOut, $r.Output.ErrorInfo.Message) + @($r.Output.TextMessages.Message) | Where-Object { $_ } | Select-Object -First 1)
        $text = "건너뜀: $($r.testName)" + $(if ($reason) { " — $("$reason".Trim())" } else { '' })
        Write-Host $text -ForegroundColor Yellow
        $summary.Add("- :warning: $text")
    }
    $executed = $passed.Count + $failed.Count
    if ($executed -eq 0) {
        Write-Host "실제로 돈 시험이 없습니다(모두 건너뜀 또는 필터에 맞는 시험 없음) — 실패로 처리합니다." -ForegroundColor Red
        $summary.Add("- :x: 실제로 돈 시험이 0개 — 성공으로 보지 않습니다.")
        if ($verdict -eq 0) { $verdict = 3 }
    }
    elseif ($FailOnSkip -and $skipped.Count -gt 0) {
        Write-Host "-FailOnSkip: 건너뛴 시험 $($skipped.Count)개 — 실패로 처리합니다." -ForegroundColor Red
        $summary.Add("- :x: 이 단계에서는 건너뛴 시험이 없어야 합니다(-FailOnSkip).")
        if ($verdict -eq 0) { $verdict = 4 }
    }
}
if ($leaks.Count -gt 0) {
    Write-Host ("사용자 화면에 시험 창이 보였습니다: " + ($leaks -join ', ')) -ForegroundColor Red
    $summary.Add("- :x: 사용자 화면(Default 데스크톱)에 시험 창이 보였습니다: " + ($leaks -join ', '))
    $verdict = 2
}
else {
    Write-Host "사용자 화면에 뜬 시험 창: 없음"
    $summary.Add("- 사용자 화면에 뜬 시험 창: 없음")
}
if ($env:GITHUB_STEP_SUMMARY) { ($summary -join "`n") + "`n" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
exit $verdict
