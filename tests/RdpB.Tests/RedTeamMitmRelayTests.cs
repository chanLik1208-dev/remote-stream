using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RdpB.App.Services;
using RdpB.Core.Pairing;
using RdpB.Core.Protocol;
using RdpB.Core.Transport;
using RdpB.Core.Windows;
using CoreSession = RdpB.Core.Session.Session;

namespace RdpB.Tests;

/// <summary>紅隊攻擊測試(網路層攔截/MITM)——真的寫一個會終止雙邊 TLS 的中間人 relay,
/// 模擬「Client 的流量被重導向到攻擊者控制的機器」(DNS 綁架/ARP 污染/路由劫持達到的效果)。
/// 不是推論,是真的起一個 relay process 去接 Client、再轉接到真正的 Host。不打算 commit。</summary>
[Trait("Category", "RedTeam")]
public class RedTeamMitmRelayTests : IAsyncLifetime
{
    private HostListenerService _realHost = null!;
    private readonly Xunit.Abstractions.ITestOutputHelper _testOutput;

    public RedTeamMitmRelayTests(Xunit.Abstractions.ITestOutputHelper testOutput) => _testOutput = testOutput;

    private void Log(string s)
    {
        _testOutput.WriteLine(s);
        Console.WriteLine(s);
    }

