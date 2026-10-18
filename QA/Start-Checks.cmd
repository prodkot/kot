@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Checks.ps1"
echo.
pause
