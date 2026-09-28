@echo off
setlocal
cd /d "%~dp0"
if not exist "LumeRemote.exe" (
  echo Extract the complete release ZIP before updating the installed host.
  pause
  exit /b 1
)
if not exist "SHA256SUMS.txt" (
  echo This folder does not contain a release manifest. Use a packaged release.
  pause
  exit /b 1
)
echo Updating the installed Lume host. Windows will request administrator permission.
echo The current remote session will disconnect briefly. Saved pairings will be kept.
start "" /wait "LumeRemote.exe" --request-host-update
if errorlevel 1 (
  echo Update did not finish. See the Windows message and setup.log in this folder.
  pause
  exit /b 1
)
start "" "LumeRemote.exe"
