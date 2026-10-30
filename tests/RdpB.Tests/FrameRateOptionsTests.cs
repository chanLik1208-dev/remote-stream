using RdpB.Core.Media;
using RdpB.Core.Windows;
using RdpB.Host.Media;

namespace RdpB.Tests;

public class FrameRateOptionsTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(144)]
    public void IsValid_AcceptsExactlyTheSixAllowedValues(int fps) =>
        Assert.True(FrameRateOptions.IsValid(fps));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(29)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(145)]
    [InlineData(240)]
    [InlineData(-60)]
    public void IsValid_RejectsAnythingNotInTheList(int fps) =>
        Assert.False(FrameRateOptions.IsValid(fps));

    [Fact]
    public void Validate_Throws_WithAllowedValuesListedInMessage()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FrameRateOptions.Validate(50));
        Assert.Contains("30", ex.Message);
        Assert.Contains("144", ex.Message);
    }

    [Fact]
    public void Validate_DoesNotThrow_ForAllowedValue() =>
        FrameRateOptions.Validate(90); // 沒丟例外就算過

    [Fact]
    public void Default_IsOneOfTheAllowedValues() =>
        Assert.True(FrameRateOptions.IsValid(FrameRateOptions.Default));

    [Fact]
    public void CaptureEncoder_Start_RejectsInvalidFps_BeforeTouchingFFmpegOrHardware()
    {
        // 驗證要在碰任何 FFmpeg/GPU 呼叫之前就先擋下來——這樣這個測試才能在沒有互動桌面的
        // 環境裡安全、快速地跑,不會卡在真的去開擷取裝置。
        using var encoder = new CaptureEncoder();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => encoder.Start(0, 50, 15_000));
        Assert.Contains("fps", ex.Message);
    }
}

public class QualitySettingsTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"rdpb-quality-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void Fps_Setter_RejectsInvalidValue()
    {
        var q = new QualitySettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => q.Fps = 50);
    }

    [Fact]
    public void Default_Fps_Is60()
    {
        var q = new QualitySettings();
        Assert.Equal(60, q.Fps);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips_SelectedFps()
    {
        var config = new RdpBConfig();
        config.Quality.Fps = 144;
        ConfigStore.Save(config, _tempFile);

        var loaded = ConfigStore.Load(_tempFile);
        Assert.Equal(144, loaded.Quality.Fps);
    }

    [Fact]
    public void Load_WithCorruptedOutOfRangeFps_ThrowsRatherThanSilentlyAccepting()
    {
        // 直接寫一個不合法的 fps 進 JSON,模擬 config.json 被手動改壞的情況——
        // 設定裡的驗證要在讀檔時就擋下來,不要讓壞掉的畫質設定一路帶到連線那一刻才爆炸。
        File.WriteAllText(_tempFile, """{"Quality":{"Fps":100}}""");
        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigStore.Load(_tempFile));
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }
}
