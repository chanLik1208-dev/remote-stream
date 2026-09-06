# ROADMAP

## ✅ 已完成（Track A）
- 螢幕擷取 → NVENC 硬編（h264/hevc/av1）→ SRT 傳輸 → NVDEC 硬解顯示
- 鍵盤（scancode，遊戲相容）+ 滑鼠按鍵 + 滾輪 + 側鍵，UDP 即時注入
- **FPS 相對滑鼠**：Raw Input 讀原始位移 + 主機相對注入，**不重置游標**（解決多數遠端玩不了 FPS 的問題）
- F8 控制開/關、F9 絕對/相對切換、本機輸入攔截
- **音訊**：WASAPI loopback → Opus(48k/立體聲/20ms) → UDP → 客戶端播放（免虛擬音效線）
- **手把**：客戶端 XInput → PAD 封包 → 主機 ViGEmBus 虛擬 Xbox360（需裝驅動）
- **UPnP 自動開埠**（跨網路）
- **單一 exe**：`dist\RemotePlay.exe` 自帶 runtime + 互動選單，一顆檔全包

## 🔜 接下來（Track A 強化，仍不需 C++）
1. **相對滑鼠靈敏度校準**：主機關「增強指標精確度」求 1:1，或協定帶 scale 係數。
2. **音訊強化**：jitter buffer 調校、封包序號 + Opus PLC 抗丟包、影音同步微調。
3. **自適應位元率 / 統計 HUD**：依 SRT 回報 RTT/丟包動態調 `-b:v`；顯示延遲/fps/bitrate。
4. **一鍵 GUI 啟動器**：.NET WinForms 包 host/client、IP、畫質、狀態，取代主控台。
5. **多螢幕 / 指定視窗擷取**、剪貼簿同步、斷線自動重連。
6. **手把震動回傳**（ViGEm 的 rumble callback → 客戶端 XInputSetState）。

## 🧭 Track B — 原生 C++ 最低延遲客戶端（壓榨最後 10–20ms）
需先安裝：VS Build Tools（C++）、CMake、vcpkg。

- **主機**：DXGI Desktop Duplication 直接取 D3D11 texture → **零拷貝**餵給 NVENC（省掉 ffmpeg 的封裝與 GPU→CPU 往返）。
- **傳輸**：自訂 UDP：NAL 分片、序號、選擇性重送 + FEC、pacing（取代 mpegts/SRT 的通用開銷）。
- **客戶端**：自寫 NVDEC 解碼 → D3D11 flip-model swapchain（waitable、低延遲 present），整合 Raw Input，游標完全隱藏。
- 目標：LAN glass-to-glass 進入 Moonlight/Parsec 等級（個位數～十幾 ms）。

建議路徑：先把 Track A 玩順、量出實際延遲與瓶頸，再決定 Track B 是否值得投入（多數情況 Track A 在區網已足夠好）。

## 已知限制
- 反作弊遊戲可能擋 SendInput 注入輸入。
- 遊戲以管理員執行時，主機輸入伺服器也需管理員權限。
- Desktop Duplication 需互動桌面工作階段（非服務）。
- 相對滑鼠注入會受主機滑鼠加速設定影響，必要時關閉「增強指標精確度」。
