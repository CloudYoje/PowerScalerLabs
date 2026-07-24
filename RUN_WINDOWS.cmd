@echo off
setlocal
cd /d "%~dp0"
set "APP=%~dp0artifacts\PowerScalerLabs\PowerScalerLabs.exe"
if not exist "%APP%" (
  echo PowerScaler Labs has not been built from this package yet.
  echo Run START_HERE.cmd first.
  pause
  exit /b 1
)
start "PowerScaler Labs" "%APP%"
exit /b 0
