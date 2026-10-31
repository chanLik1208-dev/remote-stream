# RDP-B 線路協定參考文件(給 Swift/iOS/macOS Client 實作用)

> 這份文件是給「要在 Xcode 裡從零寫一個新 Client」的人看的,不假設你讀過 C# 原始碼。
> 內容是直接從 `RdpB.Core`/`RdpB.Host`/`RdpB.Client` 的原始碼逐行核對後寫出來的,
> 但專案本身還在開發中,**有幾塊東西程式碼還沒接完整**——這些地方我會老實標出來,
> 不會假裝它已經是定案的協定。開工前請先看完第 15 節的缺口清單。

## 0. 最重要的一件事:目前沒有協定版本協商

**整個協定目前沒有任何版本號欄位、也沒有任何「我支援哪些版本」的交換機制。**
`MessageType.Hello` 存在,但它目前只被 QUIC transport 拿來當「priming frame」用
(見第 5 節),payload 是空的,沒有帶版本資訊。TCP transport 完全沒有送 Hello。

這代表:
- Host 和 Client 目前隱含「兩邊一定是同一個版本」,沒有任何機制可以偵測版本不合、
  也沒有任何欄位可以日後擴充。
- 如果你要寫一個獨立的 Swift client,**強烈建議你自己在應用層加一個版本欄位**
  (例如在 `Hello`/`ConfigSync` payload 裡塞一個版本號),而不是假設協定以後不會變。
- 在跟現有 C# Host 對接之前,務必先確認雙方的 commit/tag 一致,因為沒有任何東西會
  在版本不合時給你清楚的錯誤訊息——頂多是 Frame 解析失敗或 payload 長度不對。

---

## 1. 總覽

- **角色**:Host 是監聽方(被遠端操作的那台機器,跑擷取/編碼/輸入注入);
  Client 是主動連出去的那一方(操作者坐的那台機器,跑擷取顯示/輸入捕捉)。
- Host 監聽 TCP+TLS 的 port,也監聽 QUIC 的 port(UDP)。Client 依「自動選擇邏輯」
  (第 6 節)決定要用哪一種 transport 連過去。
- 連線建立(TLS/QUIC handshake 完成)之後,雙方建立一個 `Session`(見第 10 節),
  開始互送心跳。
- 之後的訊息流向是雙向的:
  - Client → Host:`Input` channel 的鍵盤/滑鼠/手把事件、`Control` channel 的
    配對/心跳/設定訊息、`Video` channel 的 `VideoKeyframeRequest`。
  - Host → Client:`Audio` channel 的 Opus 音訊幀、`Video` channel 的 H.264 影像幀、
    `Control` channel 的心跳/`ReleaseAll`/設定回應。
  - `Heartbeat`、`ReleaseAll` 兩種訊息兩邊都可以送,語意對稱(誰偵測到異常就送)。
- 所有訊息都走同一個多工 `Session`,用 `Channel`(第 2 節)分類、用
  `MessageType`(第 3 節)標記訊息種類。

---

## 2. Channel(byte enum)

```
Channel.Control = 0   // 配對、心跳、ReleaseAll、設定交換 — 可靠,絕不丟棄
Channel.Input   = 1   // 按鍵、滑鼠按鈕、滑鼠位移、手把 — 可靠,絕不丟棄
Channel.Audio   = 2   // Opus 音訊幀 — 可丟棄,但優先序高於 Video
Channel.Video   = 3   // 編碼後的影像幀 — 可丟棄,佇列滿了就丟最舊的
```

- **數值越小優先序越高**。送出佇列依 Control(0) > Input(1) > Audio(2) > Video(3)
  的順序處理。
- **可丟棄性**:只有 `Audio` 和 `Video` 允許在佇列塞滿時丟棄舊資料;
  `Control` 和 `Input` 永遠不丟(保證送達,否則斷線)。
- **可丟棄 channel 的佇列深度門檻**(超過這個深度就丟最舊的一筆):
  - `Video`:2
  - `Audio`:4
  - 不可丟的 channel 視為無上限。
- `Video` channel 因丟幀而觸發時,Host 端的慣例是立刻要求編碼器出一張 IDR
  (見第 13 節),因為舊幀被丟掉代表解碼端的參考畫面可能不連續了。

---

## 3. MessageType(byte enum)

```
// ── Control channel ──
Hello                 = 0   // 目前只在 QUIC 的「priming frame」用途上出現,見 §5
Pair                  = 1   // 配對請求(帶 PIN 相關資料)—— 見 §9,wire 格式尚未定案
PairAck               = 2   // 配對回應 —— 同上,尚未定案
Heartbeat             = 3   // 空 payload,見 §10
ReleaseAll            = 4   // 空 payload,強制放開所有按鍵,見 §10
ConfigSync            = 5   // 設定交換 —— payload 格式尚未在這批原始碼中看到,需要再確認
Bye                   = 6   // 主動斷線通知 —— payload 格式未見,大概率是空 payload,需要再確認

// ── Input channel ──
Key                    = 10  // 見 §11
MouseAbs               = 11
MouseRel               = 12
MouseBtn               = 13
Wheel                  = 14
Pad                    = 15  // enum 存在,但目前完全沒有任何程式碼處理它,見 §15

// ── Audio channel ──
AudioFrame             = 20  // 見 §12

// ── Video channel ──
VideoFrame             = 30  // 見 §13
VideoKeyframeRequest   = 31  // Client → Host,要求立刻出一張 IDR
```

---

## 4. Frame 線路格式(TCP / WebSocket transport 用)

