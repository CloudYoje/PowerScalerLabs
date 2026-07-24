@echo off
setlocal
set "LOGS=%LOCALAPPDATA%\PowerScaler Labs\Logs"
if not exist "%LOGS%" mkdir "%LOGS%"
start "" "%LOGS%"
