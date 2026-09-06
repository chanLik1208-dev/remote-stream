@echo off
rem Debug-Client.bat - run client with the console visible; window stays open after exit
cd /d "%~dp0"

set /p HOSTIP=Host IP or domain (e.g. zero.sakedns.cc):
if "%HOSTIP%"=="" set HOSTIP=zero.sakedns.cc

echo.
echo === RemotePlay CLIENT -> %HOSTIP%  (latency 120) ===
echo Watch for:  [client] hook 已安裝  /  press F8 -> [client] 控制 ON  /  [client] 已送出第一個輸入
echo.

dist\RemotePlay.exe client --host %HOSTIP% --latency 120
set RC=%errorlevel%

echo.
echo ==== client exited (code %RC%) - scroll up for details ====
pause
