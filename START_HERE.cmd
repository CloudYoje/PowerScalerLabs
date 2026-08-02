@echo off
setlocal
cd /d "%~dp0"
set "APP=%~dp0artifacts\PowerScalerLabs\PowerScalerLabs.exe"

echo Building the cleaned PowerScaler Labs research app and sealed HealthScale 1.1.1 companion...
call "%~dp0PUBLISH_WINDOWS.cmd"
if errorlevel 1 (
  echo.
  echo The app was not launched because publishing failed.
  echo Review: %~dp0logs\publish.log
  pause
  exit /b 1
)

if not exist "%APP%" (
  echo ERROR: Published app is missing:
  echo %APP%
  pause
  exit /b 1
)

start "PowerScaler Labs" "%APP%"
if errorlevel 1 (
  echo ERROR: Windows could not launch PowerScalerLabs.exe.
  pause
  exit /b 1
)
exit /b 0
