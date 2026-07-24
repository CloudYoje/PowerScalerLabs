@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK 8 is required.
  exit /b 1
)
dotnet run --project "src\PowerScalerLabs.Runtime\PowerScalerLabs.Runtime.csproj" -c Release -- --architecture-self-test
exit /b %errorlevel%
