using System.Net.Sockets;
using RdpB.App.Services;
using RdpB.App.ViewModels;
using RdpB.Core.Transport;

namespace RdpB.Tests;

/// <summary>對應 Kyra 的回饋:「host也要做gui和隱藏在這裏」——這裡測的是面板背後的
/// HostListenerService,跟 Avalonia 完全分開。真的用 TcpTlsTransport 連一次,不是 mock。</summary>
public class HostListenerServiceTests
{
    [Fact]
    public async Task Start_GeneratesPinAndListensOnAPort()
    {
        await using var service = new HostListenerService();
        service.Start(port: 0); // 0 = 系統自動挑一個空的埠,測試用

        Assert.True(service.IsRunning);
        Assert.NotNull(service.Pin);
        Assert.Equal(6, service.Pin!.Length);
        Assert.True(service.Port > 0);

        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ClearsRunningStateAndPin()
    {
        var service = new HostListenerService();
        service.Start(port: 0);
        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.Null(service.Pin);
        Assert.Equal(0, service.ConnectedClientCount);
    }

    [Fact]
    public async Task ClientConnects_IncrementsConnectedClientCount()
    {
        await using var service = new HostListenerService();
        service.Start(port: 0);

        var stateChanges = new List<int>();
        service.StateChanged += () => stateChanges.Add(service.ConnectedClientCount);

        await using var client = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", service.Port, proxy: null, validateServerCertificate: _ => true);

        // 給 accept loop 一點時間把 ConnectedClientCount 加上去
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.ConnectedClientCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(1, service.ConnectedClientCount);
        // 確認 StateChanged 真的有觸發、而且觸發時 ConnectedClientCount 已經是 1
        // (code review 抓到這份清單原本收集了卻沒斷言,是沒意義的測試樁)。
        Assert.Contains(1, stateChanges);

        await service.StopAsync();
    }

    [Fact]
    public async Task OneBadConnection_DoesNotKillAcceptLoop_LaterGoodClientStillConnects()
    {
        // code review 抓到的真坑:try/catch 原本包整個 while,一次 TLS handshake 失敗的連線
        // (這裡用一個隨便送垃圾資料就斷線的 TCP 連線模擬)會讓整條 accept loop 永久死掉,
        // IsRunning 卻還顯示開著——這個測試確認修好之後,爛連線不會拖累後面的真連線。
        await using var service = new HostListenerService();
        service.Start(port: 0);

        using (var garbage = new TcpClient())
        {
            await garbage.ConnectAsync("127.0.0.1", service.Port);
            await garbage.GetStream().WriteAsync(new byte[] { 0x00, 0x01, 0x02, 0x03 });
            garbage.Close(); // 斷在 TLS handshake 中間,逼 AcceptAsync 那端丟例外
        }

        await Task.Delay(300); // 給 accept loop 一點時間處理完這次失敗、繼續回到迴圈頂端

        await using var goodClient = await TcpTlsTransport.ConnectAsync(
            "127.0.0.1", service.Port, proxy: null, validateServerCertificate: _ => true);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.ConnectedClientCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(service.IsRunning); // accept loop 還活著
        Assert.Equal(1, service.ConnectedClientCount); // 真連線還是算得到

        await service.StopAsync();
    }

    [Fact]
    public async Task HostViewModel_TogglesStateCorrectly()
    {
        await using var service = new HostListenerService();
        var vm = new HostViewModel(service);

        Assert.False(vm.IsRunning);

        await vm.StartAsync(port: 0); // 0,不要去搶真正的預設埠(平行測試、或 Kyra 自己開著 Host 時會撞號)
        Assert.True(vm.IsRunning);
        Assert.NotNull(vm.Pin);
        Assert.False(vm.IsStarting); // 啟動完成後旗標該清掉,不然「啟動中…」的按鈕文字會卡住

        await vm.StopAsync();
        Assert.False(vm.IsRunning);
        Assert.Null(vm.Pin);
    }

    [Fact]
    public async Task HostViewModel_StartAsync_RunsBlockingWorkOnBackgroundThread_NotCallerThread()
    {
        // 真的有人回報過的坑:啟動 Host 會讓整個 UI「沒有回應」,根因是
        // HostListenerService.Start() 原本在呼叫端(Avalonia UI 執行緒)同步跑,裡面
        // QuicListener.ListenAsync 在某些機器上會卡住。這裡驗證 StartAsync 呼叫後、
        // await 完成前,IsStarting 已經是 true(代表同步的 _service.Start() 已經被丟進
        // Task.Run,呼叫端這一行本身沒有被卡住——如果 StartAsync 其實還是同步阻塞的,
        // 這一行根本不會執行到,因為上一行的呼叫早就卡死了,整個測試會逾時,不是斷言失敗)。
        await using var service = new HostListenerService();
        var vm = new HostViewModel(service);

        var startTask = vm.StartAsync(port: 0);
        Assert.True(vm.IsStarting); // 呼叫端馬上拿回控制權,不是卡在這裡等 Start() 做完

        await startTask;

        Assert.True(vm.IsRunning);
        Assert.False(vm.IsStarting);

        await vm.StopAsync();
    }
}
