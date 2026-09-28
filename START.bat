@echo off
setlocal
cd /d "%~dp0"
for %%F in (datachannel.dll LumeCapture.dll LumeVideo.dll) do (
  if not exist "%~dp0%%F" (
    echo Missing %%F. Extract the whole release ZIP, or run BUILD.bat from a source checkout.
    pause
    exit /b 1
  )
)
if not exist "%~dp0LumeRemote.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
  if errorlevel 1 (
    echo Lume could not be built. Windows 10 or 11 with .NET Framework 4.8 is required.
    pause
    exit /b 1
  )
)
start "" "%~dp0LumeRemote.exe"
if errorlevel 1 (
  echo Lume could not start. Run BUILD.bat to check prerequisites.
  pause
  exit /b 1
)
