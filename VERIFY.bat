@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify.ps1"
if errorlevel 1 (
  echo Verification failed. See the report above.
  pause
  exit /b 1
)
echo Verification completed. See the reported scope and any skipped checks.
pause
