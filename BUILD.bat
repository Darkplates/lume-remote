@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-all.ps1" -Tests
if errorlevel 1 (
  echo Build failed. See the error above.
  pause
  exit /b 1
)
echo Build complete. Open LumeRemote.exe or START.bat.
pause
