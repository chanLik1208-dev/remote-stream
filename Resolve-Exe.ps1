<#
  Resolve-Exe.ps1 — 找出 RemotePlay.exe 路徑;找不到就建置。回傳路徑字串。
  優先順序: dist\ 單一 exe > bin\Release\ > 重新建置。
#>
[CmdletBinding()]
param([switch]$Rebuild)
$ErrorActionPreference = 'Stop'

$dist = Join-Path $PSScriptRoot 'dist\RemotePlay.exe'
$bin  = Join-Path $PSScriptRoot 'input\bin\Release\net10.0-windows\RemotePlay.exe'
$proj = Join-Path $PSScriptRoot 'input\RemotePlay.csproj'

if (-not $Rebuild) {
  if (Test-Path $dist) { return $dist }
  if (Test-Path $bin)  { return $bin }
}

Write-Host '[build] 建置 RemotePlay …' -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o (Join-Path $PSScriptRoot 'dist') -v minimal --nologo | Out-Null
if (Test-Path $dist) { return $dist }

# 退而求其次:一般建置
dotnet build $proj -c Release -v minimal --nologo | Out-Null
return $bin
