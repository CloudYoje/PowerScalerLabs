@echo off
setlocal
cd /d "%~dp0"
where cl >nul 2>nul
if errorlevel 1 (
  echo cl.exe was not found. Open a Visual Studio Developer Command Prompt.
  exit /b 1
)
if not exist test-bin mkdir test-bin
cl /nologo /std:c++20 /EHsc /W4 ^
  tests\native\health_transition_model_tests.cpp ^
  src\native\HealthScale.Runtime\src\health_transition_model.cpp ^
  /Fe:test-bin\health_transition_model_tests.exe
if errorlevel 1 exit /b 1
test-bin\health_transition_model_tests.exe
