using RdpB.Core.Windows;

namespace RdpB.Tests;

/// <summary>Kyra 要求的功能:「用密碼鎖保護的東西請直接加密,設定加密了就每次進應用就要求
/// 輸入密碼,設定檔就每次使用設定檔都要密碼」——整份 config.json 用密碼衍生的 AES-256-GCM
/// 金鑰加密,見 ConfigStore 類別上的註解。
///
/// **_sessionKey 是行程全域的靜態狀態**,每個測試都要在 finally 裡呼叫
/// ConfigStore.ResetSessionForTests(),不然會汙染同一個測試執行序裡後面其他測試
/// (這個 class 本身沒有標 Hardware,但一樣要照 AssemblyInfo.cs 的
/// DisableTestParallelization 假設——同一個行程裡其他測試也在用 ConfigStore)。</summary>
public class ConfigStoreEncryptionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rdpb-enc-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        ConfigStore.ResetSessionForTests();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void PlainConfig_IsNotReportedAsEncrypted()
    {
        ConfigStore.Save(new RdpBConfig { ToggleHotkey = "x" }, _path);

        Assert.False(ConfigStore.IsEncrypted(_path));
        Assert.Equal("x", ConfigStore.Load(_path).ToggleHotkey);
    }

    [Fact]
    public void EnableEncryption_ThenTryUnlockAfterSessionReset_RoundTripsContent()
    {
        var config = new RdpBConfig { ToggleHotkey = "祕密設定" };
        ConfigStore.Save(config, _path); // 先存一份明文,模擬「本來就有在用」的情境

        ConfigStore.EnableEncryption("correct horse battery staple", _path);
        Assert.True(ConfigStore.IsEncrypted(_path));

        // 模擬「程式重開」——這個行程快取的金鑰消失,下一次讀取一定要重新 TryUnlock,
        // 不能魔法般繼續讀得到內容。
        ConfigStore.ResetSessionForTests();
        Assert.Throws<InvalidOperationException>(() => ConfigStore.Load(_path));

        bool unlocked = ConfigStore.TryUnlock("correct horse battery staple", _path);
        Assert.True(unlocked);
        Assert.Equal("祕密設定", ConfigStore.Load(_path).ToggleHotkey);
    }

    [Fact]
    public void TryUnlock_WrongPassword_ReturnsFalseAndLeavesFileUnreadable()
    {
        ConfigStore.EnableEncryption("right-password", _path);
        ConfigStore.ResetSessionForTests();

        bool unlocked = ConfigStore.TryUnlock("wrong-password", _path);

        Assert.False(unlocked);
        // 密碼錯誤不該悄悄把 _sessionKey 設成某個錯的值讓後續 Load 讀出亂碼——
        // 應該維持「還沒解鎖」的狀態,Load 繼續丟 InvalidOperationException。
        Assert.Throws<InvalidOperationException>(() => ConfigStore.Load(_path));
    }

    [Fact]
    public void DisableEncryption_WritesBackPlainJson()
    {
        ConfigStore.EnableEncryption("temp-password", _path);
        Assert.True(ConfigStore.IsEncrypted(_path));

        ConfigStore.DisableEncryption(_path);

        Assert.False(ConfigStore.IsEncrypted(_path));
        // 解除加密之後不需要密碼就能直接讀(ResetSessionForTests 之後也一樣)。
        ConfigStore.ResetSessionForTests();
        Assert.NotNull(ConfigStore.Load(_path));
    }
}
