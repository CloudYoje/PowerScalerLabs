@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Import-PreviousData.ps1" -SourceDataFolder "%~1"
set "EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%EXIT_CODE%"=="0" (
  echo Import did not complete.
) else (
  echo Import completed.
)
pause
exit /b %EXIT_CODE%
