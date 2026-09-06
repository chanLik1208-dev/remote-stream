<#
  Host.ps1 — 主機端啟動器（RemotePlay.exe 的輕量包裝）
  影像(NVENC)+ 輸入注入 + 音訊(WASAPI loopback→Opus)+ 虛擬手把(需 ViGEmBus) 全部由 exe 處理。
  用法:
    ./Host.ps1
    ./Host.ps1 -Codec hevc -Bitrate 40M
    ./Host.ps1 -UPnP           # 跨網路:自動開路由器埠
    ./Host.ps1 -NoInput -NoAudio
  提示:控制以系統管理員執行的遊戲時,本腳本也要用系統管理員 PowerShell 執行。
#>
[CmdletBinding()]
param(
  [int]$Port = 9000,
  [int]$InputPort = 9001,
  [ValidateSet('h264','hevc','av1')][string]$Codec = 'h264',
  [string]$Bitrate = '30M',
  [int]$Fps = 60,
  [int]$Latency = 40,
  [int]$Monitor = 0,
  [switch]$NoInput,
  [switch]$NoAudio,
  [switch]$UPnP,
  [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$exe = & (Join-Path $PSScriptRoot 'Resolve-Exe.ps1') -Rebuild:$Rebuild

$a = @('host','--port',"$Port",'--input-port',"$InputPort",'--codec',$Codec,
       '--bitrate',$Bitrate,'--fps',"$Fps",'--latency',"$Latency",'--monitor',"$Monitor")
if ($NoInput) { $a += '--no-input' }
if ($NoAudio) { $a += '--no-audio' }
if ($UPnP)    { $a += '--upnp' }
& $exe @a
