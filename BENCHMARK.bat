@echo off
setlocal
cd /d "%~dp0"
echo Lume comparison toolkit - local process counters only
echo Start your remote session before collecting. Nothing is uploaded.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\measure-resources.ps1" %*
set "benchmark_exit=%errorlevel%"
if errorlevel 1 (
  echo Collection did not finish successfully. Read the error above.
) else (
  echo Collection finished. Review the local report before sharing it.
)
pause
exit /b %benchmark_exit%
