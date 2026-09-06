<#
  Test-Loopback.ps1 — 單機影像自測（不含輸入）
  在同一台機器上跑 host 編碼 → 127.0.0.1 → ffplay 解碼顯示，
  用來確認「擷取→NVENC→SRT→NVDEC→顯示」整條管線在你機器上是通的。
  會看到一個視窗顯示你自己的桌面（畫中畫是正常現象）。關閉視窗即結束。
#>
[CmdletBinding()]
param(
  [int]$Port = 9000,
  [ValidateSet('h264','hevc','av1')][string]$Codec = 'h264',
  [string]$Bitrate = '30M',
  [int]$Fps = 60,
  [int]$Latency = 40,
  [int]$Monitor = 0
)
$ErrorActionPreference = 'Stop'
$enc = "${Codec}_nvenc"; $gop = [int]($Fps * 2)
$srt = "srt://0.0.0.0:$($Port)?mode=listener&latency=$($Latency)&transtype=live"
$ff = @(
  '-hide_banner','-loglevel','warning',
  '-filter_complex', "ddagrab=output_idx=$($Monitor):framerate=$($Fps)",
  '-fps_mode','cfr','-c:v',$enc,'-preset','p1','-tune','ll',
  '-rc','cbr','-b:v',$Bitrate,'-maxrate',$Bitrate,'-bf','0','-g',"$gop",
  '-no-scenecut','1','-forced-idr','1','-delay','0',
  '-f','mpegts','-flush_packets','1','-muxdelay','0','-muxpreload','0', $srt
)

Write-Host "[test] 啟動主機編碼 (SRT :$Port) …" -ForegroundColor Green
$hostProc = Start-Process -FilePath 'ffmpeg' -ArgumentList $ff -NoNewWindow -PassThru
try {
  Start-Sleep -Milliseconds 800
  $url = "srt://127.0.0.1:$($Port)?latency=$($Latency)"
  $play = @('-hide_banner','-loglevel','warning','-an','-probesize','32','-analyzeduration','0',
            '-fflags','nobuffer','-flags','low_delay','-framedrop','-vcodec',"${Codec}_cuvid",
            '-window_title','RemotePlay loopback test', $url)
  Write-Host "[test] 開啟 ffplay 檢視 $url（關閉視窗即結束）" -ForegroundColor Green
  & ffplay @play
}
finally {
  if ($hostProc -and -not $hostProc.HasExited) { Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue }
}
