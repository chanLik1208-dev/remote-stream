@echo off
rem Debug-Host.bat - run host as admin; window stays open after exit so you can read the error
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
  exit /b
)

echo === RemotePlay HOST (debug) - window will NOT close on exit ===
echo.

dist\RemotePlay.exe host
set RC=%errorlevel%

echo.
echo ================================================
echo   RemotePlay exited (code %RC%)
echo   Scroll UP to see the last lines = the crash reason
echo ================================================
echo.
pause
