namespace RdpB.Core.Transport.Obfuscation;

/// <summary>
/// 計畫 §7:連線外觀偽裝,只在 DIRECT 模式生效(§6),走代理時一律不套用。
/// 套在 TCP 連線建立之後、TLS handshake 之前——先交換幾個長得像目標協定的位元組,
/// 再把同一條 socket 交給 SslStream 繼續走正常的 TLS。
/// </summary>
public interface IObfuscator
{
    string Name { get; }

    /// <summary>Client 端:在 TLS handshake 之前,於 <paramref name="stream"/> 上跑一次偽裝握手。</summary>
    Task ClientHandshakeAsync(Stream stream, CancellationToken ct);

    /// <summary>Host 端:接受連線後,在 TLS handshake 之前跑對應的偽裝握手。</summary>
    Task ServerHandshakeAsync(Stream stream, CancellationToken ct);
}

/// <summary>DIRECT 但不偽裝——直接進 TLS,什麼都不做。走代理時就是用這個(§6:兩者互斥)。</summary>
public sealed class NoObfuscator : IObfuscator
{
    public static readonly NoObfuscator Instance = new();
    public string Name => "none";
    public Task ClientHandshakeAsync(Stream stream, CancellationToken ct) => Task.CompletedTask;
    public Task ServerHandshakeAsync(Stream stream, CancellationToken ct) => Task.CompletedTask;
}
