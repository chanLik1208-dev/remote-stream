using System.Security.Cryptography.X509Certificates;
using RdpB.Client;
using RdpB.Core.Pairing;
using RdpB.Core.Transport;
using RdpB.Core.Windows;
using CoreSession = RdpB.Core.Session.Session;

namespace RdpB.App.Services;

public enum ConnectOutcome { Success, PairingCancelled, WrongPin, CertificateMismatch, PairingFailed, ConnectFailed }

/// <summary><paramref name="Window"/> 只有 <see cref="ConnectOutcome.Success"/> 才會有值——
/// 呼叫端(測試,或之後想做「視窗管理」的 GUI 功能)可以用它追蹤/關閉這次開出來的串流視窗,
/// 不用整個方法都不回傳任何東西。</summary>
public sealed record ConnectResultInfo(ConnectOutcome Outcome, string Message, StreamWindow? Window = null);

/// <summary>
/// 計畫 §8「連線」按鈕背後真正的邏輯——跟 ViewModel/Avalonia 完全分開,方便之後寫測試。
/// 串連:連線(mTLS)→ 沒配對過就跑 §2 的 PIN 配對流程 → 配對過就驗證指紋沒變 → 開
/// <see cref="CoreSession"/> → 交給 <see cref="StreamWindow"/> 真正把串流畫面開出來。
///
/// **直接用 <see cref="TcpTlsTransport"/>,不透過 <see cref="TransportConnector"/>**:
/// 後者的 auto 模式會先試 QUIC,但 <see cref="HostListenerService"/> 目前只開了 TCP+TLS
/// 監聽(沒有 QUIC listener)——每次連線都要先白等 QUIC 的 1.5 秒逾時才退回 TCP,是白白
/// 浪費的等待。等 Host 端真的接上 QUIC listener 之後再換回
/// <see cref="TransportConnector"/>,目前這樣比較誠實地反映現況。
/// </summary>
public sealed class ClientConnectionService
{
    private readonly RdpBConfig _config;
    private readonly string _configPath;

    public ClientConnectionService(string? configPath = null)
    {
        _configPath = configPath ?? ConfigStore.DefaultPath;
        _config = ConfigStore.Load(_configPath);
    }

    /// <summary>
    /// <paramref name="requestPin"/>:沒配對過時用來跳出 PIN 輸入畫面,回傳 null 表示使用者取消。
    /// 這個類別完全不碰 Avalonia,UI 怎麼問 PIN 由呼叫端決定。
    /// </summary>
    public async Task<ConnectResultInfo> ConnectAsync(
        PinnedHostRecord record,
        Func<Task<string?>> requestPin,
        CancellationToken ct = default)
    {
        var (host, port) = ViewModels.MainWindowViewModel.ParseHostAndPort(record.HostAddress);

        var proxy = _config.Default.ProxyUrl is { Length: > 0 } ? ParseProxy(_config.Default.ProxyUrl) : null;
        var clientCertPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "client-identity.bin");
        var clientCert = CertificateStore.LoadOrCreate(clientCertPath, Environment.MachineName + "-client");

        bool alreadyPaired = record.FingerprintHex.Length > 0;

        TcpTlsTransport transport;
        try
        {
            transport = await TcpTlsTransport.ConnectAsync(
                host, port, proxy,
                validateServerCertificate: cert => ValidateServerCertificate(cert, record, alreadyPaired),
                ct: ct,
                clientCertificate: clientCert).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ConnectResultInfo(ConnectOutcome.ConnectFailed, $"連不上 {record.Name}:{ex.Message}");
        }

        var session = new CoreSession(transport);
        session.Start();

