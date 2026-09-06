@echo off
rem Open-Firewall.bat - 雙擊即可開放 remote-stream 需要的防火牆埠 (UDP 9000-9002)
rem 會自己請求系統管理員權限 (UAC)。

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
  exit /b
)

netsh advfirewall firewall delete rule name=remote-stream >nul 2>&1
netsh advfirewall firewall add rule name=remote-stream dir=in action=allow protocol=UDP localport=9000,9001,9002

echo.
echo [OK] Firewall opened: inbound UDP 9000, 9001, 9002 (remote-stream)
echo      RemotePlay input/audio can now reach this host.
echo.
pause
