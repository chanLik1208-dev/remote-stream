using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RdpB.Core.Media;
using RdpB.Core.Transport;

namespace RdpB.Core.Windows;

/// <summary>一台已配對 Host 的記錄。指紋是公開資訊的雜湊,不是密鑰,明文存沒關係(計畫 §8)。</summary>
public sealed class PinnedHostRecord
{
    public string Name { get; set; } = "";
    public string HostAddress { get; set; } = "";
    public string FingerprintHex { get; set; } = "";
    public DateTimeOffset LastConnected { get; set; }

    /// <summary>「預匹配模式」用的旗標,**預設 false(=舊行為不變,安全預設)**:
    /// <see cref="FingerprintHex"/> 非空且這個旗標是 false,代表指紋是「真的配對成功後」填上去
    /// 的,下次連線直接信任、不問 PIN——跟這個欄位出現以前的行為完全一樣,舊版 config.json
    /// 沒這個欄位也會因為預設值是 false 而自動符合這個情況,不用寫遷移程式碼。
    ///
    /// 只有一種情況要顯式設成 true:使用者透過自己信任的管道(讀 Host 螢幕上顯示的指紋、
    /// 電話告知……)**事先**輸入了 Host 的指紋,還沒真的配對過。這種情況下
    /// <see cref="FingerprintHex"/> 非空,但 Host 端還不認得這個 Client,PIN 配對還是要走
    /// 一次——跟一般初次連線的差別只在於:TLS 層的憑證驗證從第一次連線就會嚴格比對這個
    /// 預先填好的指紋,不是 TOFU 先接受任何憑證再談 PIN(關掉「第一次連線時中間人剛好能
    /// 插進來換掉憑證」這個 TOFU 固有的破綻)。配對成功後會自動設回 false。
    ///
    /// 故意設計成「不設就是安全/舊行為」,不是「不設就會跳過驗證」——任何忘記設這個欄位的
    /// 呼叫端(包括還沒更新的測試或之後新寫的程式碼),都只會落回最保守的舊行為,不會不小心
    /// 把什麼都還沒驗證過的連線當成已經配對。</summary>
    public bool PinPairingPending { get; set; } = false;

    /// <summary>每台 Host 可以分開設(計畫 §8),沒設就沿用 <see cref="RdpBConfig.Default"/> /
    /// <see cref="RdpBConfig.Quality"/>。</summary>
    public TransportSettings? Overrides { get; set; }
    public QualitySettings? QualityOverrides { get; set; }

    /// <summary>Kyra 要求的功能:「加上密碼的無人值守比較好,每次都有驗證(信任裝置需要
    /// Host 上設定的密碼)」——純指紋釘選(TOFU 配對過一次之後,之後只要憑證指紋對得上就
    /// 直接信任,完全不用再驗證)對「機器旁邊沒有人」的使用情境來說不夠:私鑰只要外洩,
    /// 攻擊者就能永久冒充這台裝置,沒有人在電腦前能察覺。加這個旗標之後,即使是已經信任的
    /// 裝置,每次連線都還要再驗一次 Host 端設定的連線密碼(<see cref="ConnectionPasswordSettings"/>)
    /// ——等於在「這把私鑰是不是本來配對過的那把」之外,多一道「現在這個人是不是真的知道
    /// 密碼」的驗證,兩個條件都成立才放行,私鑰外洩不再是永久性的破口。
    ///
    /// 刻意是 Client 端手動設定的旗標,不是 Host 跟 Client 之間自動協商出來的——Kyra 自己
    /// 在 Host 設定頁開了連線密碼之後,要自己到這台裝置的編輯對話框裡也勾上這個選項,
    /// 兩邊手動對齊(這個方案是給 Kyra 自己兩台機器用的個人工具,不值得為了省這一步手動
    /// 設定去多做一輪協商交握的協定設計)。預設 false,不影響任何沒開這個功能的人。</summary>
    public bool RequiresConnectionPassword { get; set; } = false;
}

