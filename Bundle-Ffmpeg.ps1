<#
  Bundle-Ffmpeg.ps1 — 把 ffmpeg/ffplay 複製進 dist\ffmpeg\，讓整包免裝 ffmpeg 就能跑。
  RemotePlay.exe 會自動去 exe 旁的 ffmpeg\ 找,所以拷進去即可,不需改 PATH。
  用法:
    ./Bundle-Ffmpeg.ps1                 # 從 PATH 上的 ffmpeg 複製
    ./Bundle-Ffmpeg.ps1 -Source "D:\ffmpeg\bin"
    ./Bundle-Ffmpeg.ps1 -IncludeProbe   # 順便帶 ffprobe（一般用不到）
#>
[CmdletBinding()]
param(
  [string]$Source,
  [string]$Dest = (Join-Path $PSScriptRoot 'dist\ffmpeg'),
  [switch]$IncludeProbe
)
$ErrorActionPreference = 'Stop'

if (-not $Source) {
  $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
  if (-not $cmd) { throw "PATH 上找不到 ffmpeg。請用 -Source 指定 ffmpeg\bin 資料夾。" }
  $Source = Split-Path $cmd.Source
}
if (-not (Test-Path (Join-Path $Source 'ffmpeg.exe')))  { throw "$Source 裡沒有 ffmpeg.exe" }
if (-not (Test-Path (Join-Path $Source 'ffplay.exe')))  { throw "$Source 裡沒有 ffplay.exe（客戶端顯示需要它）" }

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

$files = @('ffmpeg.exe','ffplay.exe')
if ($IncludeProbe) { $files += 'ffprobe.exe' }
# shared 版會有 av*.dll / SDL*.dll,一併帶上（靜態版則無）
$files += (Get-ChildItem $Source -Filter *.dll -ErrorAction SilentlyContinue).Name

$total = 0
foreach ($f in ($files | Where-Object { $_ } | Select-Object -Unique)) {
  $s = Join-Path $Source $f
  if (Test-Path $s) {
    Copy-Item $s -Destination $Dest -Force
    $sz = (Get-Item (Join-Path $Dest $f)).Length; $total += $sz
    Write-Host ("  複製 {0}  ({1:N1} MB)" -f $f, ($sz/1MB)) -ForegroundColor Green
  }
}
Write-Host ("[bundle] 完成 → {0}  (共 {1:N0} MB)" -f $Dest, ($total/1MB)) -ForegroundColor Cyan
Write-Host "[bundle] 現在 dist\ 整包可直接拷到另一台 Windows，免裝 ffmpeg。" -ForegroundColor DarkGray
