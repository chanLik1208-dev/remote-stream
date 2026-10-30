# RDP-B — 自架低延遲遠端桌面/串流

前身是 **remote-stream**(ffmpeg NVENC/NVDEC 子行程版),現在整個從零重寫成
**RDP-B**:自製 Win32 視窗 + FFmpeg 函式庫(不再開 ffplay 子行程)、不綁死 NVIDIA、
有加密配對、支援代理(VLESS 等),修掉舊版操作端容易卡鍵的問題。舊版 Track A 的
設計與限制留在 git 歷史裡可查。

```
[Host]   libavfilter ddagrab(D3D11 texture)
         → avcodec 編碼器自動挑選(nvenc → amf → qsv → libx264)
         WASAPI loopback → Opus          ┐
         SendInput / ViGEm ← 輸入        ├─ Session(多工訊息)─ Transport ─┐
[Client] Win32 視窗 + D3D11VA 硬解(任何 GPU,失敗退回軟解)                 │
         焦點式 Raw Input → 輸入          ┘                              │
Transport:TCP+TLS(mTLS)現已可用;QUIC 直連(預設優先)架構已就位但 Host 端
          尚未開 QUIC listener;可經 SOCKS5 / HTTP CONNECT 代理
```

---

## 目前狀態(2026-10-30)

這是**正在進行中的重寫**,不是成品。以下是老實列出的現況,不是願望清單。

### 已經真的能動、有測試驗證的部分
- ✅ **Transport**:`RdpB.Core` 的訊息格式、`Session` 多工、`TcpTlsTransport`
  (mTLS,`SslStream` 雙向憑證驗證)、SOCKS5/HTTP CONNECT 代理握手。
- ✅ **配對/加密**:自簽憑證(DPAPI 加密存放)、6 位數 PIN 配對流程(`PairingService`/
  `PairingCoordinator`)已經接上實際網路層,不只是計算邏輯——Host/Client 都會驗證
  *實際 TLS 連線上的對方憑證*,不是自報的。配對成功後指紋釘選,之後連線走
  mTLS + 指紋比對,不用再輸入 PIN。
- ✅ **Host 端串流**:配對成功後 `CaptureEncoder`(`ddagrab` 擷取 + 編碼器自動挑選)/
  `AudioCapturer`/`Injector` 全部接上 `Session`,真的會開始串流、注入鍵鼠。
- ✅ **Client 端串流視窗**:`RdpB.Client.StreamWindow` 是真的 Win32 視窗(自己的
  訊息迴圈執行緒,GDI `StretchDIBits` letterbox 繪製),接上
  `Decoder`(D3D11VA 硬解,失敗退回軟解)/`AudioPlayer`/`InputCaptureController`。
  熱鍵走 `RegisterHotKey`:切換控制、切換滑鼠模式、退出。
- ✅ **Avalonia GUI**:裝置清單、「連線」按鈕觸發完整的連線→配對(跳 PIN 對話框)
  →開串流視窗流程、Host 開關、基本設定頁。
- ✅ 單元測試(fast suite)139 個全綠,涵蓋 frame 編解碼、代理握手、配對 HMAC、
  `HostListenerService` 併發/心跳逾時行為等。另有 `Category=Hardware` 的整合測試
  (需要真實 GPU/互動桌面),涵蓋 Host 端到端串流、Client 端真正開視窗+解碼+配對
  全流程,本機驗證通過。

### 還沒做 / 已知缺口(不是藏起來,是刻意列出來)
- ⚠️ **Host 端沒有 QUIC listener**——只監聽 TCP+TLS,所以 Client 目前直接走
  TCP+TLS,不透過會先試 QUIC 的 `TransportConnector`。架構(`System.Net.Quic`)
  已經在 `RdpB.Core` 裡,只是 Host 端還沒接上監聽那一半。
- ⚠️ **§7 的 RDP 外觀偽裝(直連時的流量偽裝)尚未實作**。
- ⚠️ **WebSocket+TLS transport 完全沒實作**。
- ⚠️ **「連線中斷,正在重連…」疊字覆蓋層**目前只用串流視窗的標題列文字反映現況,
  沒有真正畫覆蓋層;也沒有保留最後一幀畫面的機制。
- ⚠️ **多個串流視窗同時開著**:目前允許同時連多台、開多個串流視窗,沒有刻意擋
  (比原計畫「只能連一台,第二次變切換過去」更寬鬆,還沒造成問題但不是刻意設計)。
- ⚠️ **設定頁的代理網址**(`SettingsViewModel.Save()`)還沒接到
  `TransportSettings.ProxyUrl`,目前只存訂閱網址/設定檔路徑。
- ⚠️ **無人值守**(開機就能連、看得到登入畫面、過 UAC)完全沒做,刻意留給之後
  獨立的 `RdpB.UnattendedService` 專案——現有架構本來就不會鎖本機畫面、不會登出
  使用者,只是「本機要有人已經登入、桌面要是亮的」才看得到/操作得到。
- ⚠️ **兩台實體機器的真實延遲/手感測試還沒做**,目前都是單機 127.0.0.1 自連驗證。
- 更細的協定層缺口清單(哪些 `MessageType` 的 payload 格式還沒定案等)見
  [`docs/PROTOCOL.md`](docs/PROTOCOL.md) 第 15 節——**但那份文件寫於配對網路層/
  Host 串流接線完成之前**,裡面列的「配對網路層沒串起來」「mTLS 細節沒落地」
  這幾條目前已經做完,以上面這份清單為準,PROTOCOL.md 之後會再核對更新。

---

## 專案結構

```
rdp-b.slnx
src/
  RdpB.Core           訊息格式、Session 多工、Transport、代理握手、配對/憑證釘選(不碰 Windows API)
  RdpB.Core.Windows   設定檔(config.json)、憑證存放(DPAPI)
  RdpB.Host           擷取(ddagrab)+ 編碼、音訊擷取、輸入注入、手把
  RdpB.Client         Win32 串流視窗、解碼(D3D11VA/軟解)、輸入擷取、音訊播放
  RdpB.App            單一 exe:Avalonia GUI + host/client/pair 子命令
tests/RdpB.Tests      xUnit;[Trait("Category","Hardware")] 標記需要真實 GPU/桌面的整合測試
docs/PROTOCOL.md      線路協定參考文件(給之後要寫 Swift/macOS client 的人看)
```

## 開發/測試

```powershell
dotnet test --filter "Category!=Hardware"   # 快速單元測試,CI 可跑
dotnet test --filter "Category=Hardware"    # 需要真實 GPU + 互動桌面,本機跑
```

目前還沒有打包/發行腳本(舊版的 `Host.ps1`/`Client.ps1`/`Bundle-Ffmpeg.ps1` 等已經
隨重寫移除,邏輯已經在 `RdpB.App` 裡原生實作)。單一 exe 打包、FFmpeg shared DLL
體積驗證見計畫文件,還在後續階段。

## 授權

本專案綁定 FFmpeg(GPLv3 build),散布相關授權與對應原始碼位置見
[`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md) 與
[`LICENSE-GPLv3-ffmpeg.txt`](./LICENSE-GPLv3-ffmpeg.txt)。