public sealed class TransportSettings
{
    /// <summary>底層 Core 的 TransportMode(auto|direct|tcp|ws),GUI 不直接讓人選這個了——
    /// 使用者只看得到「DIRECT / PROXY」兩個選項(見 UX 回饋),選 PROXY 時這裡存 "tcp"
    /// (ws 還沒實作,§6/§7 的偽裝邏輯只認 DIRECT,跟代理互斥本來就不該混在一起選)。</summary>
    public string Mode { get; set; } = "auto"; // auto|direct|tcp|ws

    /// <summary>代理怎麼設定:"subscription"(訂閱連結)或 "configFile"(匯入設定檔)。
    /// **目前只負責存起來,還沒有真的解析訂閱格式或設定檔內容去抽出節點**——那是之後的工作,
    /// 這裡先把 UI 和資料模型做出來。</summary>
    public string ProxySourceKind { get; set; } = "subscription";

    public string? ProxySubscriptionUrl { get; set; }
    public string? ProxyConfigFilePath { get; set; }

    public string? ProxyUrl { get; set; }
    public string? ProxyUsername { get; set; }

    /// <summary>DPAPI 加密後的 base64——**不是明文密碼**(計畫 §8)。讀寫一律經過
    /// <see cref="SetProxyPassword"/>/<see cref="GetProxyPassword"/>,不要直接動這個欄位。</summary>
    public string? EncryptedProxyPasswordBase64 { get; set; }

    [JsonIgnore]
    public string? ProxyPassword
    {
        get => EncryptedProxyPasswordBase64 is null ? null : SecretProtector.UnprotectFromBase64(EncryptedProxyPasswordBase64);
        set => EncryptedProxyPasswordBase64 = value is null ? null : SecretProtector.ProtectToBase64(value);
    }

    /// <summary>§7:只有 DIRECT 模式才有意義——走代理的連線一律不套偽裝(§6)。
    /// GUI 現在連選項都不會顯示在 PROXY 模式下,不只是停用。</summary>
    public bool RdpCamouflageWhenDirect { get; set; } = true;

    /// <summary>Kyra 要求的改動:§7 偽裝原本的設計是「連線一定要打去 3389 這個專用埠」才能
    /// 套用握手外觀,但這個埠常常被 Windows 自己的 Remote Desktop 服務佔用,一旦佔用整個
    /// 偽裝直接連不上(真的在 Kyra 機器上炸出來的坑)。改成預設偽裝握手直接疊在原本要連的
    /// 那個埠上(由 <see cref="TcpTlsTransportListener"/> 自動偵測,不用另外佔一個埠),3389
    /// 變成進階選項——想要「連埠號都跟真正的 RDP 一致」這一點點額外效果、也能接受可能跟
    /// Windows 原生 RDP 服務撞埠風險的人才需要打開。Host 端用這個設定決定要不要額外開
    /// 3389 的監聽器,Client 端用這個設定決定連線時要不要打去 3389 而不是使用者填的那個埠
    /// (兩邊各讀各自機器上的這個值,不是同一份設定互相告知)。</summary>
    public bool RdpCamouflageUseDedicatedPort { get; set; } = false;

    /// <summary>Kyra 要求的開關:DIRECT 模式預設一律先試 QUIC(1.5 秒逾時)才退回 TCP+TLS——
    /// 對會走 frp/DDNS 這種「中間一定有 NAT 或只轉發 TCP」的連線情境來說,QUIC 永遠連不上,
    /// 每次連線都白白多等 1.5 秒才真的開始 TCP handshake。開了這個選項之後,DIRECT 模式直接
    /// 跳過 QUIC 嘗試,但**偽裝(§7)還是照常可以套用**——跟選「透過代理(PROXY)」不一樣,
    /// PROXY 模式選下去偽裝整個不能用(§6/§7 的互斥設計),這裡只是單純「不試 QUIC」,
    /// 連線意圖還是 DIRECT。</summary>
    public bool DisableQuic { get; set; } = false;
}

