# ROADMAP

延續 README「目前狀態」那份缺口清單,這裡列接下來要做的順序。完整架構決策見
`docs/PROTOCOL.md` 和專案根目錄外的設計計畫(Kyra 手上那份)。

## 下一步(照優先順序)
1. **Host 端 QUIC listener**:目前 Client 只走 TCP+TLS(因為 Host 沒監聽 QUIC)。
   接上之後把 `ClientConnectionService` 換回透過 `TransportConnector` 的
   自動選擇(QUIC 優先、逾時退回 TCP)。
2. **§7 RDP 外觀偽裝**(`IObfuscator`,僅 DIRECT 模式自動套用):握手外觀偽裝、
   TLS 指紋貼近 schannel、閒置流量外形。
3. **重連 UX**:串流視窗真正的「連線中斷,正在重連…」疊字覆蓋層 + 保留最後一幀,
   取代目前只改標題列文字的暫時做法。
4. **多視窗行為定案**:目前允許同時開多個串流視窗,沒刻意擋——跟 Kyra 確認要不要
   照原計畫限制成「同時只能連一台,第二次連線變切換過去」,還是維持現在更寬鬆的行為。
5. **設定頁代理網址串接**:`SettingsViewModel.Save()` 要把輸入的代理網址實際存到
   `TransportSettings.ProxyUrl`。
6. **WebSocket+TLS transport**:目前完全沒實作,`TransportMode.Ws` 會丟
   `NotSupportedException`。
7. **HUD**(連線狀況/協議細節/拆解延遲三塊,熱鍵 F10 循環)尚未實作。
8. **單一 exe 打包 + 體積驗證**:`dotnet publish` self-contained + FFmpeg shared DLL
   打進 `dist\ffmpeg\`,驗證總體積落在預算內(gyan full-shared 目前估算會超出
   原訂 180–250MB 預算,落在 330–350MB 左右,需要評估要不要自編精簡版 FFmpeg)。
9. **兩台實體機器測試**:目前所有驗證都是單機 127.0.0.1 自連,真實延遲/手感
   需要 Kyra 在兩台機器上實測。

## 刻意不在這個階段做的
- **無人值守**(開機就能連、登入畫面、過 UAC)——另外開一個獨立的
  `RdpB.UnattendedService` 專案,需要 Windows 服務 + Session 0 切換 +
  Secure Desktop 擷取/注入,技術難度和權限敏感度都高,等核心功能穩定、
  Kyra 真的需要再做。
- 4:4:4 銳利文字、多螢幕、剪貼簿/檔案同步、P2P 打洞/relay、自適應位元率、
  Mac 客戶端——全部留給之後的階段,`RdpB.Core` 目前的設計已經盡量不碰
  Windows API,方便之後跨平台。
