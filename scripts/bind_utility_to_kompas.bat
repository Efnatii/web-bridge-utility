@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%bind_utility_to_kompas.ps1"

if not exist "%PS_SCRIPT%" (
  echo ERROR: PowerShell helper was not found:
  echo   %PS_SCRIPT%
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -WaitForKompasExit %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
  echo.
  echo Script failed with exit code %EXIT_CODE%.
)

exit /b %EXIT_CODE%