/// <summary>計畫 §8「畫質」那一類設定。跟 <see cref="TransportSettings"/> 一樣,每台 Host
/// 可以分開設,也有全域預設值。</summary>
public sealed class QualitySettings
{
    private int _fps = FrameRateOptions.Default;

    /// <summary>只能是 <see cref="FrameRateOptions.AllowedValues"/>(30/45/60/90/120/144)其中之一,
    /// 跟 GUI 下拉選單、<c>CaptureEncoder.Start</c> 的驗證是同一份清單。設不合法的值會直接丟例外,
    /// 不會悄悄存進 config.json 之後連不起來才發現。</summary>
    public int Fps
    {
        get => _fps;
        set { FrameRateOptions.Validate(value); _fps = value; }
    }

    public int BitrateKbps { get; set; } = 15_000;
    public string Encoder { get; set; } = "auto"; // auto|h264_nvenc|h264_amf|h264_qsv|libx264
    public string Decoder { get; set; } = "auto"; // auto|d3d11va|sw

    /// <summary>Kyra 要求的畫質縮放功能:擷取到的畫面先降採樣再編碼,解析度越高、軟體編碼
    /// (libx264 fallback,見 CaptureEncoder 上的註解)越吃重,這是最直接能讓它「至少能動」
    /// 的手段。100 = 不縮放(原生解析度)。</summary>
    public int ScalePercent { get; set; } = 100;

    /// <summary>快速/平衡/品質是預設組合,選了其中之一就覆蓋 Fps/BitrateKbps/ScalePercent
    /// 這三個值(見 QualityPresets);"custom" 才讓使用者自己調這三個欄位。存的是這三個值
    /// 本身,不是存「現在選哪個 preset」這件事本身會動態決定行為——preset 只是 GUI 層面
    /// 「幫你填好這三個數字」的捷徑,CaptureEncoder/HostListenerService 完全不知道
    /// preset 的存在,只讀 Fps/BitrateKbps/ScalePercent,邏輯單純。</summary>
    public string Preset { get; set; } = "balanced";
}

/// <summary>計畫外、Kyra 事後要求的功能:快速/平衡/品質三組預設值,外加 custom 代表「使用者
/// 自己調,不套用任何一組」。平衡組的數字刻意跟 QualitySettings 原本的預設值一樣(Fps=60,
/// Bitrate=15000,Scale=100)——這樣舊的 config.json(沒有 Preset 欄位,反序列化會補上預設值
/// "balanced")套用這組預設不會改變任何人現有的實際行為。</summary>
public static class QualityPresets
{
    public const string Fast = "fast", Balanced = "balanced", Quality = "quality", Custom = "custom";

    public static readonly IReadOnlyList<string> All = [Fast, Balanced, Quality, Custom];

    public static bool TryGetValues(string preset, out int fps, out int bitrateKbps, out int scalePercent)
    {
        switch (preset)
        {
            case Fast: fps = 30; bitrateKbps = 6_000; scalePercent = 50; return true;
            case Balanced: fps = 60; bitrateKbps = 15_000; scalePercent = 100; return true;
            case Quality: fps = 60; bitrateKbps = 25_000; scalePercent = 100; return true;
            default: fps = 0; bitrateKbps = 0; scalePercent = 0; return false; // custom:呼叫端不套用
        }
    }
}

/// <summary>Host 端信任的一台 Client(計畫 §2 配對成功後記下來的那一半——
/// <see cref="PinnedHostRecord"/> 是 Client 記 Host,這個反過來是 Host 記 Client)。
/// 指紋一樣是公開雜湊,明文存沒關係。</summary>
public sealed class TrustedClientRecord
{
    public string FingerprintHex { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset LastConnected { get; set; }
}

/// <summary>Kyra 要求的功能:讓 RDP-B 自己管理 frpc(github.com/fatedier/frp),
/// 這台機器當 Host 時,把自己的監聽埠透過 Kyra 自己架的 frps(VPS 上)對外曝露——
/// 不用手動在路由器開埠/UPnP,原理類似「Host 主動撥出去」的反向隧道,跟 Host
/// 本身是監聽方這件事不衝突:frpc 才是真正主動連出去的那一端。</summary>
public sealed class FrpSettings
{
    public bool Enabled { get; set; } = false;
    public string ServerAddr { get; set; } = "";
    public int ServerPort { get; set; } = 7000; // frps 預設控制埠

