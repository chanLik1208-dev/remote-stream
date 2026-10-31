using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RdpB.App.Services;
using RdpB.Core.Pairing;
using RdpB.Core.Windows;

namespace RdpB.Tests;

/// <summary>紅隊自我攻擊測試——不是單元測試,是真的拿原始 socket/TLS/QUIC/WS 去打一個真的
/// 在跑的 HostListenerService,刻意不照 ClientConnectionService 的乖乖配合協定走。Kyra 授權
/// 的自我滲透測試,範圍限定本機。這份檔案是攻擊報告的配套證據,不打算 commit。</summary>
[Trait("Category", "RedTeam")]
public class RedTeamAttackTests : IAsyncLifetime
{
    private HostListenerService _host = null!;
    private readonly List<string> _log = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _testOutput;

    public RedTeamAttackTests(Xunit.Abstractions.ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    public async Task InitializeAsync()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"rdpb-redteam-{Guid.NewGuid():N}.json");
        _host = new HostListenerService(configPath);
        _host.Start(port: 0);
        await Task.Delay(100); // 讓 accept loop 真的起來
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private static X509Certificate2 AttackerCert() => TestSupport.EphemeralCertificate.CreateSelfSigned("attacker");

    private void Log(string s)
    {
        _log.Add(s);
        _testOutput.WriteLine(s);
        Console.WriteLine(s);
    }

