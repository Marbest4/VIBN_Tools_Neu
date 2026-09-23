@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Prepare-Development.ps1" -SelectFeeSdk %*
if errorlevel 1 (
  echo.
  echo Die Entwicklungsumgebung konnte nicht vorbereitet werden.
  pause
)
exit /b %errorlevel%
