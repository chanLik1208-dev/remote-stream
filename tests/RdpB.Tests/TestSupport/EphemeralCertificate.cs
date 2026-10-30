using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RdpB.Tests.TestSupport;

/// <summary>測試用的自簽憑證,只給 QUIC/TLS 的單元測試建立 handshake 用,不是正式的配對憑證邏輯
/// (那個排在計畫 §2,還沒寫)。</summary>
internal static class EphemeralCertificate
{
    public static X509Certificate2 CreateSelfSigned(string commonName = "rdp-b-test")
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        // 匯出再匯入,確保私鑰可匯出(SslStream/QUIC server 端需要),避開某些平台上 ephemeral key 的限制。
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