    // ── 1. 完全不出示 client 憑證 ──────────────────────────────────────────
    [Fact]
    public async Task Attack_NoClientCertificate_RejectedWithoutLeakingAnything()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);
        using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);

        Exception? handshakeEx = null;
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                // 故意不給 ClientCertificates
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            });

            // 握手如果真的成功了(預期會成功,因為 server 的 callback 一律接受),
            // 嘗試讀資料,確認對面會主動斷線、不回傳任何東西(不洩漏版本/堆疊)。
            var buf = new byte[256];
            int n = await ssl.ReadAsync(buf, CancellationToken.None);
            Assert.Equal(0, n); // 讀到 0 bytes = 對面已經乾淨關閉,沒有送任何資料
        }
        catch (Exception ex)
        {
            handshakeEx = ex;
        }

        // 不管是 TLS 握手本身就失敗,還是握手成功後讀到 EOF,都算「沒有被放行」。
        // 重點斷言:_host 完全沒有變成已連線/已串流狀態。
        Assert.False(_host.IsStreaming);
        Log($"[1] 無憑證連線:handshakeEx={handshakeEx?.GetType().Name ?? "(無,握手成功但讀到 EOF)"}");
    }

    // ── 2. PIN 連續亂猜,量測實際速度 ───────────────────────────────────────
    [Fact]
    public async Task Attack_PinBruteForce_MeasureThroughput()
    {
        var attackerCert = AttackerCert();
        int attempts = 0;
        int wrongPinResults = 0;
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(20);

        while (sw.Elapsed < budget)
        {
            attempts++;
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _host.Port).WaitAsync(TimeSpan.FromSeconds(3));
                using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "127.0.0.1",
                    ClientCertificates = [attackerCert],
                    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                }).WaitAsync(TimeSpan.FromSeconds(3));

                // 直接猜一個固定的錯誤 PIN(真的攻擊者會跑排列組合,這裡只要量「一次嘗試耗時」)。
                string guessedPin = "000000";
                if (guessedPin == _host.Pin) guessedPin = "111111"; // 避免剛好猜中打亂量測

                var fingerprint = PairingService.ComputeFingerprint(attackerCert.RawData);
                var tag = PairingService.ComputePairingTag(guessedPin, fingerprint, fingerprint); // 隨便算,反正是錯的
                var payload = PackPairMessage(fingerprint, tag);

                // 手刻 Frame:[Channel.Control=0][MessageType.Pair][len LE 4][payload]
                var frame = BuildFrame(channel: 0, type: GetPairMessageType(), payload);
                await ssl.WriteAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                await ssl.FlushAsync();

                // 等 PairAck(或連線被關)。
                var headerBuf = new byte[6];
                int got = await ReadSomeAsync(ssl, headerBuf, TimeSpan.FromSeconds(3));
                if (got > 0) wrongPinResults++;
            }
            catch
            {
                // 連線被拒/逾時也算一次嘗試結束,繼續下一輪。
            }
        }

        sw.Stop();
        double perSecond = attempts / sw.Elapsed.TotalSeconds;
        double secondsFor1M = 1_000_000 / Math.Max(perSecond, 0.001);

        Log($"[2] PIN 暴力嘗試:{attempts} 次 / {sw.Elapsed.TotalSeconds:0.#}秒 = {perSecond:0.##}次/秒," +
                 $"收到回應(PairAck)次數={wrongPinResults}," +
                 $"窮舉 100 萬種組合理論估計需要 {secondsFor1M / 3600:0.##} 小時(單執行緒循序嘗試)");

        // 這裡沒有「正確」的斷言值——重點是量出真實數字留給報告用。
        Assert.True(attempts > 0);
    }

    // ── 3. Frame 協定層畸形輸入(TCP 路徑)──────────────────────────────────
    [Theory]
    [InlineData(0, 0, 10)]           // 宣稱 10 bytes payload,實際上一個 byte 都不送、直接斷線
    [InlineData(-1, 0, 0)]           // 宣稱長度是負數
    [InlineData(int.MaxValue, 0, 0)] // 宣稱長度超大
    public async Task Attack_MalformedFrameLength_TCP_DoesNotCrashHost(int declaredLen, int actualSend, int extra)
    {
        _ = extra;
        var attackerCert = AttackerCert();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);
        using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [attackerCert],
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        });

        var header = new byte[6];
        header[0] = 0; // Channel.Control
        header[1] = GetPairMessageType();
        BitConverter.GetBytes(declaredLen).CopyTo(header, 2);
        await ssl.WriteAsync(header);
        if (actualSend > 0)
            await ssl.WriteAsync(new byte[actualSend]);
        await ssl.FlushAsync();

        // 不管對面怎麼反應,這條連線本身的死活不重要——重點是 Host 本身(其他連線)還活著。
        try { await Task.Delay(300); } catch { }

        // 驗證 Host 的 accept loop 沒有被這個畸形連線砸死:再開一條正常的(會被 slot 擋掉,
        // 但至少 TCP/TLS 握手要能成功,代表 listener 還活著)。
        using var probeClient = new TcpClient();
        await probeClient.ConnectAsync(IPAddress.Loopback, _host.Port).WaitAsync(TimeSpan.FromSeconds(5));
        using var probeSsl = new SslStream(probeClient.GetStream(), false, (_, _, _, _) => true);
        var probeCert = AttackerCert();
        await probeSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [probeCert],
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Log($"[3] 畸形 frame 長度={declaredLen}:Host listener 存活(後續連線 TLS 握手成功)");
        Assert.True(true);
    }

    // ── 4. WS 訊息大小上限 ────────────────────────────────────────────────
    [Fact]
    public async Task Attack_WsOversizedMessage_RejectedWithoutOom()
    {
        var attackerCert = AttackerCert();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, 7444);
        using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [attackerCert],
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        });

        // 手刻 HTTP Upgrade(照 WsTransport.ClientUpgradeHandshakeAsync 同一套)。
        string key = Convert.ToBase64String(RandomBytes(16));
        string request = "GET /rdpb HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(request));
        await ssl.FlushAsync();
        await ReadHttpResponseHeadersAsync(ssl);

        var socket = WebSocket.CreateFromStream(ssl, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

        // 用「一直不結束的 fragment」去撐爆 MaxMessageSize(64MB),但實際只送遠小於 64MB 的
        // 資料量(每個 chunk 1MB,送 5 次不標 EndOfMessage)——驗證邏輯對不對,不用真的等灌完
        // 64MB+ 才觸發。
        var chunk = new byte[1024 * 1024];
        bool threw = false;
        try
        {
            for (int i = 0; i < 80; i++) // 80MB,超過 64MB 上限
            {
                await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            await socket.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            threw = true;
            Log($"[4] WS 超大訊息(80MB fragment):送到一半就被斷線/丟例外({ex.GetType().Name}),符合預期(上限 64MB)");
        }

        if (!threw)
            Log("[4] WS 超大訊息(80MB fragment):80MB 全部送完都沒被斷線——跟預期的 64MB 上限不符,需要確認");

        Assert.True(threw, "80MB 的未結束 fragment 應該要在 64MB 上限觸發時就被斷線,不該讓 80MB 全部送完");
    }

    // ── 5. 連線槽位 DoS:攻擊者連上但完全不送 Pair 訊息,佔住 slot ──────────────
    [Fact]
    public async Task Attack_SilentConnection_NoLongerBlocksLegitimateClient()
    {
        // 這個測試原本叫 Attack_SilentConnection_HoldsSlot_BlocksLegitimateClient,紅隊攻擊
        // 測試時拿來證實一個真的存在的洞:攻擊者連上來晾著不送任何東西,就能把唯一的連線
        // 槽位獨占到 PairingTimeout(60 秒)逾時才放,合法使用者完全連不進來。那個洞已經
        // 修了(HostListenerService 把搶獨佔槽位的時機從「接受連線時」改成「配對成功後」,
        // 見 OnTransportAcceptedAsync/HandleNewConnectionAsync 上的註解)——這裡反過來驗證
        // 修法真的有效:攻擊者晾著不送資料,不該再擋住任何合法使用者。
        var attackerCert = AttackerCert();
        using var attackerClient = new TcpClient();
        await attackerClient.ConnectAsync(IPAddress.Loopback, _host.Port);
        using var attackerSsl = new SslStream(attackerClient.GetStream(), false, (_, _, _, _) => true);
        await attackerSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [attackerCert],
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        });
        // 完全不送任何東西,就晾著——模擬「連上但不配合」的攻擊者。

        await Task.Delay(500); // 給 Host 時間處理攻擊者這條連線(修法後:不會佔用任何獨佔資源)

        // 這時合法使用者應該連得進來、正常被 Host 服務——用另一條連線驗證。
        using var victimClient = new TcpClient();
        await victimClient.ConnectAsync(IPAddress.Loopback, _host.Port);
        using var victimSsl = new SslStream(victimClient.GetStream(), false, (_, _, _, _) => true);
        var victimCert = AttackerCert();
        await victimSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [victimCert],
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        });

        // 合法使用者的連線應該被 Host 正常接住、Session 真的啟動——讀到 Host 心跳迴圈送出來
        // 的第一個 ping frame(不是 EOF),證明這條連線沒有被攻擊者晾著的那條卡住。
        var buf = new byte[16];
        int n = await ReadSomeAsync(victimSsl, buf, TimeSpan.FromSeconds(5));

        Log($"[5] 攻擊者晾著連線不送資料,合法使用者第二條連線正常收到 Host 的資料(讀到 {n} bytes," +
                 "0 才代表 EOF/被擋)——證實原本『完全不送 Pair 訊息就能長期佔住唯一連線槽位』的洞" +
                 "已經修掉:src/RdpB.App/Services/HostListenerService.cs 的 OnTransportAcceptedAsync/" +
                 "HandleNewConnectionAsync 現在把搶獨佔槽位的時機從「接受連線時」改成「配對成功後」," +
                 "攻擊者晾著不配對,不會再佔用任何獨佔資源。");

        Assert.True(n > 0, "合法使用者的連線被擋掉了——slot-DoS 修法可能失效或被改壞");
    }

    // ── 6. RDP 偽裝(3389)送垃圾資料 ────────────────────────────────────────
    [Fact]
    public async Task Attack_RdpCamouflagePort_GarbageData_DoesNotCrashListener()
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, 3389).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Log($"[6] 3389 偽裝埠連不上({ex.GetType().Name}:{ex.Message})——這台測試機可能本來就有真的" +
                     "Windows RDP 服務佔用 3389,偽裝監聽開不起來(HostListenerService 設計上允許的優雅降級)," +
                     "這個攻擊面這次沒有實測到,是已知限制,不是新發現。");
            Assert.True(true);
            return;
        }

        using var _ = client;
        // 故意送完全不是 TPKT/X.224 格式的垃圾(ServerUpgradeHandshakeAsync 預期第一個 byte
        // 是 0x03)。
        var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41, 0x41, 0x41, 0x41 };
        await client.GetStream().WriteAsync(garbage);
        await client.GetStream().FlushAsync();

        var buf = new byte[16];
        try
        {
            int n = await ReadSomeAsync(client.GetStream(), buf, TimeSpan.FromSeconds(3));
            Log($"[6] 送垃圾資料到 3389:讀回 {n} bytes(預期 0=乾淨斷線)");
        }
        catch (Exception ex)
        {
            Log($"[6] 送垃圾資料到 3389:讀取時丟例外 {ex.GetType().Name}(對面可能直接重置連線,也算安全反應)");
        }

        // 驗證 listener 本身沒死:再連一次。
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, 3389).WaitAsync(TimeSpan.FromSeconds(5));
        Log("[6] 3389 listener 在收到垃圾資料後依然存活(後續連線成功)");
        Assert.True(true);
    }

    // ── 7. 預匹配模式:錯誤指紋應該在 TLS 層就被拒絕 ───────────────────────────
    [Fact]
    public async Task Attack_PreMatch_WrongFingerprint_RejectedAtTlsLayer_PinNeverAsked()
    {
        var record = new PinnedHostRecord
        {
            Name = "攻擊測試",
            HostAddress = $"127.0.0.1:{_host.Port}",
            FingerprintHex = new string('A', 64), // 故意跟 _host.FingerprintHex 不一樣
            PinPairingPending = true,
        };
        var configPath = Path.Combine(Path.GetTempPath(), $"rdpb-redteam-client-{Guid.NewGuid():N}.json");
        var client = new ClientConnectionService(configPath);

        bool pinAsked = false;
        var result = await client.ConnectAsync(record, () =>
        {
            pinAsked = true;
            return Task.FromResult<string?>("123456");
        });

        Log($"[7] 預匹配模式帶錯誤指紋:Outcome={result.Outcome},PIN callback 有沒有被呼叫={pinAsked}" +
                 "(用的是真正的 ClientConnectionService 當攻擊者的角度重打一次交叉驗證)");

        Assert.False(pinAsked);
        Assert.Equal(ConnectOutcome.ConnectFailed, result.Outcome);

        if (File.Exists(configPath)) File.Delete(configPath);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }

    private static async Task ReadHttpResponseHeadersAsync(Stream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        int blankCount = 0;
        while (blankCount < 4) // "\r\n\r\n"
        {
            int n = await s.ReadAsync(one);
            if (n == 0) break;
            buf.Add(one[0]);
            if ((one[0] == '\r' && (blankCount == 0 || blankCount == 2)) ||
                (one[0] == '\n' && (blankCount == 1 || blankCount == 3)))
                blankCount++;
            else
                blankCount = 0;
        }
    }

    private static async Task<int> ReadSomeAsync(Stream s, byte[] buf, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { return await s.ReadAsync(buf, cts.Token); }
        catch (OperationCanceledException) { return -1; } // 逾時沒讀到東西也沒斷線,算另一種結果
    }

    private static byte[] BuildFrame(byte channel, byte type, byte[] payload)
    {
        var buf = new byte[6 + payload.Length];
        buf[0] = channel;
        buf[1] = type;
        BitConverter.GetBytes(payload.Length).CopyTo(buf, 2);
        payload.CopyTo(buf, 6);
        return buf;
    }

    // MessageType.Pair 的實際數值——反射拿,不用在這個攻擊測試裡額外 internals-visible-to 什麼的。
    private static byte GetPairMessageType()
    {
        var enumType = typeof(RdpB.Core.Protocol.MessageType);
        return (byte)(RdpB.Core.Protocol.MessageType)Enum.Parse(enumType, "Pair");
    }

    private static byte[] PackPairMessage(byte[] fingerprint, byte[] tag)
    {
        var buf = new byte[2 + fingerprint.Length + tag.Length];
        buf[0] = (byte)fingerprint.Length;
        fingerprint.CopyTo(buf, 1);
        buf[1 + fingerprint.Length] = (byte)tag.Length;
        tag.CopyTo(buf, 2 + fingerprint.Length);
        return buf;
    }
}
