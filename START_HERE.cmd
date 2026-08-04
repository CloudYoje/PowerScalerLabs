@echo off
setlocal
cd /d "%~dp0"
set "APP=%~dp0artifacts\PowerScalerLabs\PowerScalerLabs.exe"

if /i "%~1"=="/build" goto build
if /i "%~1"=="/rebuild" goto build
if exist "%APP%" goto launch

echo No published app was found. Building PowerScaler Labs first...
goto publish

:build
echo Building and verifying PowerScaler Labs...

:publish
call "%~dp0PUBLISH_WINDOWS.cmd"
if errorlevel 1 goto publish_failed

if not exist "%APP%" (
  echo ERROR: Published app is missing:
  echo %APP%
  pause
  exit /b 1
)

:launch
start "PowerScaler Labs" "%APP%"
if errorlevel 1 (
  echo ERROR: Windows could not launch PowerScalerLabs.exe.
  pause
  exit /b 1
)
exit /b 0

:publish_failed
echo.
echo The app was not launched because publishing failed.
echo Review: %~dp0logs\publish.log
pause
exit /b 1
