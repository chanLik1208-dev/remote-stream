using RdpB.Core.Windows;

namespace RdpB.App.Services;

/// <summary>
/// Kyra 要求的「無痕模式」:可以讀正常模式的 config.json(已配對裝置、設定,正常使用的
/// 方便性都在),但這個 session 做的任何事——新配對、LastConnected 時間戳更新、改過的
/// 設定、frp token——全部只留在這個複本裡,關掉就消失,**真正的 config.json 完全不會被
/// 這個 session 寫到一個 byte**。
///
/// 做法:啟動時把真正的 config.json(如果存在)複製一份到 %TEMP% 底下一個隨機檔名,
/// 之後整個 session 全部的 ConfigStore.Load/Save 都對著這個複本操作——不用改
/// ConfigStore 本身或任何 ViewModel 的邏輯,因為它們本來就已經是「吃一個 configPath
/// 參數」的設計(這次順便補上 MainWindowViewModel.Host 原本沒有接到 configPath 的坑,
/// 見那邊的註解)。
///
/// **老實標出來的限制**:
///  - 身分憑證(host-identity.bin/client-identity.bin)刻意*不*複製,繼續用真正那份
///    ——這是故意的,不是漏做:無痕模式要的是「不留新紀錄」,不是「假裝成另一台設備」,
///    沿用同一份身分才能連得上已經配對過的裝置(正常使用的方便性)。代價是:這台機器上
///    `%APPDATA%\RdpB\` 底下的身分憑證檔案本來就在那裡,無痕模式不會也不能把它藏起來。
///  - 如果程式在 session 中途異常結束(當掉、被強制關閉),temp 檔案可能沒有被清掉,
///    會留在 %TEMP% 資料夾裡——不是完全無痕,是盡力而為;正常透過視窗關閉/系統匣結束
///    都會清掉。
/// </summary>
public sealed class IncognitoSession : IDisposable
{
    public string ConfigPath { get; }

    private IncognitoSession(string configPath) => ConfigPath = configPath;

    public static IncognitoSession Start()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rdpb-incognito-{Guid.NewGuid():N}.json");

        // 複製「現有設定的內容」,不是複製檔案本身的位元組——這樣如果真正的 config.json
        // 格式之後升級,這裡也不用跟著改,永遠是當下記憶體裡那份 RdpBConfig 物件的快照。
        var realConfig = ConfigStore.Load(ConfigStore.DefaultPath);
        ConfigStore.Save(realConfig, tempPath);

        return new IncognitoSession(tempPath);
    }

    public void Dispose()
    {
        try { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); }
        catch { /* 盡力而為,見類別上的註解——刪不掉不該讓程式關不掉 */ }
    }
}