    /// <summary>DPAPI 加密後的 base64——**不是明文 token**,讀寫一律經過 <see cref="Token"/>,
    /// 不要直接動這個欄位(計畫 §8,跟 TransportSettings.EncryptedProxyPasswordBase64
    /// 同一個模式)。</summary>
    public string? EncryptedTokenBase64 { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Token
    {
        get => EncryptedTokenBase64 is null ? null : SecretProtector.UnprotectFromBase64(EncryptedTokenBase64);
        set => EncryptedTokenBase64 = value is null ? null : SecretProtector.ProtectToBase64(value);
    }

    /// <summary>frps 對外要開的那個公開埠——Client 端最後連的是 ServerAddr:RemotePort,
    /// 不是本機的 Host 埠。</summary>
    public int RemotePort { get; set; } = 17443;
}

/// <summary>Kyra 要求的功能:「就算 Client 那台機器本身被裝了惡意軟體/有人在遠端監看,
/// 串流畫面本身也抓不到」。兩層都是 Client 端的設定,刻意預設關閉——兩個都有真實副作用
/// (見各欄位註解),不該在使用者沒選的情況下自己生效。</summary>
public sealed class PrivacySettings
{
    /// <summary>串流視窗排除於任何畫面擷取(<c>SetWindowDisplayAffinity</c> +
    /// <c>WDA_EXCLUDEFROMCAPTURE</c>,Windows 10 2004+ 才支援)——截圖工具、OBS、Windows
    /// Graphics Capture API、Teams/Zoom 的螢幕分享全部只會看到黑屏,但正在操作的人眼睛看
    /// 畫面完全正常。不是真的 HDCP(那要走 Output Protection Manager,綁在認證過的硬體保護
    /// 鏈上,對這個情境殺雞用牛刀),是 OS 原生支援、可靠度高的「排除擷取」機制,Teams 的
    /// 敏感內容、密碼管理器也是用同一支 API。
    ///
    /// 預設關閉:開著的話 Kyra 自己想截圖、想用 OBS 錄自己的遠端畫面也會錄不到,是真的
    /// 副作用,不是無痛開關。</summary>
    public bool BlockScreenCapture { get; set; } = false;

    /// <summary>偵測有沒有東西被注入進串流視窗的程序(DLL 注入、debugger 附加)——
    /// best-effort、縮小攻擯窗口、抬高攻擊者的成本,**不是保證防住**:真的有 kernel 權限的
    /// 攻擊者還是繞得過去,這點要老實講清楚,不是不實的安全承諾(跟計畫 §7 RDP 偽裝那節的
    /// 態度一致)。偵測到就直接斷線,不是「警告但繼續」——一旦懷疑被注入,繼續串流等於讓
    /// 攻擊者繼續有機會把解碼後的畫面內容挖出去。
    ///
    /// 預設關閉:定期掃描程序載入的模組清單本身幾乎零成本,但誤判(例如某些驅動程式/
    /// 防毒軟體自己注入的合法模組)會讓使用者莫名其妙被斷線,所以讓 Kyra 自己決定要不要
    /// 承擔這個風險換取這層防護。</summary>
    public bool DetectTampering { get; set; } = false;
}

/// <summary>計畫 §4/§8 的 HUD 設定——Client 專屬(操作者自己要不要看、怎麼看的東西,
/// 不是 Host 端的設定)。**老實講清楚範圍**:這一版只做了「連線狀況」和「協議/編解碼細節」
/// 兩塊,「拆解延遲」(擷取/編碼/網路/解碼/present 五段分開顯示)還沒做——那需要在 Host
/// 擷取/編碼熱路徑上加時間戳記並且透過訊息傳回 Client,影響範圍比純顯示層大得多,這次
/// 範圍先不做,F10 循環也只在「全部/只連線狀況/全關」這三態之間切,不是真的六種排列組合。</summary>
public sealed class HudSettings
{
    public string Preset { get; set; } = "connection-only"; // all|connection-only|off,開啟串流視窗時的初始狀態,之後 F10 可以在這三態循環

