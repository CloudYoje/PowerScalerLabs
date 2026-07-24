@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-Windows.ps1"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
  echo.
  echo PowerScaler Labs publish failed.
  echo Log: %~dp0logs\publish.log
  pause
)
exit /b %EXIT_CODE%
