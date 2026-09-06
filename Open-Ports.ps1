<#
  Open-Ports.ps1 — 用 UPnP 自動在路由器開/關埠（跨網路連線用）
  依賴 Windows 內建 COM「HNetCfg.NATUPnP」，需路由器有開 UPnP。
  用法:
    ./Open-Ports.ps1                       # 開 9000,9001 (UDP)
    ./Open-Ports.ps1 -Remove               # 移除
    ./Open-Ports.ps1 -Status               # 只列出目前的對應
    ./Open-Ports.ps1 -Ports 9000,9001 -Protocol UDP
  說明: SRT 影像與輸入都走 UDP，所以預設開 UDP。
#>
[CmdletBinding()]
param(
  [int[]]$Ports = @(9000, 9001),
  [ValidateSet('UDP','TCP')][string]$Protocol = 'UDP',
  [switch]$Remove,
  [switch]$Status,
  [switch]$Quiet
)

function Get-LocalIPv4 {
  $cfg = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
    Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } | Select-Object -First 1
  if ($cfg) { return $cfg.IPv4Address.IPAddress }
  return (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -First 1).IPAddress
}

function Get-NatCollection {
  try {
    $nat = New-Object -ComObject HNetCfg.NATUPnP
    $coll = $nat.StaticPortMappingCollection
  } catch {
    throw "無法建立 UPnP COM 物件：$($_.Exception.Message)"
  }
  if ($null -eq $coll) {
    throw "路由器沒有回應 UPnP（未啟用 UPnP，或 Windows 的 'SSDP Discovery' 服務未執行）。請到路由器開啟 UPnP，或改用手動轉發 / WireGuard。"
  }
  return $coll
}

$coll = Get-NatCollection

if ($Status) {
  Write-Host "目前 UPnP 埠對應：" -ForegroundColor Cyan
  $any = $false
  foreach ($m in $coll) {
    $any = $true
    "  {0}/{1}  ->  {2}:{3}  [{4}]  enabled={5}" -f `
      $m.ExternalPort, $m.Protocol, $m.InternalClient, $m.InternalPort, $m.Description, $m.Enabled | Write-Host
  }
  if (-not $any) { Write-Host "  （無）" -ForegroundColor DarkGray }
  return
}

if ($Remove) {
  foreach ($p in $Ports) {
    try { $coll.Remove($p, $Protocol); if (-not $Quiet) { Write-Host "[upnp] 已移除 $p/$Protocol" -ForegroundColor DarkGray } }
    catch { if (-not $Quiet) { Write-Host "[upnp] 移除 $p/$Protocol 略過（不存在）" -ForegroundColor DarkGray } }
  }
  return
}

# 新增對應
$localIP = Get-LocalIPv4
if (-not $localIP) { throw "找不到本機 IPv4 位址。" }

foreach ($p in $Ports) {
  try {
    # 先清掉舊的同埠對應避免衝突
    try { $coll.Remove($p, $Protocol) } catch {}
    [void]$coll.Add($p, $Protocol, $p, $localIP, $true, "remote-stream $p")
    if (-not $Quiet) { Write-Host "[upnp] 已開埠 $p/$Protocol -> $localIP`:$p" -ForegroundColor Green }
  } catch {
    Write-Host "[upnp] 開埠 $p/$Protocol 失敗：$($_.Exception.Message)" -ForegroundColor Yellow
  }
}

# 顯示對外 IP（客戶端要連的位址）— 對外查詢自己的公網 IP，非必要可忽略
try {
  $wan = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 4)
  if (-not $Quiet) {
    Write-Host "[upnp] 對外(公網) IP：$wan" -ForegroundColor Cyan
    Write-Host "[upnp] 客戶端連線：./Client.ps1 -HostIP $wan -Port $($Ports[0])" -ForegroundColor Cyan
  }
} catch {
  if (-not $Quiet) { Write-Host "[upnp] （無法查公網 IP，請自行到 whatismyip 確認）" -ForegroundColor DarkGray }
}