    public string Position { get; set; } = "top-left"; // top-left|top-right|bottom-left|bottom-right

    /// <summary>0~1,1 = 完全不透明。用 GDI AlphaBlend 真的做半透明(不是 §8 原本「連線中斷」
    /// banner 那種犧牲視覺效果的 solid 色塊簡化版——HUD 本來就是疊在畫面上長時間看的東西,
    /// 值得花這個工夫)。</summary>
    public double Opacity { get; set; } = 0.85;

    public int FontSize { get; set; } = 12;

    /// <summary>HUD 整體重繪頻率(Hz)。計畫原文建議「連線狀況這塊的 RTT/丟失率可以單獨設
    /// 快一點,例如 4Hz」——這一版簡化成只有一個整體更新頻率,沒有做到「不同區塊各自獨立
    /// 更新頻率」那麼細,老實標出來不是忘記。</summary>
    public double UpdateHz { get; set; } = 1.0;
}

/// <summary>Host 端的「連線密碼」——Kyra 要求的無人值守加固(見
/// <see cref="PinnedHostRecord.RequiresConnectionPassword"/> 上的註解)。跟
/// <see cref="FrpSettings.Token"/> 同一個模式,DPAPI 加密存在磁碟上,不是 PBKDF2 單向雜湊
/// ——這裡**必須**是可逆的:Host 端驗證時要把明文密碼當 HMAC key 去跟 Client 送來的 tag
/// 比對(跟 PIN 配對完全同一套機制,見 PairingService.VerifyPairingTag),單向雜湊沒辦法
/// 拿來當 HMAC key 用。</summary>
public sealed class ConnectionPasswordSettings
{
    public bool Enabled { get; set; } = false;

    public string? EncryptedPasswordBase64 { get; set; }

