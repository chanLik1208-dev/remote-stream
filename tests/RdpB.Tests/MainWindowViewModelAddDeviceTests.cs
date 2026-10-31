using RdpB.App.ViewModels;
using RdpB.Core.Windows;

namespace RdpB.Tests;

/// <summary>「允許預匹配模式」的輸入驗證——格式檢查、正常值,還有確保 PinPairingPending
/// 只在真的有預匹配指紋時才設成 true(見 PinnedHostRecord.PinPairingPending 上的註解)。</summary>
public class MainWindowViewModelAddDeviceTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rdpb-adddevice-{Guid.NewGuid():N}.json");

    [Fact]
    public void AddDevice_WithoutFingerprint_CreatesRecord_WithPinPairingNotPending()
    {
        var vm = new MainWindowViewModel(_configPath)
        {
            NewDeviceName = "測試主機",
            NewDeviceAddress = "192.168.0.50",
        };

        vm.AddDeviceCommand.Execute(null);

        var saved = ConfigStore.Load(_configPath);
        var record = Assert.Single(saved.PinnedHosts);
        Assert.Equal("", record.FingerprintHex);
        Assert.False(record.PinPairingPending);
    }

    [Fact]
    public void AddDevice_WithValidPreMatchedFingerprint_NormalizesAndStores_WithPinPairingPending()
    {
        var vm = new MainWindowViewModel(_configPath)
        {
            NewDeviceName = "預匹配主機",
            NewDeviceAddress = "192.168.0.51",
            // 故意帶冒號分隔(常見的指紋顯示格式)跟小寫,應該自動正規化成跟
            // Convert.ToHexString 輸出一致的無分隔大寫格式。
            NewDeviceFingerprintHex = string.Join(":", Enumerable.Repeat("ab", 32)),
        };

        vm.AddDeviceCommand.Execute(null);

        var saved = ConfigStore.Load(_configPath);
        var record = Assert.Single(saved.PinnedHosts);
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), record.FingerprintHex);
        Assert.True(record.PinPairingPending); // 預匹配不等於配對完成,PIN 還是要走一次
    }

    [Theory]
    [InlineData("太短")]
    [InlineData("ZZ")] // 不是十六進位字元
    public void AddDevice_WithInvalidFingerprintFormat_RejectsAndDoesNotSave(string badFingerprint)
    {
        var vm = new MainWindowViewModel(_configPath)
        {
            NewDeviceName = "格式錯誤測試",
            NewDeviceAddress = "192.168.0.52",
            NewDeviceFingerprintHex = badFingerprint,
        };

        vm.AddDeviceCommand.Execute(null);

        var saved = ConfigStore.Load(_configPath);
        Assert.Empty(saved.PinnedHosts);
        Assert.Contains("格式不對", vm.StatusMessage);
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }
}
