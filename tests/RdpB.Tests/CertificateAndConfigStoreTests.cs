using RdpB.Core.Windows;

namespace RdpB.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void ProtectUnprotect_RoundTrips()
    {
        var plain = "my-proxy-password-太平の秘密"u8.ToArray();
        var protected_ = SecretProtector.Protect(plain);
        Assert.NotEqual(plain, protected_); // 確實被改過,不是原樣存
        var unprotected = SecretProtector.Unprotect(protected_);
        Assert.Equal(plain, unprotected);
    }

    [Fact]
    public void Base64RoundTrip_Works()
    {
        var b64 = SecretProtector.ProtectToBase64("hunter2");
        Assert.Equal("hunter2", SecretProtector.UnprotectFromBase64(b64));
    }
}

public class CertificateStoreTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"rdpb-cert-test-{Guid.NewGuid():N}.bin");

    [Fact]
    public void LoadOrCreate_CreatesNewCertificate_WhenFileMissing()
    {
        Assert.False(File.Exists(_tempFile));
        var cert = CertificateStore.LoadOrCreate(_tempFile, "rdpb-test-host");

        Assert.True(File.Exists(_tempFile));
        Assert.Contains("rdpb-test-host", cert.Subject);
        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void LoadOrCreate_ReturnsSameIdentity_OnSecondCall()
    {
        var first = CertificateStore.LoadOrCreate(_tempFile, "rdpb-test-host");
        var firstFingerprint = CertificateStore.Fingerprint(first);

        var second = CertificateStore.LoadOrCreate(_tempFile, "rdpb-test-host");
        var secondFingerprint = CertificateStore.Fingerprint(second);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void SavedFile_IsNotAPlaintextCertificate()
    {
        CertificateStore.LoadOrCreate(_tempFile, "rdpb-test-host");
        var raw = File.ReadAllBytes(_tempFile);

        // PKCS12/PFX 檔案開頭是固定的 ASN.1 SEQUENCE tag(0x30);存檔是 DPAPI 加密過的,
        // 不會長這樣——確保真的有被保護,不是明文 PFX 直接落地。
        Assert.NotEqual(0x30, raw[0]);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }
}

public class ConfigStoreTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"rdpb-config-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_ReturnsEmptyDefault_WhenFileMissing()
    {
        var config = ConfigStore.Load(_tempFile);
        Assert.Empty(config.PinnedHosts);
        Assert.Equal("auto", config.Default.Mode);
        Assert.True(config.MinimizeToTray); // 預設開(UX 回饋:自動化行為,預設收到系統匣)
    }

    [Fact]
    public void MinimizeToTray_RoundTrips_WhenSetToFalse()
    {
        var config = new RdpBConfig { MinimizeToTray = false };
        ConfigStore.Save(config, _tempFile);

        var loaded = ConfigStore.Load(_tempFile);
        Assert.False(loaded.MinimizeToTray);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips_PinnedHostsAndSettings()
    {
        var config = new RdpBConfig();
        config.PinnedHosts.Add(new PinnedHostRecord
        {
            Name = "遊戲房主機",
            HostAddress = "192.168.0.50",
            FingerprintHex = "deadbeef",
        });
        config.Default.Mode = "direct";

        ConfigStore.Save(config, _tempFile);
        var loaded = ConfigStore.Load(_tempFile);

        Assert.Single(loaded.PinnedHosts);
        Assert.Equal("遊戲房主機", loaded.PinnedHosts[0].Name);
        Assert.Equal("direct", loaded.Default.Mode);
    }

    [Fact]
    public void ProxyPassword_IsEncryptedAtRest_NotPlaintextInFile()
    {
        var config = new RdpBConfig();
        config.Default.ProxyPassword = "super-secret-password";
        ConfigStore.Save(config, _tempFile);

        var rawJson = File.ReadAllText(_tempFile);
        Assert.DoesNotContain("super-secret-password", rawJson);

        var loaded = ConfigStore.Load(_tempFile);
        Assert.Equal("super-secret-password", loaded.Default.ProxyPassword);
    }

    [Fact]
    public void FingerprintField_IsStoredAsPlaintext_NotEncrypted()
    {
        // §8:指紋是公開雜湊,不需要加密,確認沒有被誤套 DPAPI。
        var config = new RdpBConfig();
        config.PinnedHosts.Add(new PinnedHostRecord { Name = "x", HostAddress = "x", FingerprintHex = "aabbccdd" });
        ConfigStore.Save(config, _tempFile);

        var rawJson = File.ReadAllText(_tempFile);
        Assert.Contains("aabbccdd", rawJson);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }
}
