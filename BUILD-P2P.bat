@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-peer.ps1"
if errorlevel 1 (
  echo WebRTC build failed. Read the error above. The prebuilt DLL can still be used.
  pause
  exit /b 1
)
echo WebRTC native library built successfully.
pause
