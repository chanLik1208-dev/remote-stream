<#
計畫 §10/§11:打包成 self-contained 發布(不是單一 exe)+ 绑 FFmpeg shared DLL,量 dist\ 的體積。
FFmpeg 是唯一要從網路下載的東西(gyan.dev 的 full-shared build),其餘都是本機建置。

**不加 -p:PublishSingleFile=true**(真的實測確認過的坑,見 src/RdpB.App/RdpB.App.csproj
上的註解):PublishSingleFile 跟 System.Net.Quic 的 msquic.dll 處不來,就算開了
IncludeNativeLibrariesForSelfExtract,msquic.dll 還是不會被自我解壓,QUIC 整條路會連不上
(退回 TCP+TLS,不會整個不能用,但失去低延遲路徑)。這個腳本原本有這個開關,是舊版留下來、
沒跟上這個發現的坑,已經拿掉。

用法:
    .\Build-Dist.ps1                  # 用 dist\ffmpeg\ 裡已經有的 FFmpeg(沒有就自動下載)
    .\Build-Dist.ps1 -SkipFfmpeg       # 只重新 publish .NET 部分,不管 FFmpeg DLL——
                                       # **注意**:實測發現 dotnet publish -o 會把上一次
                                       # 放進 dist\publish\ffmpeg\ 的檔案清掉,所以這個開關
                                       # 用完之後 dist\publish\ 裡不會有 FFmpeg,只適合「我只
                                       # 是想確認 .NET 那部分能不能建置成功」,不是真的能跑的
                                       # 完整打包,還是要再跑一次不帶這個開關的才會補回來。
    .\Build-Dist.ps1 -ForceFfmpegRedownload  # 不管 dist\ffmpeg\ 裡有沒有,重新下載一份
#>
param(
    [switch]$SkipFfmpeg,
    [switch]$ForceFfmpegRedownload
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$distDir = Join-Path $root "dist"
$publishDir = Join-Path $distDir "publish"
$ffmpegDir = Join-Path $publishDir "ffmpeg"

Write-Host "== dotnet publish(self-contained,不塞單一 exe——見上面關於 msquic.dll 的註解)==" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\RdpB.App") -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗" }

if (-not $SkipFfmpeg) {
    # 開發機上通常已經有 dist\ffmpeg\(Hardware 測試本來就要用到這份),直接沿用最省事,
    # 也等於用「這份 DLL 真的在這台機器上跑過 D3D11VA 解碼/NVENC 編碼」的證據來驗證打包。
    $existingFfmpeg = Join-Path $distDir "ffmpeg"
    if (-not $ForceFfmpegRedownload -and (Test-Path (Join-Path $existingFfmpeg "avcodec-*.dll"))) {
        Write-Host "== 沿用 dist\ffmpeg\(已驗證可用的那份)==" -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
        Copy-Item (Join-Path $existingFfmpeg "*.dll") $ffmpegDir -Force
    }
    else {
        Write-Host "== 從 gyan.dev 下載 FFmpeg full-shared build ==" -ForegroundColor Cyan
        $downloadDir = Join-Path $distDir "_ffmpeg_download"
        New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
        $archive = Join-Path $downloadDir "ffmpeg-full-shared.7z"
        Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full-shared.7z" -OutFile $archive

        $sevenZip = "C:\Program Files\7-Zip\7z.exe"
        if (-not (Test-Path $sevenZip)) { throw "找不到 7-Zip($sevenZip),裝一下或手動解壓 $archive" }
        $extractDir = Join-Path $downloadDir "extracted"
        & $sevenZip x $archive "-o$extractDir" -y | Out-Null

        $bin = Get-ChildItem $extractDir -Directory | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName "bin" }
        New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
        # 只帶執行期真的會載入的 shared DLL,不帶 ffmpeg.exe/ffplay.exe/ffprobe.exe——
        # RdpB 用 FFmpeg.AutoGen 直接呼叫函式庫,不是開子行程呼叫這幾個工具,不用帶。
        Copy-Item (Join-Path $bin "av*.dll") $ffmpegDir -Force
        Copy-Item (Join-Path $bin "sw*.dll") $ffmpegDir -Force

        Remove-Item $downloadDir -Recurse -Force
    }
}

$sizeMb = [math]::Round((Get-ChildItem $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "== 完成:$publishDir,共 $sizeMb MB ==" -ForegroundColor Green
Write-Host "計畫 §10 預算參考:330-350MB 左右(.NET runtime ~80MB + Avalonia/Skia ~35MB + FFmpeg shared DLL ~235MB)"

# Kyra 要求的功能:每次重新打包,真正會變動的只有「自己這幾個專案編出來的檔案」——
# Avalonia/NAudio/FFmpeg/msquic 這些第三方依賴,只要沒改套件版本就完全不會變,沒必要每次
# 都重傳整個 ~370MB 的 dist\publish\。這裡把真正會變的那一小撮檔案(我們自己的 5 個專案
# 編出來的 .dll/.exe,各自配對的 .pdb 方便之後真的當掉時對照堆疊,加上 .deps.json/
# .runtimeconfig.json 這兩個雖然平常不太會變、但重新 publish 一定會重新產生的中繼資料)
# 另外複製一份到 dist\update\——Kyra 只要留著 Host/Client 機器上原本那份完整的 dist,
# 之後每次都只需要拿 dist\update\ 這一份小資料夾去覆蓋過去就好。
Write-Host "== 整理增量更新資料夾 dist\update\ ==" -ForegroundColor Cyan
$updateDir = Join-Path $distDir "update"
if (Test-Path $updateDir) { Remove-Item $updateDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $updateDir | Out-Null

$ownAssemblies = @("RdpB.Core", "RdpB.Core.Windows", "RdpB.Host", "RdpB.Client", "RdpB")
foreach ($name in $ownAssemblies) {
    foreach ($ext in @(".dll", ".pdb")) {
        $f = Join-Path $publishDir "$name$ext"
        if (Test-Path $f) { Copy-Item $f $updateDir -Force }
    }
}
foreach ($f in @("RdpB.exe", "RdpB.deps.json", "RdpB.runtimeconfig.json")) {
    $src = Join-Path $publishDir $f
    if (Test-Path $src) { Copy-Item $src $updateDir -Force }
}

$updateSizeMb = [math]::Round((Get-ChildItem $updateDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "== 完成:$updateDir,共 $updateSizeMb MB(覆蓋到目標機器既有 dist\publish\ 裡同名檔案即可)==" -ForegroundColor Green
