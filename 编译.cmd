@echo off
rem Rebuild RayRadar.exe from source. ASCII only.
title Build RayRada
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
pause
