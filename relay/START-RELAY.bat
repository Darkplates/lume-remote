@echo off
setlocal
cd /d "%~dp0"
where py.exe >nul 2>nul
if errorlevel 1 (
  echo Python 3.10 or later is required to run the optional relay.
  echo The Lume Windows application itself does not need Python.
  pause
  exit /b 1
)
echo This starts the relay on all IPv4 network interfaces, TCP port 24817.
echo No firewall rules or router settings are changed.
echo Use only a machine you intend to operate as a relay.
echo Press Ctrl+C to stop it.
py -3 "%~dp0relay.py" --bind 0.0.0.0 --port 24817
if errorlevel 1 echo Relay stopped with an error.
pause
