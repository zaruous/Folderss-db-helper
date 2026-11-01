@echo off
rem 플러그인 미리보기 호스트 실행: publish -> tools\PluginPreview 빌드 -> 창 띄우기 (scripts\preview.ps1).
rem 사용: scripts\host.bat [-Theme Black^|Light^|GitHub^|Nord^|Solarized^|Dracula^|Catppuccin] [-NoPublish] [-Fresh]
setlocal
chcp 65001 >nul
set "PS=powershell.exe"
where pwsh.exe >nul 2>&1 && set "PS=pwsh.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '%~dp0preview.ps1' %*; exit $LASTEXITCODE"
if errorlevel 1 (
    echo.
    echo 호스트 실행 실패 - 위 오류를 확인하세요.
    pause
    exit /b 1
)
endlocal
