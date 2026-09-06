# remote-stream — 自架低延遲遊戲串流(類 GeForce Now）

在你的 **RTX 4070** 主機上跑遊戲,用 **NVENC 硬體編碼**把畫面串到另一台 Windows,
客戶端 **NVDEC 硬解**顯示,並把鍵盤/滑鼠**即時回傳注入**到主機。區網下可到 ~20–50ms
glass-to-glass,體感接近 GeForce Now / Parsec。

```
[主機 PC]  ddagrab 擷取 → NVENC 編碼 → SRT/UDP ─────► [客戶端 PC]  接收 → NVDEC 解碼 → 顯示
   ▲  SendInput 注入 ◄──── UDP 輸入封包 ◄──────────────  全域 hook 擷取鍵鼠
```

---

## 兩條路線

| | Track A（本專案目前）| Track B（升級版）|
|---|---|---|
| 影像引擎 | ffmpeg NVENC + ffplay NVDEC | 原生 C++ DXGI→NVENC 零拷貝 + 自訂 UDP |
| 需安裝 | 無（你已有 ffmpeg + .NET 10）| VS Build Tools + CMake + vcpkg |
| 延遲 | 低（區網 ~20–50ms）| 最低（省去 ffplay 緩衝與封裝開銷）|
| 狀態 | ✅ 可用 | 🔜 規劃中，見 ROADMAP |

先用 Track A 玩起來，確定體驗與網路 OK，再決定要不要做 Track B。

---

## 需求
- **主機端**：Windows + NVIDIA GPU（NVENC）、ffmpeg 在 PATH、.NET SDK。已驗證：RTX 4070 / ffmpeg 7.0 / .NET 10。
- **客戶端**：Windows + NVIDIA GPU（NVDEC，硬解；沒有也能軟解）、ffmpeg(含 ffplay) 在 PATH、.NET SDK。
- 兩台在**同一區網**最佳。跨網路請看下方「跨網路」。

## 快速開始（單一 exe）

打包後的 `dist\RemotePlay.exe` **自帶 .NET runtime**，拷到第二台 Windows 不用裝 .NET。
執行過 `./Bundle-Ffmpeg.ps1` 後，`dist\ffmpeg\` 內含 `ffmpeg.exe`/`ffplay.exe`，exe 會**自動使用內建那份**，
所以 **整個 `dist\` 資料夾拷到別台就能跑，連 ffmpeg 都不用另外裝**（exe 會先找旁邊的 `ffmpeg\`，再退回 PATH）。

- **雙擊 `RemotePlay.exe`** → 互動選單（選主機/客戶端、輸入 IP）。
- 或命令列：
  ```
  RemotePlay.exe host                       # 主機（跑遊戲那台）
  RemotePlay.exe client --host 192.168.0.50 # 客戶端（操作那台）
  ```
- 或用 PowerShell 包裝（方便調參數，會自動建置 exe）：`./Host.ps1` / `./Client.ps1 -HostIP <ip>`

一顆 exe 同時處理：影像(NVENC/NVDEC)、鍵鼠、FPS 相對滑鼠、音訊、手把、UPnP。

## 用 PowerShell 腳本（進階調參）

### 0. 先在單機自測（強烈建議）
在**主機**上開兩個 PowerShell 視窗，一個當 host、一個用 127.0.0.1 當 client，確認畫面通：
```powershell
# 視窗 A（只測影像，不含輸入）
./Host.ps1 -NoInput
# 視窗 B
./Client.ps1 -HostIP 127.0.0.1 -NoInput
```
會看到一個視窗顯示你自己的桌面（畫中畫是正常的）。能看到就代表編碼→傳輸→解碼全通。

### 1. 主機端（跑遊戲那台）
```powershell
./Host.ps1
# 常用參數：
#   -Codec h264|hevc|av1   預設 h264（相容性最好；hevc/av1 同畫質更省頻寬）
#   -Bitrate 30M           位元率（區網 1080p60 建議 20–50M）
#   -Fps 60                幀率
#   -Latency 30            SRT 緩衝(ms)，越低越即時但越怕丟包
#   -Monitor 0             要串哪個螢幕
#   -NoInput               只串影像、不開輸入注入伺服器
```
記住主機的區網 IP（`ipconfig`）。

### 2. 客戶端（操作那台）
```powershell
./Client.ps1 -HostIP 192.168.x.x
#   -Fullscreen            全螢幕
#   -NoInput               只看畫面、不回傳鍵鼠
#   其餘 -Codec/-Latency 需與主機一致
```

### 3. 操作
- 客戶端視窗開著時，按 **F8** 切換「控制主機」開/關。
- **ON**：你的鍵鼠會控制主機、並在本機被攔截（避免同時動到本機）。
- **OFF**：恢復操作本機。
- 緊急脫困：再按一次 **F8**；或 **Ctrl+Alt+Del**（系統層級，永遠有效）。

---

## 目前能做 / 限制
- ✅ 螢幕串流（H.264/HEVC/AV1，NVENC/NVDEC 硬體）
- ✅ 鍵盤 + 滑鼠 + 滾輪 + 側鍵，UDP 即時注入
- ✅ **FPS 相對滑鼠**（Raw Input + 相對注入，不重置游標）— F9 切換絕對/相對
- ✅ **音訊**（WASAPI loopback → Opus → 播放，免裝虛擬音效線）
- ✅ **手把**（客戶端 XInput → 主機虛擬 Xbox360，需主機裝 **ViGEmBus** 驅動）
- ✅ **UPnP 自動開埠**（跨網路，`-UPnP`）
- ⚠️ 有些**反作弊遊戲**會擋 SendInput 注入的輸入（Parsec 也有同樣限制）。
- ⚠️ 若遊戲以**系統管理員**執行，主機端也要以管理員執行才注入得進去。
- ⚠️ Desktop Duplication 需在**互動桌面工作階段**執行（不能當 Windows 服務跑）。
- ⚠️ 音訊需主機輸出裝置為 **48kHz**（Windows 音效設定，多數預設即是）；非 48k 會自動重取樣。
- 🎮 手把需在**主機**安裝 ViGEmBus：https://github.com/ViGEm/ViGEmBus/releases

## 跨網路（非區網）
- 主機需在路由器把 **影像埠（預設 9000）** 與 **輸入埠（預設 9001）** 轉發到主機。
- SRT 本身抗丟包，適合網路環境；把 `-Latency` 調到 60–120 視線路而定。
- 或改用 WireGuard/Tailscale 把兩台拉進同一虛擬網段，再照區網用法。

## 檔案結構
```
remote-stream/
  RemotePlay 一鍵啟動 = dist\RemotePlay.exe（雙擊或 CLI）
  Host.ps1 / Client.ps1   PowerShell 包裝（調參數用，會自動建置 exe）
  Resolve-Exe.ps1         找/建 RemotePlay.exe
  Bundle-Ffmpeg.ps1       把 ffmpeg/ffplay 打包進 dist\ffmpeg\（整包免裝）
  Test-Loopback.ps1       單機影像自測
  Open-Ports.ps1          UPnP 自動開/關路由器埠
  input/                  C#(.NET 10) 原始碼（Program/Media/Gamepad/Audio.cs）
  dist/                   打包成品：RemotePlay.exe + ffmpeg\（可整包拷走）
  ROADMAP.md              後續（靈敏度校準 / HUD / GUI / Track B 原生 C++）
  README.md
```

詳見 [ROADMAP.md](ROADMAP.md)。