    public async Task InitializeAsync()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"rdpb-mitm-host-{Guid.NewGuid():N}.json");
        _realHost = new HostListenerService(configPath);
        _realHost.Start(port: 0);
        await Task.Delay(100);
    }

    public async Task DisposeAsync() => await _realHost.DisposeAsync();

    /// <summary>終止雙邊 TLS 的 relay:對 client 自稱是 Host(用攻擊者自己的憑證),對真正的
    /// Host 自稱是 Client(也用攻擊者自己的另一張憑證)。兩段 TLS 各自獨立終止在這個 relay 上,
    /// 中間直接轉發明文 bytes——這正是典型 MITM(終止再轉發)相對於單純網路層被動竊聽的差別:
    /// 這個 relay 理論上看得到/能竄改所有流量,不是只能看密文。</summary>
    private sealed class TlsTerminatingMitmRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _realHostAddress;
        private readonly int _realHostPort;
        private readonly X509Certificate2 _relayCertForClient;
        private readonly X509Certificate2 _relayCertForHost;
        private Task? _acceptLoop;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public TlsTerminatingMitmRelay(string realHostAddress, int realHostPort,
            X509Certificate2 relayCertForClient, X509Certificate2 relayCertForHost)
        {
            _realHostAddress = realHostAddress;
            _realHostPort = realHostPort;
            _relayCertForClient = relayCertForClient;
            _relayCertForHost = relayCertForHost;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var clientTcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleAsync(clientTcp);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient clientTcp)
        {
            using var _1 = clientTcp;
            // 對 client 偽裝成 Host:用攻擊者自己的憑證做 server 端 TLS,一律接受任何 client 憑證
            // (模仿真正 RDP-B Host 自己的行為,這樣 client 不會在這一步就因為「對方沒憑證」被擋)。
            using var clientSsl = new SslStream(clientTcp.GetStream(), false, (_, _, _, _) => true);
            try
            {
                await clientSsl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _relayCertForClient,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                });
            }
            catch
            {
                return; // client 根本沒跟 relay 完成 TLS(例如已經釘選真正 Host 指紋、直接拒絕 relay 的假憑證)
            }

            // 對真正的 Host 偽裝成 Client:另外開一條連線,用攻擊者自己的第二張憑證做 client 端 mTLS。
            using var hostTcp = new TcpClient();
            try
            {
                await hostTcp.ConnectAsync(_realHostAddress, _realHostPort);
            }
            catch { return; }

            using var hostSsl = new SslStream(hostTcp.GetStream(), false, (_, _, _, _) => true);
            try
            {
                await hostSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _realHostAddress,
                    ClientCertificates = [_relayCertForHost],
                    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                });
            }
            catch { return; }

            // 雙邊 TLS 都終止在這裡了——接下來單純雙向轉發明文 bytes,relay 理論上能看到/竄改
            // 所有 Frame 內容(這裡沒有真的竄改,只是單純轉發,重點是驗證「轉發」這件事本身
            // 能不能騙過配對流程)。
            var a = clientSsl.CopyToAsync(hostSsl, _cts.Token);
            var b = hostSsl.CopyToAsync(clientSsl, _cts.Token);
            try { await Task.WhenAny(a, b); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_acceptLoop is not null) { try { await _acceptLoop; } catch { } }
        }
    }

    private static X509Certificate2 NewCert(string cn) => TestSupport.EphemeralCertificate.CreateSelfSigned(cn);

    // ── a. TOFU 第一次配對透過 relay ─────────────────────────────────────
    [Fact]
    public async Task Mitm_TofuFirstPairing_ThroughRelay_RejectedByFingerprintBinding()
    {
        var relayCertForClient = NewCert("mitm-pretends-to-be-host");
        var relayCertForHost = NewCert("mitm-pretends-to-be-client");
        await using var relay = new TlsTerminatingMitmRelay("127.0.0.1", _realHost.Port, relayCertForClient, relayCertForHost);

        var clientCert = NewCert("real-client");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, relay.Port);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true); // TOFU:client 這端也先接受任何憑證(還沒配對過)
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [clientCert],
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        });

        // Client 這一端 TLS 握手看到的「Host 憑證」其實是 relay 的 relayCertForClient,不是
        // 真正 Host 的憑證——這正是 PairAsync 拿去算 hostFingerprint 的那個值。
        var transport = TcpTlsTransport.FromAcceptedStream(ssl); // 借用同一個封裝,直接包現成的 SslStream
        var session = new CoreSession(transport);
        session.Start();

        var pairResult = await PairingCoordinator.PairAsync(
            session, _realHost.Pin!, (ssl.RemoteCertificate as X509Certificate2)!, clientCert, CancellationToken.None, TimeSpan.FromSeconds(10));

        Log($"[MITM-a] TOFU 配對透過終止雙邊 TLS 的 relay:PairResult={pairResult}" +
            (pairResult == PairingResult.Success
                ? "——嚴重:relay 成功冒充,配對被攔截成功!"
                : "——relay 沒能騙過配對流程,指紋綁定機制擋住了中間人。"));

        Assert.NotEqual(PairingResult.Success, pairResult);
        await session.DisposeAsync();
    }

    // ── b. 已配對裝置被重導向到 relay ─────────────────────────────────────
    [Fact]
    public async Task Mitm_AlreadyPairedClient_RedirectedToRelay_RejectedAtTlsLayer_PairNeverSent()
    {
        // 先讓一個真正的 client 跟真正的 Host 完整配對一次,拿到真實的 FingerprintHex。
        var pairedConfigPath = Path.Combine(Path.GetTempPath(), $"rdpb-mitm-client-{Guid.NewGuid():N}.json");
        var record = new PinnedHostRecord { Name = "mitm-test", HostAddress = $"127.0.0.1:{_realHost.Port}", FingerprintHex = "" };
        var realClientService = new ClientConnectionService(pairedConfigPath);
        var firstConnect = await realClientService.ConnectAsync(record, () => Task.FromResult<string?>(_realHost.Pin));
        Assert.Equal(ConnectOutcome.Success, firstConnect.Outcome);
        firstConnect.Window?.Dispose();
        Assert.NotEmpty(record.FingerprintHex);
        await Task.Delay(300); // 讓 Host 端串流/slot 收尾,不要讓下面的攻擊卡在 slot 上

        // 現在模擬這台裝置被重導向到攻擊者的 relay(record.HostAddress 指向 relay.Port,
        // 但 record.FingerprintHex 還是真正 Host 的那組,PinPairingPending 還是 false
        // ——代表「已經配對過、理論上不用再問 PIN」的狀態)。
        var relayCertForClient = NewCert("mitm-pretends-to-be-host-2");
        var relayCertForHost = NewCert("mitm-pretends-to-be-client-2");
        await using var relay = new TlsTerminatingMitmRelay("127.0.0.1", _realHost.Port, relayCertForClient, relayCertForHost);

        var redirected = new PinnedHostRecord
        {
            Name = record.Name,
            HostAddress = $"127.0.0.1:{relay.Port}", // 被劫持到 relay,不是真正 Host
            FingerprintHex = record.FingerprintHex,   // 但指紋還是真正 Host 的
            PinPairingPending = false,
        };

        bool pinAsked = false;
        var attackResult = await new ClientConnectionService(pairedConfigPath).ConnectAsync(redirected, () =>
        {
            pinAsked = true;
            return Task.FromResult<string?>(_realHost.Pin);
        });

        Log($"[MITM-b] 已配對裝置被重導向到 relay:Outcome={attackResult.Outcome},PIN 有沒有被問={pinAsked}" +
            (attackResult.Outcome == ConnectOutcome.Success
                ? "——嚴重:relay 冒充真正 Host 成功,已配對裝置被劫持!"
                : "——Client 自己的憑證指紋驗證在 TLS 層就擋下來了,relay 的假憑證過不了關。"));

        Assert.NotEqual(ConnectOutcome.Success, attackResult.Outcome);
        Assert.False(pinAsked); // 連 PIN 都不該問到,應該在憑證驗證那一步就失敗

        if (File.Exists(pairedConfigPath)) File.Delete(pairedConfigPath);
    }

    // ── c. 預匹配模式透過 relay(交叉驗證,這次真的經過一個會轉發流量的 relay)────
    [Fact]
    public async Task Mitm_PreMatchedFingerprint_ThroughRelay_RejectedAtTlsLayer()
    {
        var relayCertForClient = NewCert("mitm-pretends-to-be-host-3");
        var relayCertForHost = NewCert("mitm-pretends-to-be-client-3");
        await using var relay = new TlsTerminatingMitmRelay("127.0.0.1", _realHost.Port, relayCertForClient, relayCertForHost);

        var configPath = Path.Combine(Path.GetTempPath(), $"rdpb-mitm-prematch-{Guid.NewGuid():N}.json");
        var record = new PinnedHostRecord
        {
            Name = "mitm-prematch",
            HostAddress = $"127.0.0.1:{relay.Port}", // 直接指向 relay(模擬被劫持的位址)
            FingerprintHex = _realHost.FingerprintHex!, // 透過信任管道事先知道的是「真正 Host」的指紋
            PinPairingPending = true,
        };

        bool pinAsked = false;
        var result = await new ClientConnectionService(configPath).ConnectAsync(record, () =>
        {
            pinAsked = true;
            return Task.FromResult<string?>(_realHost.Pin);
        });

        Log($"[MITM-c] 預匹配指紋透過 relay 連線:Outcome={result.Outcome},PIN 有沒有被問={pinAsked}");

        Assert.NotEqual(ConnectOutcome.Success, result.Outcome);
        Assert.False(pinAsked);

        if (File.Exists(configPath)) File.Delete(configPath);
    }
}
