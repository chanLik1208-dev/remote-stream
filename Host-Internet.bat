@echo off
rem Host-Internet.bat - 跨網際網路用的主機設定 (低位元率 + 高 SRT 緩衝)
rem 若還是會斷,把下面的 8M 改小 (例如 5M / 4M),或把 120 改大 (例如 180)。
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
  exit /b
)

echo === RemotePlay HOST (internet preset: 8Mbps, SRT latency 120ms) ===
echo.

dist\RemotePlay.exe host --bitrate 8M --latency 120
set RC=%errorlevel%

echo.
echo ==== exited (code %RC%) — scroll up for details ====
pause
