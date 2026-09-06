<#
  Client.ps1 — 客戶端啟動器（RemotePlay.exe 的輕量包裝）
  影像(NVDEC 硬解顯示)+ 鍵鼠回傳 + 音訊播放 + 手把(XInput→主機) 全部由 exe 處理。
  用法:
    ./Client.ps1 -HostIP 192.168.0.50
    ./Client.ps1 -HostIP 192.168.0.50 -Fullscreen
    ./Client.ps1 -HostIP 192.168.0.50 -NoInput          # 只看畫面+聽聲音
  操作:  F8 = 控制主機 開/關    F9 = 滑鼠 相對(FPS)/絕對(桌面) 切換
  -Codec / -Latency 需與主機一致。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$HostIP,
  [int]$Port = 9000,
  [int]$InputPort = 9001,
  [ValidateSet('h264','hevc','av1')][string]$Codec = 'h264',
  [int]$Latency = 40,
  [ValidateSet('rel','abs')][string]$Mouse = 'rel',
  [string]$Toggle = 'F8',
  [string]$ToggleMouse = 'F9',
  [switch]$Fullscreen,
  [switch]$SoftwareDecode,
  [switch]$NoInput,
  [switch]$NoAudio,
  [switch]$NoGamepad,
  [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
$exe = & (Join-Path $PSScriptRoot 'Resolve-Exe.ps1') -Rebuild:$Rebuild

$a = @('client','--host',$HostIP,'--port',"$Port",'--input-port',"$InputPort",'--codec',$Codec,
       '--latency',"$Latency",'--mouse',$Mouse,'--toggle',$Toggle,'--toggle-mouse',$ToggleMouse)
if ($Fullscreen)     { $a += '--fullscreen' }
if ($SoftwareDecode) { $a += '--software-decode' }
if ($NoInput)        { $a += '--no-input' }
if ($NoAudio)        { $a += '--no-audio' }
if ($NoGamepad)      { $a += '--no-gamepad' }
& $exe @a