這是 TCP+TLS transport(和未來的 WS+TLS transport)專用的自訂 frame header。
**QUIC transport 不用這整套長度前綴邏輯去框住一串連續位元組流——但 QUIC 的可靠雙向
stream 和每一條單向 stream 裡,仍然是用「同一個 6-byte header 格式」去標頭,
只是不需要靠它在一條 stream 裡切出多筆訊息的邊界(細節看第 5 節)。**

```
offset  長度    欄位              說明
------  ------  ----------------  ------------------------------------------------
0       1 byte  Channel           Channel enum 的數值(0=Control,1=Input,2=Audio,3=Video)
1       1 byte  MessageType       MessageType enum 的數值
2       4 bytes PayloadLength     Int32,小端(little-endian),payload 的 byte 數
6       N bytes Payload           實際資料,長度就是上面的 PayloadLength
```

ASCII 示意圖(header 固定 6 bytes):

```
 byte:   0        1        2    3    4    5        6 .. 6+N-1
       +--------+--------+----+----+----+----+-------------------+
       |Channel |MsgType |     PayloadLength     |    Payload     |
       | (u8)   | (u8)   |     (i32, LE)         |    (N bytes)   |
       +--------+--------+----+----+----+----+-------------------+
```

- `PayloadLength` 一定要用小端序讀寫(`BinaryPrimitives.WriteInt32LittleEndian` /
  `ReadInt32LittleEndian`),這跟一般網路協定常見的大端序不同,**千萬不要搞反**。
- `PayloadLength` 不會是負數;收到負數視為資料損壞(對面程式碼會丟例外)。
- 一條 TCP/TLS 連線上,多筆 Frame 首尾相接直接串流傳送,沒有額外的分隔符——
  完全靠這個 6-byte header 的長度欄位去切出下一筆從哪裡開始。
- 讀取端的邏輯是:先確認 buffer 至少有 6 bytes 才能讀 header;算出
  `HeaderSize(6) + PayloadLength` 這個總長度,buffer 不夠就等更多資料到來
  (`TryParse` 回傳 false,不消耗任何 byte)。
- `WireSize = 6 + Payload.Length`。

Swift 端實作建議:用一個簡單的 state machine(先讀 6 bytes header,解析出
payload 長度,再讀那麼多 bytes 的 payload)去包一層在 `NWConnection`/`InputStream`
上,跟 C# 這邊的 `FrameStreamPump` 做一樣的事即可。

---

## 5. QUIC transport 的訊息邊界規則

QUIC 連線上的 ALPN 固定是 **`rdpb/1`**(`SslApplicationProtocol`)。

QUIC 連線內部分成兩種 stream:

### 5.1 可靠雙向 stream(Control + Input)

- 整條連線**只有一條**這種 stream,由 **Client 主動開**(`OpenOutboundStreamAsync(Bidirectional)`)。
- 這條 stream 上跑的訊息格式跟 TCP 完全一樣:連續的 `[Channel][MsgType][PayloadLength LE][Payload]`
  frame 串流,因為 QuicStream 本身就是 .NET 的 `Stream`,直接重用同一套讀寫邏輯
  (`FrameStreamPump`)。
- `Control` 和 `Input` 這兩個 channel 的所有訊息都走這條 stream。

### 5.2 Priming Hello frame(必須做,否則會卡死)

QUIC 的 stream 要**真的寫過資料**才會在對面出現——單純呼叫
`OpenOutboundStreamAsync` 只是本地配置一個 stream id,不會送出任何 STREAM frame,
對面完全不知道這條 stream 存在。

所以 Client 開完那條可靠雙向 stream 後,**必須立刻**送一筆
`Frame(Channel.Control, MessageType.Hello, 空 payload)` 的 bytes(6 bytes header,
payload 長度 0),把這條 stream「打」到對面去。如果不送這個 priming frame,
Host 端的 `AcceptInboundStreamAsync` 會一路卡到 idle timeout。

Host 端收到第一條 inbound stream 後:
1. 驗證它是 **Bidirectional**(不是就直接拋錯/關連線——代表第一條 stream 不符預期)。
2. **讀掉**這筆 priming Hello frame(讀 6-byte header,算出 payload 長度,把 payload
   也讀掉丟棄),**不會把它當成一筆應用層訊息往上送**(不會觸發 `MessageReceived`)。
3. 之後才把這條 stream 正式當成「可靠雙向 stream」繼續用 `FrameStreamPump` 讀寫。

> Host 端**不會回送**對等的 Hello——priming 只是 Client 單方向要做的事,因為只有
> Client 主動開 stream。如果你的 Swift client 是去實作 Client 端,這一步是必需的;
> 如果哪天要實作 Host 端(目前沒有這個需求),則要照上面三步處理第一條 inbound stream。

### 5.3 每筆 Audio/Video 訊息走獨立單向 stream

- `Channel.Audio` 和 `Channel.Video`(可丟棄的兩個 channel)**每一筆訊息都開一條
  新的單向 stream**(`OpenOutboundStreamAsync(Unidirectional)`),不會重複使用。
- 這條 stream 裡的內容格式跟 TCP 的 Frame **header+payload 完全一樣**
  (先 6-byte header,再 payload),只是**整條 stream 從頭到尾只放這一筆訊息**,
  寫完 payload 後立刻呼叫 `CompleteWrites`(對應程式碼是
  `WriteAsync(ReadOnlyMemory<byte>.Empty, completeWrites: true, ct)`),關掉這條
  stream 的寫入方向。讀取端一樣是先讀 6-byte header 拿到 payload 長度,再讀那麼多
  bytes,讀完就把整條 stream dispose 掉。
