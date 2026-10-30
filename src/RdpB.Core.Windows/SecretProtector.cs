using System.Security.Cryptography;

namespace RdpB.Core.Windows;

/// <summary>
/// 計畫 §8 的保護分類:**代理帳密**和**憑證私鑰**用 DPAPI 加密,**不存明文**。
/// 綁在 Windows 使用者帳號(CurrentUser scope)上——換電腦、換帳號打不開是預期行為,
/// 本來就不該讓別人的帳號讀到你的代理密碼。
/// </summary>
public static class SecretProtector
{
    // entropy 讓同一份 DPAPI 保護多一層跟應用程式綁定的區隔,不是安全的全部,只是防止
    // 「這台機器上隨便哪個程式都能解開」的最低程度加強。
    private static readonly byte[] Entropy = "RdpB.SecretProtector.v1"u8.ToArray();

    public static byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] encrypted) =>
        ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);

    public static string ProtectToBase64(string plaintext) =>
        Convert.ToBase64String(Protect(System.Text.Encoding.UTF8.GetBytes(plaintext)));

    public static string UnprotectFromBase64(string base64) =>
        System.Text.Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(base64)));
}
