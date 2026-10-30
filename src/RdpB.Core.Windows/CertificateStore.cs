using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RdpB.Core.Windows;

/// <summary>
/// 計畫 §2:Host/Client 各自第一次啟動時產生一張身分用的自簽憑證(mTLS 用),
/// 私鑰用 <see cref="SecretProtector"/>(DPAPI)加密存成單獨的檔案,不混進 config.json。
/// </summary>
public static class CertificateStore
{
    private const int ValidityYears = 10; // 身分憑證,不是給人看有效期的,拉長一點免得到期要重新配對

    /// <summary>如果 <paramref name="path"/> 已經有加密存檔就載入,否則產生一張新的身分憑證並存檔。</summary>
    public static X509Certificate2 LoadOrCreate(string path, string subjectName)
    {
        if (File.Exists(path))
        {
            var encrypted = File.ReadAllBytes(path);
            var pfxBytes = SecretProtector.Unprotect(encrypted);
            return X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null, X509KeyStorageFlags.Exportable);
        }

        var cert = CreateSelfSigned(subjectName);
        Save(path, cert);
        return cert;
    }

    private static X509Certificate2 CreateSelfSigned(string subjectName)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={subjectName}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(ValidityYears);
        var cert = req.CreateSelfSigned(notBefore, notAfter);

        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static void Save(string path, X509Certificate2 cert)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var pfxBytes = cert.Export(X509ContentType.Pfx);
        var encrypted = SecretProtector.Protect(pfxBytes);
        File.WriteAllBytes(path, encrypted);
    }

    /// <summary>計畫 §2 的指紋——SHA-256 雜湊 DER 編碼的憑證本體,拿去做配對/釘選比對。</summary>
    public static byte[] Fingerprint(X509Certificate2 cert) => SHA256.HashData(cert.RawData);
}
