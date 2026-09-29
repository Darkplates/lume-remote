@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\render-ui-dpi.ps1"
if errorlevel 1 (
  echo UI scale check failed. See the report above.
  pause
  exit /b 1
)
echo Repeat after signing in at 100, 125, 150 and 200 percent display scale.
pause
