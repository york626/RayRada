@echo off
rem ASCII only. Prefer PowerShell 7 (pwsh); fall back to Windows PowerShell if absent.
setlocal
set "PS="
where pwsh.exe >nul 2>nul && set "PS=pwsh.exe"
if not defined PS if exist "%USERPROFILE%\pwsh7\pwsh.exe" set "PS=%USERPROFILE%\pwsh7\pwsh.exe"
if not defined PS if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined PS set "PS=powershell.exe"
cd /d "%~dp0"
echo [build] PowerShell: %PS%
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
pause
endlocal
