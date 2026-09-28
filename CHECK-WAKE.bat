@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\wake-readiness.ps1"
if errorlevel 1 echo Some wake diagnostics were unavailable. No settings were changed.
pause