        if (!alreadyPaired)
        {
            var pin = await requestPin().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pin))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(ConnectOutcome.PairingCancelled, "已取消配對");
            }

            var pairResult = await PairingCoordinator.PairAsync(
                session, pin.Trim(), transport.RemoteCertificate!, clientCert, ct).ConfigureAwait(false);

            if (pairResult != PairingResult.Success)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new ConnectResultInfo(pairResult switch
                {
                    PairingResult.WrongPin => ConnectOutcome.WrongPin,
                    PairingResult.CertificateMismatch => ConnectOutcome.CertificateMismatch,
                    _ => ConnectOutcome.PairingFailed,
                }, DescribePairingFailure(pairResult));
            }

            record.FingerprintHex = Convert.ToHexString(PairingService.ComputeFingerprint(transport.RemoteCertificate!.RawData));
        }

        record.LastConnected = DateTimeOffset.UtcNow;
        PersistPairingResult(record);

        var window = new StreamWindow(session, $"RDP-B — {record.Name}");
        window.Show();

        return new ConnectResultInfo(ConnectOutcome.Success, $"已連線到 {record.Name}", window);
    }

    /// <summary>
    /// **真的坑,不是順手加的防禦性寫法**:這個類別建構時載入的 <see cref="_config"/> 只是
    /// 為了讀 <c>Default.ProxyUrl</c>——跟呼叫端(MainWindowViewModel)手上那份、record 實際
    /// 活在裡面的 <c>RdpBConfig</c> 是完全不同的物件實例。如果直接 <c>ConfigStore.Save(_config, _configPath)</c>,
    /// 存進去的是「這個服務自己建構時讀到的那份舊快照」,record 剛剛配對成功填上去的
    /// FingerprintHex 根本不在那份快照的 PinnedHosts 裡——等於白做,甚至可能拿舊快照覆蓋掉
    /// GUI 那邊真正的最新狀態。改成重新從磁碟讀一次最新的 config,按 HostAddress 找到(或建立)
    /// 對應的項目再存回去,不依賴呼叫端手上那份物件實例是不是「最新」的。</summary>
    private void PersistPairingResult(PinnedHostRecord record)
    {
        var config = ConfigStore.Load(_configPath);
        var existing = config.PinnedHosts.FirstOrDefault(h => h.HostAddress == record.HostAddress);
        if (existing is null)
        {
            config.PinnedHosts.Add(new PinnedHostRecord
            {
                Name = record.Name,
                HostAddress = record.HostAddress,
                FingerprintHex = record.FingerprintHex,
                LastConnected = record.LastConnected,
            });
        }
        else
        {
            existing.FingerprintHex = record.FingerprintHex;
            existing.LastConnected = record.LastConnected;
        }
        ConfigStore.Save(config, _configPath);
    }

    /// <summary>已經配對過的裝置:TLS handshake 時對方出示的憑證指紋,一定要跟
    /// <see cref="PinnedHostRecord.FingerprintHex"/> 記的一致,不一致直接拒絕連線(可能是
    /// 重裝過系統、或真的有人冒充)。沒配對過的裝置:這裡先接受任何憑證,真正的信任建立在
    /// 之後的 PIN 配對流程(計畫 §2)——那一步才是密碼學上真正綁定身分的地方,TLS 層先接受
    /// 只是為了能把連線建立起來談 PIN,不是最終的信任判斷。</summary>
    private static bool ValidateServerCertificate(X509Certificate2? cert, PinnedHostRecord record, bool alreadyPaired)
    {
        if (!alreadyPaired) return true;
        if (cert is null) return false;

        var actualFingerprint = Convert.ToHexString(PairingService.ComputeFingerprint(cert.RawData));
        return string.Equals(actualFingerprint, record.FingerprintHex, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribePairingFailure(PairingResult result) => result switch
    {
        PairingResult.WrongPin => "PIN 不正確",
        PairingResult.CertificateMismatch => "憑證指紋不符,可能是重裝過系統或連線被竄改,需要重新配對",
        PairingResult.Timeout => "配對逾時,對方沒有在時限內回應",
        PairingResult.Disconnected => "配對過程中斷線了",
        _ => "配對失敗",
    };

    private static ProxyOptions? ParseProxy(string url)
    {
        if (!url.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url["socks5://".Length..];
        var parts = rest.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) return null;
        return new ProxyOptions(ProxyKind.Socks5, parts[0], port);
    }
}
