@echo off
setlocal
cd /d "%~dp0"
where msbuild >nul 2>nul
if errorlevel 1 (
  echo MSBuild was not found. Open a Visual Studio Developer Command Prompt.
  exit /b 1
)
msbuild HealthScale.sln /m /p:Configuration=Release /p:Platform=x64