    [JsonIgnore]
    public string? Password
    {
        get => EncryptedPasswordBase64 is null ? null : SecretProtector.UnprotectFromBase64(EncryptedPasswordBase64);
        set => EncryptedPasswordBase64 = value is null ? null : SecretProtector.ProtectToBase64(value);
    }
}

public sealed class RdpBConfig
{
    public List<PinnedHostRecord> PinnedHosts { get; set; } = new();
    public List<TrustedClientRecord> TrustedClients { get; set; } = new();
    public TransportSettings Default { get; set; } = new();
    public QualitySettings Quality { get; set; } = new();
    public FrpSettings Frp { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public ConnectionPasswordSettings ConnectionPassword { get; set; } = new();

    // §8 其他設定分類,先放基本欄位,GUI 階段再擴充
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+Shift";
    public HudSettings Hud { get; set; } = new();

    /// <summary>縮小視窗(或按 X)時自動收到系統匣,不是真的結束程式——預設開,
    /// 使用者可以在設定頁關掉,改成縮小/關閉就直接退出。(UX 回饋:自動化行為,
    /// 不要另外放一個「縮到系統匣」按鈕讓人自己按。)</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Kyra 回報的真坑:這台機器要當 Host 時監聽哪個埠,以前完全沒有讓使用者改的
    /// 地方——<c>HostViewModel.StartAsync</c>/<c>HostListenerService.Start</c> 都有
    /// <c>port</c> 參數,但從頭到尾沒有任何呼叫端傳過不是 <see cref="DefaultPorts.Host"/>
    /// 的值,UI 上也沒有對應欄位。同一台機器想同時用兩個獨立的 RdpB 身分分別開 Host(例如
    /// 不同用途各自配對不同的裝置),或單純原本的埠被別的東西佔用,都沒辦法繞過去。這個值
    /// 存起來,Host 分頁的「狀態」那頁可以改、改完要重啟 Host 才會套用(跟 frp/連線密碼那些
    /// 設定同一個慣例)。TCP+TLS/QUIC/RDP 偽裝自動偵測都共用同一個埠(見 DefaultPorts.Host
    /// 上的註解),所以只需要存這一個數字。</summary>
    public int HostListenPort { get; set; } = DefaultPorts.Host;

    /// <summary>無痕模式(見 IncognitoSession)要用哪種模式啟動,"ask"(預設,每次開啟都問)
    /// |"normal"|"incognito"。使用者在啟動選擇視窗勾選「記住這個選擇」才會變成後兩者,
    /// 之後開啟 app 直接套用、不再問——這個欄位本身永遠寫在**真正的** config.json
    /// (ConfigStore.DefaultPath),不會因為正在無痕模式裡就跑去寫暫存複本,不然下次就沒有
    /// 「記住」這件事可言了。命令列的 --incognito/--normal 永遠覆蓋這個記住的選擇。</summary>
    public string DefaultLaunchMode { get; set; } = "ask"; // ask|normal|incognito
}

/// <summary>讀寫 <c>%APPDATA%\RdpB\config.json</c>(計畫 §8)。一般設定明文,敏感欄位
/// 透過 <see cref="TransportSettings.ProxyPassword"/> 之類的屬性自動走 DPAPI。
///
/// **整份設定檔的密碼加密**(Kyra 要求的功能,取代原本只鎖 UI 畫面的設計):Kyra 的原話是
/// 「用密碼鎖保護的東西請直接加密,設定加密了就每次進應用就要求輸入密碼,設定檔就每次使用
/// 設定檔都要密碼」——原本的設計(見已刪除的 SettingsLockVerifier)只在「設定」分頁前面擋一個
/// UI 畫面、存的是 PBKDF2 雜湊,config.json 本身在磁碟上還是明文,不符合這個要求。改成整份
/// config.json 真的用密碼衍生的金鑰(AES-256-GCM)加密存在磁碟上,解密**只在程式啟動那一刻
/// 問一次密碼**(App.axaml.cs 的 EnsureConfigUnlockedAsync)——衍生出來的金鑰快取在
/// <see cref="_sessionKey"/>(整個行程的生命週期,不是某個特定 configPath 專屬),之後這次
/// 執行期間所有 Load/Save(不管讀寫哪個路徑,包括無痕模式的暫存複本——同一把金鑰天生就能
/// 解開用同一把金鑰存的任何檔案)都透明套用,不用每次存取都重新問密碼一次,但也不是完全不問
/// ——沒有先成功 Unlock 過,Load 一份加密檔會直接丟例外,不會悄悄用明文邏輯去解析加密過的
/// 位元組而得到亂七八糟的結果。
///
/// **檔案格式**(自己描述自己,不需要另外存一個「有沒有加密」的旗標去判斷):
/// magic(8 bytes,ASCII "RDPBENC1") + salt(16 bytes) + nonce(12 bytes) + tag(16 bytes)
/// + ciphertext(其餘)。沒有這個 magic 開頭的檔案一律當明文 JSON 處理(向下相容,沒開過
/// 加密的使用者完全不受影響)。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly byte[] EncryptedMagic = "RDPBENC1"u8.ToArray();
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    // OWASP 建議至少 600k,這裡用跟原本 SettingsLockVerifier 一樣的理由(本機互動用途,
    // 不是帳號密碼資料庫外洩後被離線暴力破解的那個威脅模型)先用較低值換取不要每次啟動
    // 程式都要等好幾秒才能解密。
    private const int Pbkdf2Iterations = 200_000;

