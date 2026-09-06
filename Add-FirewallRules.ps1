<#
  Add-FirewallRules.ps1 — 在「主機」開放 remote-stream 需要的入站 UDP 埠（需系統管理員）
  影像 9000 / 輸入 9001 / 音訊 9002。輸入之所以連不上，多半是這裡沒放行。
  用法（在主機、以系統管理員 PowerShell）:
    ./Add-FirewallRules.ps1
    ./Add-FirewallRules.ps1 -Remove          # 移除規則
    ./Add-FirewallRules.ps1 -VideoPort 9000 -InputPort 9001
#>
[CmdletBinding()]
param(
  [int]$VideoPort = 9000,
  [int]$InputPort = 9001,
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$name = 'remote-stream (UDP in)'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "需以『系統管理員』執行 PowerShell 才能改防火牆。請在開始選單對 PowerShell 按右鍵→以系統管理員身分執行，再跑一次。" }

# 先移除舊的同名規則
Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

if ($Remove) { Write-Host "[fw] 已移除規則「$name」" -ForegroundColor DarkGray; return }

$ports = @($VideoPort, $InputPort, ($InputPort + 1)) | Sort-Object -Unique
New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Protocol UDP `
  -LocalPort $ports -Profile Any | Out-Null
Write-Host "[fw] 已開放入站 UDP 埠：$($ports -join ', ')（所有網路設定檔）" -ForegroundColor Green
Write-Host "[fw] 現在客戶端的輸入/音訊封包就能進到主機了。" -ForegroundColor DarkGray
