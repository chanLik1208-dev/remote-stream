using System.Text.Json;
using System.Text.Json.Serialization;
using RdpB.Core.Media;

namespace RdpB.Core.Windows;

/// <summary>一台已配對 Host 的記錄。指紋是公開資訊的雜湊,不是密鑰,明文存沒關係(計畫 §8)。</summary>
public sealed class PinnedHostRecord
{
    public string Name { get; set; } = "";
    public string HostAddress { get; set; } = "";
    public string FingerprintHex { get; set; } = "";
    public DateTimeOffset LastConnected { get; set; }

    /// <summary>每台 Host 可以分開設(計畫 §8),沒設就沿用 <see cref="RdpBConfig.Default"/> /
    /// <see cref="RdpBConfig.Quality"/>。</summary>
    public TransportSettings? Overrides { get; set; }
    public QualitySettings? QualityOverrides { get; set; }
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

public sealed class RdpBConfig
{
    public List<PinnedHostRecord> PinnedHosts { get; set; } = new();
    public List<TrustedClientRecord> TrustedClients { get; set; } = new();
    public TransportSettings Default { get; set; } = new();
    public QualitySettings Quality { get; set; } = new();

    // §8 其他設定分類,先放基本欄位,GUI 階段再擴充
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+Shift";
    public string HudPreset { get; set; } = "connection-only"; // connection-only|all|off

    /// <summary>縮小視窗(或按 X)時自動收到系統匣,不是真的結束程式——預設開,
    /// 使用者可以在設定頁關掉,改成縮小/關閉就直接退出。(UX 回饋:自動化行為,
    /// 不要另外放一個「縮到系統匣」按鈕讓人自己按。)</summary>
    public bool MinimizeToTray { get; set; } = true;
}

/// <summary>讀寫 <c>%APPDATA%\RdpB\config.json</c>(計畫 §8)。一般設定明文,敏感欄位
/// 透過 <see cref="TransportSettings.ProxyPassword"/> 之類的屬性自動走 DPAPI。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "config.json");

    public static RdpBConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new RdpBConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RdpBConfig>(json, JsonOptions) ?? new RdpBConfig();
    }

    public static void Save(RdpBConfig config, string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }
}