- **過期丟棄的實作方式**:每個 channel 維護一個「尚在飛行中的 stream」佇列,新開一條
  就塞進佇列;佇列長度超過該 channel 的 `QueueDepth` 門檻(Video=2,Audio=4,
  見第 2 節)時,把佇列最前面(最舊)的那條 stream 呼叫 `Abort(Write, 0)` 直接中止
  寫入——**不會等它寫完**,所以收不到一筆完整訊息,讀取端那條 stream 的
  `ReadExactAsync` 會失敗並整筆忽略(不影響其他 stream、不影響可靠 stream)。
  這正是 QUIC 相對 TCP 的優勢:一條 stream 卡住不會拖累其他 stream。
- 收到的單向 stream(`AcceptInboundStreamAsync` 回傳 Unidirectional 類型)都丟進
  一個背景 task 個別處理;收到 Bidirectional 類型的「第二條」inbound stream 目前
  **直接關掉不處理**(協定上不支援第二條可靠雙向 stream)。

### 5.4 Stream 數量上限設定值

Client 端 `QuicClientConnectionOptions` 目前設定:

```
MaxInboundBidirectionalStreams  = 4     // 預留餘裕,目前實際只會用到 0 條(對面不會開雙向 stream 回來)
MaxInboundUnidirectionalStreams = 256   // 給對面大量的 Audio/Video 單向 stream 用
```

這兩個值是**連線發起端（Client）**設定的值,代表「我願意接受對面開多少條 inbound
stream」。Host 端的 `QuicListener`/`QuicServerConnectionOptions` 要設定對稱的值
(本文件讀到的檔案裡沒有看到 Host 端 listener 的設定碼,**需要再確認** Host 側是否
也設了一樣寬的門檻——如果 Host 設太小,可能會讓 Host→Client 方向的 Audio/Video
stream 卡住)。如果你是寫 Client 端,照抄上面兩個值即可;**不要設太小**,否則
在高 fps/高音訊幀率時會因為同時飛行中的 stream 數量超過上限而連線被拒。

---

## 6. 自動選擇邏輯(Auto transport selection)

`TransportMode` 有四種:`Auto`、`Direct`、`Tcp`、`Ws`。

- `shouldTryQuic = (沒有代理) && (mode 是 Auto 或 Direct)`。
- 如果 `shouldTryQuic` 成立:
  1. 用 **1.5 秒**的 timeout 嘗試 QUIC 連線(`QuicTransport.ConnectAsync`)。
  2. 成功就回傳 QUIC transport。
  3. 失敗(逾時或任何例外)時,**不管原本是 Auto 還是 Direct,兩者程式碼目前的行為
     是一樣的**:都會往下退回到 TCP+TLS(DIRECT,proxy 傳 `null`),並把失敗原因記在
     `FallbackReason` 欄位給診斷用。原始碼裡的註解把兩者寫成「Auto 自動退回」vs
     「Direct 直接往下走 TCP」兩種說法,但實際行為是同一段 fallback 邏輯——這點
     如果你的 Swift 實作要精確模仿,可以直接理解成「QUIC 失敗就一定退回 TCP」,
     不用在 Auto/Direct 之間做差異化處理。
- 如果 `shouldTryQuic` 不成立(**有代理**,或 mode 是 `Tcp`):
  - mode 是 `Ws` → **目前會直接丟 `NotSupportedException`,WebSocket+TLS transport
    還沒有實作**(見第 15 節)。
  - 其他情況 → 直接走 `TcpTlsTransport.ConnectAsync`,把 `ProxyOptions` 原封不動帶進去
    (有代理就是走代理,沒代理就是 DIRECT TCP)。
- **有代理時一律不嘗試 QUIC**:因為 QUIC 是 UDP,一般 SOCKS5/HTTP CONNECT 代理不轉發
  UDP,所以只要 `ProxyOptions` 不是 null,就只會走 TCP(或未來的 WS)。
- `TransportMode.Direct` 的語意是「即使之後要支援 Ws,也絕對不能帶代理設定進來」——
  程式碼裡有明確檢查,`Direct` 模式搭配非 null 的 `ProxyOptions` 會直接丟例外。

---

## 7. 代理支援(SOCKS5 / HTTP CONNECT)

- `ProxyOptions` 記錄:`Kind`(`Socks5` 或 `HttpConnect`)、`Host`、`Port`,外加
  `Socks5Credentials`(SOCKS5 認證用)或 `HttpProxyAuthorizationHeader`(HTTP CONNECT
  的 `Authorization` header 字串)。
- 實際握手邏輯是**標準協定**(`Socks5Client.ConnectAsync` / `HttpConnectClient.ConnectAsync`),
  走的是一般 SOCKS5 CONNECT 流程和標準 HTTP `CONNECT host:port` 方法——這份文件不重覆
  完整規格,因為 iOS/macOS 上有現成的函式庫(`Network.framework` 的 proxy 設定、
  或第三方 SOCKS5 函式庫)可以直接用,不需要你手刻 bytes。
- 代理握手是在**裸 TCP socket 連線建立之後、TLS 開始之前**做的(先連到代理伺服器,
  跟代理談好要轉去 `host:port`,代理回應 OK 之後,接下來才在這條 socket 上開始跑
  TLS handshake 連到真正的目標)。
- 有代理時,§8 的 RDP 偽裝**不會套用**(只在 DIRECT 模式生效,見下一節),代理握手
  完成後直接進 TLS。

---

## 8. §7 RDP 偽裝(只在 DIRECT 模式生效)

