@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-Portable.ps1" %*
if errorlevel 1 exit /b 1
echo Das portable Paket liegt unter artifacts\publish.
endlocal
