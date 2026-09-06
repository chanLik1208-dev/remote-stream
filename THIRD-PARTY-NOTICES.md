# 第三方元件聲明

本專案的**原始碼**不含任何第三方程式碼。以下聲明僅適用於
[Releases](https://github.com/chanLik1208-dev/remote-stream/releases) 中
**包含 ffmpeg 的那個下載檔**（`RemotePlay-with-ffmpeg.zip`）。

---

## FFmpeg

| | |
|---|---|
| 版本 | `7.0-essentials_build` |
| 建置者 | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) |
| 授權 | **GPL v3**（建置參數含 `--enable-gpl --enable-version3`，靜態連結 libx264 / libx265 / libxvid） |
| 授權全文 | 本 repo 的 [`LICENSE-GPLv3-ffmpeg.txt`](./LICENSE-GPLv3-ffmpeg.txt) |
| 對應原始碼 | FFmpeg 上游：<https://ffmpeg.org/download.html> ・ 該建置的原始碼與建置腳本：<https://github.com/GyanD/codexffmpeg>（對應 tag `7.0`） |

### 為什麼 RemotePlay 本身不受 GPL 約束

`RemotePlay.exe` **沒有連結、也沒有內嵌** FFmpeg 的任何程式碼。
它是把 `ffmpeg.exe` / `ffplay.exe` 當作**獨立的外部程式**，透過命令列參數與管線呼叫。

兩者是各自獨立的執行檔、各自獨立的行程，以命令列這種通用介面溝通，
屬於 GPL 所稱的 *mere aggregation*（單純聚集），不構成衍生著作。
因此 `RemotePlay.exe` 的授權不受 FFmpeg 的 GPL 影響。

GPL 的義務只落在**散布 ffmpeg 二進位檔**這件事本身 —— 也就是上表所列的
授權全文與對應原始碼位置，兩者都已在此提供。

### 如果你不想碰 GPL

下載 Releases 裡的 **`RemotePlay.exe`**（不含 ffmpeg 的乾淨版），
再自行取得 ffmpeg 放到 exe 旁邊的 `ffmpeg\` 資料夾即可：

```powershell
# 從你 PATH 上既有的 ffmpeg 複製
./Bundle-Ffmpeg.ps1

# 或指定來源
./Bundle-Ffmpeg.ps1 -Source "D:\ffmpeg\bin"
```

你也可以改用 **LGPL 版本**的 ffmpeg 建置（不含 x264/x265），義務會輕很多；
本專案只用到 NVENC / NVDEC 與 SRT，不依賴 x264/x265。