> 這一節對應程式碼裡 `RdpCamouflageObfuscator`,目的是讓 DPI/防火牆在 TCP 連線剛建立
> 時看到的 bytes 長得像一般的 RDP(MS-RDPBCGR)連線,藉此混過只檢查埠號和握手開頭的
> 被動式封包檢測。**這不是真的在跑 RDP 協定**,後面完全不會有真正的 RDP 流程,純粹是
> 交換這幾個 bytes 當偽裝,交換完就直接切進 TLS。擋得住多數被動 DPI,擋不住會完整模擬
> 一次真實 RDP 流程的主動探測——原始碼註解裡就講得很清楚,這裡不誇大效果。

### 時機

TCP socket 連上之後(**如果有代理,代理握手也已經完成之後**),**TLS handshake
開始之前**:

1. Client → Host:送出一個偽裝的 **Connection Request**。
2. Host → Client:回送一個偽裝的 **Connection Confirm**。
3. 雙方都把這幾個 bytes 從 stream 讀乾淨(不解析內容,只是把偽裝用的位元組清掉,
   避免殘留在緩衝區干擾接下來的 TLS handshake)。
4. 接著直接用同一條 socket 開始正常的 TLS handshake(`SslStream.AuthenticateAsClient/ServerAsync`)。

**只有 DIRECT(無代理)模式才會做這一步**;有代理時完全跳過,直接進 TLS。

### Connection Request(Client → Host)的精確 bytes 格式

整體是一個 **TPKT frame**,裡面包一個 **X.224 Connection Request(CR)**:

```
TPKT header(4 bytes,大端序):
  byte 0:     0x03                     // TPKT version,固定值
  byte 1:     0x00                     // reserved,固定 0
  byte 2-3:   totalLen (big-endian u16) // 整個 TPKT frame 的總長度(含這 4 byte header)

X.224 部分(緊接在 TPKT header 後面):
  byte 0:     LI = (6 + variable 長度)  // X.224 的 length indicator,1 byte
  byte 1:     0xE0                      // CDT/CR code = Connection Request
  byte 2-3:   0x00 0x00                 // DST-REF,固定 0
  byte 4-5:   0x00 0x00                 // SRC-REF,固定 0
  byte 6:     0x00                      // CLASS,固定 0
  (以上 byte1~6 共 6 bytes 是「X.224 固定部分」)

Variable 部分(緊接在 X.224 固定部分後面):
  ASCII 字串: "Cookie: mstshash=RDPB\r\n"   // 23 bytes,routing cookie
  RDP_NEG_REQ block(8 bytes,見下方共用格式),type=0x01,protocol=0x00000001(PROTOCOL_SSL)
```

`RDP_NEG_REQ` / `RDP_NEG_RSP` 共用的 8-byte 格式:

```
offset  長度   欄位        值
0       1      type        REQ 用 0x01,RSP 用 0x02
1       1      flags       固定 0x00
2-3     2      length      固定 8(小端序存,bytes 是 0x08 0x00)
4-7     4      protocol    PROTOCOL_SSL = 0x00000001,小端序存
                            (bytes 是 0x01 0x00 0x00 0x00)
```

> 注意:這個 8-byte block 內部的 `length` 和 `protocol` 欄位是**小端序**,
> 但外層 TPKT header 的 `totalLen` 是**大端序**——兩層用不同的 byte order,
> 務必對照上面表格逐欄確認,不要套用同一個 endianness 假設。

### Connection Confirm(Host → Client)的精確 bytes 格式

結構跟 Request 幾乎一樣,差異只在 X.224 code 和 variable 部分:

```
TPKT header:同上格式(4 bytes)

X.224 固定部分(6 bytes):
  byte 0: 0xD0   // CDT/CR code = Connection Confirm(跟 Request 的 0xE0 不同)
  byte 1-6: 同上,全部 0x00

Variable 部分:
  只有 RDP_NEG_RSP block(8 bytes,格式同上表),type=0x02,protocol=0x00000001
  (沒有 cookie 字串,Confirm 不帶 cookie)
```

### 讀取規則(雙方共用)

讀一個完整 TPKT frame 的邏輯:先讀 4 byte header,`header[0]` 必須是 `0x03`
(否則視為對面不是合法偽裝、連線可能被動過,直接丟例外中止),
`totalLen = (header[2] << 8) | header[3]`(大端序 16-bit),合法範圍是
`4 <= totalLen <= 4096`,然後再讀 `totalLen - 4` bytes 把剩下的部分讀完。

Swift 端實作提示:這整段偽裝邏輯只需要在「DIRECT 模式」時,TCP 連上之後,TLS
開始之前跑一次;如果你一開始先只做「走代理」或「不需要偽裝」的版本,可以先跳過
這一節,之後再補。

---

## 9. 配對協定(Pairing)