    /// <summary>這次執行期間解開過之後快取的金鑰——是整個行程共用的,不是某個 configPath
    /// 專屬的(無痕模式的暫存複本也是用同一把鑰匙加密/解密,見類別上的註解)。</summary>
    private static byte[]? _sessionKey;
    private static byte[]? _sessionSalt;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "config.json");

    /// <summary>這份設定檔是不是加密過的格式——App 啟動時用這個決定要不要跳密碼輸入視窗,
    /// 不存在或是明文 JSON 都回傳 false。</summary>
    public static bool IsEncrypted(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return false;

        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < EncryptedMagic.Length) return false;
            var header = new byte[EncryptedMagic.Length];
            int read = fs.Read(header, 0, header.Length);
            return read == header.Length && header.AsSpan().SequenceEqual(EncryptedMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // code review 抓到的坑:原本只接 IOException——如果檔案被防毒軟體/同步工具暫時
            // 鎖住而丟的是 UnauthorizedAccessException,這裡會直接往外飄,從 App.axaml.cs
            // 那個沒人等待的 fire-and-forget InitializeAsync Task 裡消失,表現成程式悄悄卡住
            // 沒有任何畫面,完全看不出原因。讀不到就當作不是加密格式,交給正常的 Load 流程
            // 去處理同一個例外(那裡至少是在使用者看得到結果的流程裡)。
            return false;
        }
    }

    /// <summary>用密碼試著解開 <paramref name="path"/>——成功的話把衍生出來的金鑰快取在這個
    /// 行程的生命週期裡,回傳 true;密碼錯(AES-GCM 的驗證標籤對不起來)回傳 false,不改變
    /// 任何既有狀態,呼叫端可以讓使用者重新輸入。</summary>
    public static bool TryUnlock(string password, string? path = null)
    {
        path ??= DefaultPath;
        var bytes = File.ReadAllBytes(path);
        if (!TryParseEnvelope(bytes, out var salt, out _, out _, out _)) return false;

        var key = DeriveKey(password, salt);
        if (!TryDecryptWithKey(bytes, key, out _)) return false;

        _sessionKey = key;
        _sessionSalt = salt;
        return true;
    }

    /// <summary>開啟加密——衍生一把新的金鑰(新的隨機 salt,就算是「重設密碼」也一樣,不
    /// 沿用舊的 salt),把目前的設定內容重新存一次(這次會真的寫成加密格式)。</summary>
    public static void EnableEncryption(string password, string? path = null)
    {
        path ??= DefaultPath;
        var config = Load(path); // 可能原本是明文,也可能是已經加密過的(重設密碼的情境)
        _sessionSalt = RandomNumberGenerator.GetBytes(SaltSize);
        _sessionKey = DeriveKey(password, _sessionSalt);
        Save(config, path); // _sessionKey 有值,Save 會自動寫成加密格式
    }

    /// <summary>關閉加密——把目前的設定內容重新存成明文 JSON。</summary>
    public static void DisableEncryption(string? path = null)
    {
        path ??= DefaultPath;
        var config = Load(path); // 用目前快取的 _sessionKey 解密讀出來
        _sessionKey = null;
        _sessionSalt = null;
        Save(config, path); // 沒有 _sessionKey 了,Save 會寫成明文
    }

    public static RdpBConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new RdpBConfig();

        var bytes = File.ReadAllBytes(path);
        if (LooksEncrypted(bytes))
        {
            if (_sessionKey is null)
                throw new InvalidOperationException(
                    "設定檔已加密,必須先用 ConfigStore.TryUnlock 驗證密碼才能讀取——這通常代表呼叫順序有誤," +
                    "App.axaml.cs 應該在建立任何會讀設定檔的物件之前就先確保解鎖完成。");

            if (!TryDecryptWithKey(bytes, _sessionKey, out var json))
                throw new InvalidOperationException("設定檔解密失敗——可能是檔案損毀,或者快取的金鑰跟這份檔案對不上。");

            return JsonSerializer.Deserialize<RdpBConfig>(json, JsonOptions) ?? new RdpBConfig();
        }

