# remote-stream — macOS 客戶端

從 **Mac** 操作跑在 **Windows 主機**（本專案 `dist\RemotePlay.exe`）上的遊戲。
主機端**不用改**：這個 mac 客戶端用**完全相同的線路協定**接影像、回傳鍵鼠/手把。

```
[Windows 主機 RTX4070] ──SRT/NVENC──►  [Mac 客戶端] ffplay 顯示
        ▲  ViGEm 虛擬手把 / SendInput ◄─UDP 協定─  CGEventTap 鍵鼠 + GameController 手把
```

## 需求
- macOS 12+
- **ffmpeg**（含 ffplay）：`brew install ffmpeg`
- Xcode command line tools（`xcode-select --install`）以取得 `swift`
- 權限：**系統設定 → 隱私與安全性 → 輔助使用**（必需，攔截/送鍵鼠）；FPS 相對滑鼠另需 **輸入監控**。

## 建置 / 執行
```bash
cd macos
./run-client.sh 192.168.0.50            # 一鍵建置並連線
# 或
swift build -c release
./.build/release/remoteplay client --host 192.168.0.50 --fullscreen
```

## 操作
- **F8** = 控制主機 開/關（開啟時鍵鼠控制主機並在 Mac 本機被攔截）
- **F9** = 滑鼠 相對(FPS) / 絕對(桌面) 切換
- 相對模式用 `CGAssociateMouseAndMouseCursorPosition(false)` + 原始 delta，**不重置游標** → FPS 視角正常。

## 參數
`--host <ip>`（必填）、`--port 9000`、`--input-port 9001`、`--latency 40`、
`--mouse rel|abs`（預設 rel）、`--fullscreen`、`--no-input`、`--no-gamepad`、`--no-video`。
（埠與 codec 需與主機一致；主機預設 h264/9000/9001。）

## 已實作
- ✅ 影像：ffplay 接 SRT（低延遲旗標，與 Windows 版一致）
- ✅ 鍵盤：**mac 鍵碼 → PC set-1 掃描碼**轉換（`KeyMap.swift`），主機才不會打錯鍵
- ✅ 滑鼠：絕對座標 / **FPS 相對位移** / 按鍵 / 滾輪 / 側鍵，F8/F9 切換、本機攔截
- ✅ 手把：GameController → XInput 佈局 PAD 封包 → 主機 ViGEm

## 已知落差 / 限制（相對 Windows 客戶端）
- 🔸 **音訊尚未做**：主機的音訊是獨立的 Opus/UDP 側通道（:9002），mac 端還沒有 Opus 解碼＋CoreAudio 播放。
  暫解：主機端把音訊 mux 進影像 SRT，ffplay 就能直接出聲（待做）。或之後加 libopus 綁定。
- 🔸 修飾鍵左右共用遮罩：`flagsChanged` 無法精準區分左右鍵同時按放的 down/up（邊角情況）。
- 🔸 keymap 為常用鍵集合；冷門鍵可能未涵蓋（未涵蓋者略過）。
- 🔸 影像目前走 ffplay 軟解；要 VideoToolbox 硬解可改用 mpv（`--hwdec=videotoolbox`）。
- 🔸 F8/F9 若被 macOS 系統功能占用，於「鍵盤設定」開「將 F1–F12 當標準功能鍵」。

## 未在此環境驗證
本客戶端於 Windows 開發分支撰寫，**尚未在 macOS 上 `swift build` 或實機測試**。
請在 Mac 上建置、依上述授權後對接 Windows 主機測試；協定層已對齊 `input/Program.cs` 的 `Proto`。