> **這裡要特別老實說:程式碼裡只有「怎麼算 PIN / 指紋 / HMAC」這一層數學邏輯
> (`PairingService`),完全沒有看到把 `MessageType.Pair` / `MessageType.PairAck`
> 實際序列化成 bytes、送上 Control channel、在 Session 裡接收處理的程式碼。
> 也就是說,配對這個「協定」目前只存在計算層,**還沒有串成一個端到端的網路流程**。
> 下面描述的是「已經確定的計算邏輯」部分,Pair/PairAck 的 payload bytes 格式
> 需要你自己設計(或等 C# 端補上後再對齊)。**這是目前最大的缺口之一。****

### 已確定的計算邏輯

1. **PIN**:Host 產生一個 6 位數字字串(`"D6"` 格式,保留前導零,範圍
   `000000`~`999999`,用 `RandomNumberGenerator.GetInt32(0, 1_000_000)`)。
   有效期由呼叫端自己控管,程式裡的計畫是 5 分鐘(這個 5 分鐘本身目前也只是
   註解裡提到的「計畫」,沒有看到實際的逾時執行程式碼)。
2. **憑證指紋**:`SHA-256(cert.RawData)`——即憑證的**原始 DER bytes**做 SHA-256,
   **不是** Subject/公鑰/有效期等會變動的欄位,單純整份 DER 編碼的雜湊。
3. **配對用 HMAC**:
   ```
   key     = UTF8 bytes of PIN 字串(例如 "042817" 的 UTF8 bytes,共 6 bytes)
   message = hostFingerprint bytes (32 bytes) ++ clientFingerprint bytes (32 bytes)
             (host 的指紋在前,client 的指紋在後,直接串接,沒有長度前綴或分隔符)
   tag     = HMAC-SHA256(key, message)   // 32 bytes 輸出
   ```
   雙方各自拿到對方的憑證指紋後,各自算一次這個 HMAC,比對是否一致(用常數時間比較
   `CryptographicOperations.FixedTimeEquals`,避免 timing attack)完成配對驗證。
   因為兩份指紋都放進訊息裡,中間人換掉任何一邊的憑證,雙方算出的值就會不一致,
   抓得到竄改。

### 配對完成後要記住的東西

`PinnedHost` 記錄:`Name`、`HostAddress`、`CertificateFingerprint`(32 bytes)。
這是 Client 端配對成功後要存進本機設定(程式裡提到是 `config.json` 那種檔案)的
資料,之後重新連線時,TLS 的 `RemoteCertificateValidationCallback`
直接拿連線時收到的憑證指紋跟這個存起來的指紋比對,一致才放行——**不需要再輸入
PIN**。

### mTLS 後續怎麼驗證(需要再確認的部分)

程式碼裡 `SslClientAuthenticationOptions`/`SslServerAuthenticationOptions` 的
`RemoteCertificateValidationCallback` 只回傳一個 `bool`(呼叫端傳進來的
`validateServerCertificate` 函式決定),這份文件讀到的檔案範圍裡:
- **沒有看到** Host 端要求 Client 提供憑證的設定(`ClientCertificateRequired`
  之類的 Server 端 TLS 選項),也**沒有看到** Client 端在 `SslClientAuthenticationOptions`
  裡附上自己的 `ClientCertificates`。
- 註解裡提到「之後 Client 釘選(pin)這個指紋,連線就是 mTLS + 指紋比對」,
  但 mTLS(雙向憑證驗證)實際在哪個檔案設定、Client 端的憑證從哪裡產生/儲存,
  **都需要再確認**(可能在這批檔案之外的 Host/Client 啟動邏輯裡,沒有包含在
  本次讀取的檔案清單中)。
- 可以確定的只是:每一方都有自己的 X.509 憑證,連線時雙方互相看到對方的憑證,
  用上面的指紋公式算出來比對——**驗證的落地細節建議直接去問專案擁有者或等對應的
  Host/Client 啟動程式碼補上後再核對**,不要自己假設一個流程就動手做。

---

## 10. 心跳與斷線判斷(Session)

- **心跳間隔**:預設 **500ms** 送一次 `Frame(Channel.Control, MessageType.Heartbeat, 空 payload)`。
- **逾時判斷**:如果目前狀態是 `Active`,且「現在時間 - 上次收到 Heartbeat 的時間」
  超過 **2 秒**,就判定斷線,進入 `Reconnecting` 狀態。
- **收到對方的 Heartbeat** 時:更新「上次收到時間」;如果目前是 `Reconnecting`
  狀態,收到一筆就立刻視為恢復,切回 `Active`。
- **狀態機**:`Connecting → Active → Reconnecting → Closed`(或 `Active → Closed`,
  或 `Reconnecting → Active`)。進入 `Reconnecting` 後,有一個 **30 秒**的
  reconnect window,期間如果沒有變回 `Active`,就會自動轉成 `Closed`
  (`Reconnecting` 狀態每 200ms 檢查一次是否超過這個 deadline)。
- **transport 層斷線事件**(TCP/QUIC 任何一個底層連線掛掉)也會直接觸發跟逾時
  一樣的「進入 Reconnecting 或 Closed」流程。

### ReleaseAll 語意

- 不管是「心跳逾時」還是「transport 斷線」,**一偵測到就立刻**呼叫
  `KeyStateTracker.ReleaseAll()`,拿到一份目前「還按著哪些鍵/滑鼠按鈕」的快照
  (`ReleaseSnapshot`),透過 `KeyReleaseRequested` 事件往外送——**不等重連結果
  出來就先做這件事**(對應計畫裡的「雙保險放開按鍵」)。
- 快照不是空的(`!snapshot.IsEmpty`)才會真的觸發事件。
- 呼叫端拿到這份快照後要做的事:
  - **Host 端**(`Injector`):把快照裡每個按著的 scan code 都送一次
    `SendKey(scanCode, down: false, extended: false)`,每個按著的滑鼠按鈕都送一次
    `MouseBtn(button, down: false)`——**實際放開 Windows 層級的按鍵狀態**。
  - **Client 端**(`InputCaptureController.Deactivate`):除了本機放開 hook/clip
    cursor 之外,還會把快照裡的 Key/MouseBtn 事件**各自封裝成一筆訊息送給 Host**
    (payload 裡 flags=0 代表放開,見第 11 節),並且如果快照不是空的,**額外多送
    一筆 `MessageType.ReleaseAll`(空 payload)給 Host**,讓 Host 端也走一次自己的
    `Injector.ReleaseAll()`(雙保險,即使前面那幾筆 Key/MouseBtn 訊息路上丟了,
    Host 收到這筆 `ReleaseAll` 還是會自己清掉自己追蹤到的按鍵狀態)。
  - 任何一方收到 `MessageType.ReleaseAll` 訊息時,`Session.OnMessageReceived`
    都會直接呼叫本地的 `RequestKeyRelease()`(不會把這筆訊息往上送給應用層的
    `MessageReceived` 事件,是 Session 內部直接處理掉的)。
- `Session.Rebind(newTransport)`:重連成功後,呼叫端用新的 transport 換掉舊的,
  `Session` 狀態（不用重新配對）繼續沿用,直接變回 `Active`。

---

## 11. Input channel 各訊息的精確 payload byte layout

> 下面每一種都用 Host 端 `Injector.Handle`(接收端解碼)和 Client 端
> `InputCaptureController`(傳送端打包)**兩邊對照過**,目前**完全一致**,
> 沒有發現 offset 不符的地方。

### `Key`(MessageType = 10)— payload 至少 3 bytes

```
offset  長度    欄位         說明
0-1     2       scanCode    u16,小端序(byte0 | byte1<<8),鍵盤 scan code
2       1       flags       bit0 = down(1=按下,0=放開)
                             bit1 = extended(1=extended key,例如右邊的 Ctrl/Alt、方向鍵等)
                             其餘 bit 未使用
```
Client 端打包範例:`[scanCode & 0xff, scanCode >> 8, (down?1:0) | (extended?2:0)]`。
放開時(`Deactivate` 送的 release 訊息)flags 固定是 `0`(down=0, extended=0)。

### `MouseAbs`(MessageType = 11)— payload 至少 4 bytes

```
offset  長度    欄位    說明
0-1     2       x       u16,小端序,絕對座標 x,正規化到 0..65535 範圍
                         (Client 端算法:nx = clamp(rawX * 65535 / (clientWidth-1), 0, 65535))
2-3     2       y       u16,小端序,絕對座標 y,正規化方式同上(用 clientHeight)
```

### `MouseRel`(MessageType = 12)— payload 至少 4 bytes

```
offset  長度    欄位    說明
0-1     2       dx      i16,小端序,相對位移 x(來自 Raw Input 的 lLastX,clamp 到 i16 範圍)
2-3     2       dy      i16,小端序,相對位移 y(來自 Raw Input 的 lLastY,clamp 到 i16 範圍)
```

### `MouseBtn`(MessageType = 13)— payload 至少 2 bytes

```
offset  長度    欄位     說明
0       1       button   u8,按鈕編號:0=左鍵,1=右鍵,2=中鍵
                         (對應 WM_LBUTTONDOWN/WM_RBUTTONDOWN/WM_MBUTTONDOWN)
1       1       down     u8,非 0 表示按下,0 表示放開(接收端判斷是 `payload[1] != 0`)
```

### `Wheel`(MessageType = 14)— payload 至少 3 bytes

```
offset  長度    欄位         說明
0-1     2       delta       i16,小端序,滾輪位移量(直接取 WM_MOUSEWHEEL 的 wParam 高 16 bit,
                             不做額外縮放)
2       1       horizontal  u8,非 0 表示水平滾輪,0 表示垂直滾輪
```
**要注意的現況**:目前 Client 端(`InputCaptureController`)**只處理了 `WM_MOUSEWHEEL`
(垂直滾輪)**,送出的 `horizontal` byte **固定是 `0`**。沒有看到任何處理
`WM_MOUSEHWHEEL`(水平滾輪)的程式碼。所以雖然 payload 格式上預留了 `horizontal`
欄位,**目前的 C# Client 實際上永遠不會送出 `horizontal=1` 的 Wheel 訊息**——
你的 Swift client 如果要支援觸控軌跡板的水平滑動,protocol 上這個欄位是可以用的,
只是對面現有 Client 沒用到。

### `Pad`(MessageType = 15)— **目前完全沒有實作,沒有任何 payload 格式可查**

`Pad` 在 `MessageType` enum 裡存在(值是 15),但:
- `Injector.Handle` 的 `switch` 沒有 `case MessageType.Pad`,收到這個訊息類型
  什麼都不會發生(直接落到 switch 末端,不處理)。
- `InputCaptureController` 沒有任何程式碼會送出 `MessageType.Pad`。

**這是一個完全空白的協定欄位。** 如果你的 Swift client 需要支援手把輸入,
payload 格式需要你自己定義(或者等 C# 端補上實作後對齊),目前**沒有任何
既有 bytes 格式可以參考**——不要猜測格式,這裡只能寫「需要再確認」。

---

## 12. Audio channel payload 格式

`MessageType.AudioFrame`(channel = `Audio`)的 payload:

```
offset  長度     欄位        說明
0-1     2        seq         u16,小端序,序號(byte0 | byte1<<8)
2..     N        opusData    Opus 編碼後的原始資料,長度 = payload 總長 - 2
```

- `HeaderSize = 2`。打包:`buf[0] = seq & 0xff; buf[1] = seq >> 8;` 後面接 Opus bytes。
- **序號繞回規則**:16-bit 序號,繞回時用「unchecked ushort 減法」去判斷先後順序
  (`expected = lastSeq + 1`(自動 wrap),`diff = (ushort)(seq - expected)`,
  這個 diff 的結果範圍是 `0..65535`,利用無號整數減法的繞回特性安全處理 wraparound)。
- **接收端判斷邏輯**(這是 C# 參考實作裡 `AudioSequenceTracker` 的演算法,
  **純粹是接收端自己的事,不是協定本身強制要求的行為**——Swift 端可以照抄這個演算法,
  也可以自己設計別的掉包處理策略,只要能正確解讀 seq 欄位即可):
  - `diff == 0`:序號跟期望值一致,正常播放(`PlayNormally`)。
  - `diff` 在 `1..10`(`MaxConcealFrames = 10`)之間:中間掉了 `diff` 幀,
    先用 Opus 的 PLC(packet loss concealment)補 `diff` 幀,再解碼播放這一幀
    (`ConcealThenPlay`,`ConcealFrameCount = diff`)。
  - `diff >= 65536 - 1000`(也就是非常接近「繞回前」的範圍,`OutOfOrderThreshold = 1000`):
    視為舊封包亂序到達或重複封包,**整包丟棄**,不更新序號追蹤狀態(`Drop`)。
  - 其餘情況(掉了超過 10 幀但沒有落在上面「太舊」的範圍):掉太多幀,補下去沒意義,
    直接接上新的序號當作重新同步,不做 PLC 補償(`PlayNormally`,`ConcealFrameCount = 0`)。
  - 20ms 一幀是這個邏輯的假設前提(10 幀 PLC 上限 = 200ms,再補下去聽起來比真的
    斷線還怪,所以設這個上限)。
- **Opus PLC 要不要用、怎麼呼叫,是 Swift client 自己的事**,協定層只保證「給你
  一個可以判斷順序/掉包的序號欄位」,不規定解碼端一定要做 PLC。

---

## 13. Video channel payload 格式

`MessageType.VideoFrame`(channel = `Video`)的 payload:**原始 H.264 Annex-B
bytestream,一個 packet = 一個完整的編碼 frame**。

- Host 端的編碼管線(`CaptureEncoder`)用 FFmpeg/libavcodec,依序嘗試
  `h264_nvenc → h264_amf → h264_qsv → libx264`,第一個 `avcodec_open2` 成功的
  就用(`SelectedEncoder` 可查到實際選中的是哪一個)。
- `EncodedPacket { Data, IsKeyframe, Pts }`:`Data` 就是 `AVPacket` 的原始 bytes
  (`avcodec_receive_packet` 吐出來的那一包,逐 byte複製成 `byte[]`,沒有再做任何
  額外的包裝/切割),`IsKeyframe` 來自 `(pkt->flags & AV_PKT_FLAG_KEY) != 0`。
- **關於「這個 EncodedPacket 到底怎麼變成一筆送上 Video channel 的訊息」**:
  在本次讀取的檔案範圍裡,**沒有看到**把 `CaptureEncoder.PacketEncoded` 事件接去
  `Session.SendAsync(Channel.Video, MessageType.VideoFrame, ...)` 的那段整合程式碼
  (應該是在沒有列入本次讀取清單的 Host 主程式/`HostSession` 類別裡)。所以:
  - **需要再確認** `IsKeyframe` 這個資訊有沒有被放進 payload 裡(例如前面加一個
    額外的 1-byte flag),還是完全不送、靠 Client 自己從 Annex-B bytestream 裡
    解析 NAL unit type 去判斷這是不是 IDR/keyframe。從目前看到的程式碼風格推測,
    **比較可能是後者**(payload 就是純 bytestream,keyframe 資訊靠解析 NAL header
    自己判斷),但這只是推測,**不是確認過的事實**,實作前請跟專案擁有者確認一次。
- **SPS/PPS inline 有沒有**:程式碼裡**沒有特別處理** SPS/PPS 的插入時機
  (沒有看到額外呼叫 `h264_mp4toannexb` 之類的 bitstream filter,也沒有手動插入
  SPS/PPS NAL 的邏輯)。`ctx->gop_size = int.MaxValue`(無限 GOP,只有明確要求時
  才插入 IDR),NVENC 那條路徑有另外開 `forced-idr=1` 這個選項(沒開的話,
  `RequestKeyframe()`/送 `AV_FRAME_FLAG_KEY` 完全沒有效果——這是原始碼註解裡特別
  點出的一個實測踩到的坑)。**假設是 nvenc/各硬體編碼器的預設行為**:一般來說
  H.264 編碼器在每個 IDR 前面都會重新帶一次 SPS/PPS(這是業界常見預設),但這份
  文件**沒有在程式碼裡實際驗證看到的 bytestream 是否每個 IDR 都重複帶 SPS/PPS**,
  如果你的 Swift 端解碼器(`VideoToolbox`)對 SPS/PPS 缺漏敏感,建議第一次實測時
  用封包截圖工具(Wireshark 之類)直接核對收到的 bytes,不要完全依賴這份文件的推測。
- **IDR 請求**:Client 偵測到連續丟幀/畫面壞掉時,送
  `Frame(Channel.Video, MessageType.VideoKeyframeRequest, 空 payload)` 給 Host,
  Host 端邏輯上對應呼叫 `CaptureEncoder.RequestKeyframe()`(下一幀強制編成 IDR)。
  這段「收到 VideoKeyframeRequest 之後呼叫 RequestKeyframe」的實際接線程式碼同樣
  **沒有列在本次讀取的檔案清單裡**,是從 enum 命名和既有的 `RequestKeyframe()`
  方法合理推斷出來的語意,邏輯上應該就是這樣接的,但建議對照原始碼確認一次。
- **自動丟幀後的 keyframe 語意**:`Video` channel 佇列深度門檻是 2
  (第 2 節),佇列滿了丟最舊的幀時,TCP transport 的 `FrameDropped` 事件
  (攜帶 `(Channel, int)`)和 QUIC transport 的對應事件都會觸發——Host 端收到
  這個事件的慣例是立刻要求編碼器出一張 IDR(對應 `CaptureEncoder.RequestKeyframe`),
  因為舊幀被丟掉,解碼端的參考畫面鏈已經斷了,繼續送 P-frame 沒有意義。

---

## 14. fps 允許值集合

```
FrameRateOptions.AllowedValues = [30, 45, 60, 90, 120, 144]
FrameRateOptions.Default       = 60
```

GUI 的下拉選單跟 `CaptureEncoder.Start(fps, ...)` 的參數驗證共用這份清單,傳入
不在這個集合裡的值會直接丟 `ArgumentOutOfRangeException`。如果設定交換協定
(`ConfigSync`,目前 payload 格式未定)要帶 fps 設定,Swift 端也應該只允許這
六個值裡面選。

---

## 15. 這份文件目前沒覆蓋、但 Swift 端實作可能需要知道的事(老實列出的缺口)

1. **協定版本協商完全不存在**(見第 0 節)——沒有版本欄位,沒有相容性檢查機制,
   這是目前整個協定最大的結構性缺口,建議你自己的實作至少預留一個版本欄位。
2. **配對(Pairing)的網路層流程沒有串起來**(見第 9 節)——`Pair`/`PairAck`
   這兩個 `MessageType` 存在,PIN/指紋/HMAC 的計算邏輯都寫好了,但**沒有看到
   把這套計算邏輯包成實際 wire bytes、送上 Control channel、在 Session 裡
   接收解析的程式碼**。這部分的 payload 格式(例如 Pair 訊息裡 PIN 怎麼帶、
   要不要連著憑證指紋一起送、HMAC tag 放在 Pair 還是 PairAck)都**需要再確認**,
   不要自己猜一個格式就刻進 Swift client 裡,先跟專案擁有者對一次。
3. **mTLS 的實際落地細節沒有在這批檔案裡**(見第 9 節)——Server 端要不要求
   Client 憑證、Client 端的憑證怎麼產生/存放,都需要再確認。
4. **`ConfigSync` 和 `Bye` 的 payload 格式完全沒看到**(見第 3 節)——這兩個
   `MessageType` 存在,但本次讀取的檔案裡沒有任何序列化/反序列化程式碼。
5. **`Pad`(手把輸入)完全沒有實作**(見第 11 節)——enum 值存在,Host/Client
   都沒有程式碼處理它,payload 格式是空白,需要重新設計。
6. **Video channel 的 keyframe 資訊怎麼過線、`VideoKeyframeRequest` 怎麼接到
   `RequestKeyframe()`,這兩段整合程式碼都不在本次讀取範圍內**(見第 13 節),
   是從命名和既有方法合理推斷出來的,建議實測核對一次封包內容。
7. **(已過時,2026/10/31 更正)WebSocket+TLS transport 其實已經實作完了**——
   `WsTransport`(Client 端連線 + Server 端 `ServerUpgradeHandshakeAsync`)和
   `WsTransportListener`(Host 端監聽,見 `HostListenerService.Start` 裡的
   `_wsListener`)兩邊都有,走 RFC 6455 §4.1 最小可用的 HTTP Upgrade 交握,
   交握完用 BCL 的 `WebSocket.CreateFromStream` 接手 framing。本節原本寫
   「`TransportMode.Ws` 直接丟 `NotSupportedException`」是這份文件沒有跟上
   程式碼進度,不是現在的真實行為——第 6 節那段敘述也一併過時,實際上
   `TransportConnector.ConnectAsync` 的 `TransportMode.Ws` 分支會真的呼叫
   `WsTransport.ConnectAsync`。Swift client 如果要支援 WS,可以直接照
   `WsTransport.cs`/`WsTransportListener.cs` 的程式碼對,不用從零設計格式。
8. **Host 端 QUIC listener 的 `MaxInboundBidirectionalStreams`/
   `MaxInboundUnidirectionalStreams` 設定沒有在本次讀取的檔案裡看到**
   (只看到 Client 端的設定值,見第 5.4 節),需要再確認 Host 端是否對稱設定。
9. **GUI 設定檔格式(config.json 之類)尚未定案**——`PinnedHost` 記錄只是「要存
   什麼欄位」的形狀,實際檔案格式/路徑/序列化方式沒有在這批檔案裡看到。
10. **PIN 的 5 分鐘有效期、Host 顯示 PIN 的 UI 流程**,目前只存在於程式碼註解裡
    提到的「計畫」,沒有看到實際執行逾時/顯示的程式碼。
11. **心跳/斷線期間,舊訊息(例如還在 QUIC 單向 stream 飛行中的音訊/影像幀)
    要不要在 Rebind 之後重送**,目前 `Session.Rebind` 只是換 transport、重置
    心跳計時,沒有看到任何「補送遺失訊息」的邏輯——重連後的 Video 應該靠
    `VideoKeyframeRequest` 要一張新 IDR 來處理,但這不是自動發生的,**需要
    呼叫端自己在 Rebind 之後主動送一次 keyframe request**,這份文件沒看到
    現有程式碼會自動做這件事。

---

*本文件依 2026-10-29 當時讀取到的 `RdpB.Core`/`RdpB.Host`/`RdpB.Client` 原始碼內容撰寫。
之後如果協定有變動(尤其是補上版本協商、配對網路流程、ConfigSync/Bye payload 這幾塊),
務必重新核對原始碼後更新本文件,不要單靠記憶修改。*