        // code review 抓到的坑:原本這裡是另外呼叫 File.ReadAllText(path) 重讀一次——明文
        // 情境(目前絕大多數使用者)等於每次 Load 都讀兩次磁碟,上面的 bytes 已經是完整檔案
        // 內容了,直接轉字串就好,不用真的再讀一次。
        var text = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<RdpBConfig>(text, JsonOptions) ?? new RdpBConfig();
    }

    public static void Save(RdpBConfig config, string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);

        if (_sessionKey is not null && _sessionSalt is not null)
            File.WriteAllBytes(path, Encrypt(json, _sessionKey, _sessionSalt));
        else
            File.WriteAllText(path, json);
    }

    /// <summary>測試專用:清掉這個行程快取的金鑰,不去動任何檔案——<see cref="_sessionKey"/>
    /// 是整個行程生命週期共用的全域狀態(刻意的,見類別上的註解),任何呼叫過
    /// EnableEncryption/TryUnlock 的測試**都必須**在 finally 裡呼叫這個方法重置,不然會
    /// 汙染同一個測試執行序裡後面其他測試讀寫 config.json 的行為(它們預期是明文,卻被
    /// 這裡留下的金鑰影響到 Save/Load 邏輯)。</summary>
    public static void ResetSessionForTests()
    {
        _sessionKey = null;
        _sessionSalt = null;
    }

    private static bool LooksEncrypted(byte[] bytes) =>
        bytes.Length >= EncryptedMagic.Length && bytes.AsSpan(0, EncryptedMagic.Length).SequenceEqual(EncryptedMagic);

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Encrypt(string json, byte[] key, byte[] salt)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aesGcm = new AesGcm(key, TagSize))
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[EncryptedMagic.Length + salt.Length + nonce.Length + tag.Length + ciphertext.Length];
        int offset = 0;
        EncryptedMagic.CopyTo(result, offset); offset += EncryptedMagic.Length;
        salt.CopyTo(result, offset); offset += salt.Length;
        nonce.CopyTo(result, offset); offset += nonce.Length;
        tag.CopyTo(result, offset); offset += tag.Length;
        ciphertext.CopyTo(result, offset);
        return result;
    }

    /// <summary>只拆信封、不解密——TryUnlock 要先拿到 salt 才能衍生金鑰去試解密。</summary>
    private static bool TryParseEnvelope(byte[] bytes, out byte[] salt, out byte[] nonce, out byte[] tag, out byte[] ciphertext)
    {
        salt = []; nonce = []; tag = []; ciphertext = [];
        int headerLen = EncryptedMagic.Length + SaltSize + NonceSize + TagSize;
        if (bytes.Length < headerLen || !LooksEncrypted(bytes)) return false;

        int offset = EncryptedMagic.Length;
        salt = bytes[offset..(offset + SaltSize)]; offset += SaltSize;
        nonce = bytes[offset..(offset + NonceSize)]; offset += NonceSize;
        tag = bytes[offset..(offset + TagSize)]; offset += TagSize;
        ciphertext = bytes[offset..];
        return true;
    }

    /// <summary>已經有金鑰在手(不管是剛衍生出來的,還是這次執行期間快取的),直接拿去解
    /// nonce/tag/ciphertext——不用重新衍生金鑰,salt 只在 TryUnlock/EnableEncryption 那一刻
    /// 用過一次。密碼錯或資料被動過都會讓 AES-GCM 的驗證標籤對不起來,統一回傳 false,不分
    /// 這兩種情況(跟「密碼錯」在呼叫端看來是同一件事:沒辦法用這把金鑰讀到內容)。</summary>
    private static bool TryDecryptWithKey(byte[] bytes, byte[] key, out string json)
    {
        json = "";
        if (!TryParseEnvelope(bytes, out _, out var nonce, out var tag, out var ciphertext)) return false;

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }

        json = Encoding.UTF8.GetString(plaintext);
        return true;
    }
}
