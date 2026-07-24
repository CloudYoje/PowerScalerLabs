@echo off
setlocal
set "DATA=%LOCALAPPDATA%\PowerScaler Labs\Data"
if not exist "%DATA%" mkdir "%DATA%"
start "" "%DATA%"
