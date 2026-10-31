using System.Diagnostics;
using RdpB.Core.Windows;

namespace RdpB.App.Services;

/// <summary>
/// 管理一條 frpc 隧道的生命週期——下載/定位 frpc.exe、產生設定檔、開子行程、監看意外
/// 結束時用退避重連、停止時乾淨收掉。Host 開著的時候如果 <see cref="FrpSettings.Enabled"/>
/// 是 true,<see cref="HostListenerService"/> 會啟動這個服務,把自己的主監聽埠透過 Kyra
/// 自己架的 frps 曝露出去。
///
/// **安全性重點**:token 絕對不透過命令列參數傳給 frpc.exe——命令列參數在這台機器上任何
/// 使用者都能用工作管理員/Process Explorer 看到,等於把密鑰貼在公布欄上。全部寫進設定檔
/// (frpc.toml),只用 <c>-c &lt;設定檔路徑&gt;</c> 這一個參數,設定檔本身放在
/// %APPDATA%\RdpB\(跟身分憑證同一層,DPAPI 保護的範圍——這層保護的是「別的 Windows 帳號
/// 讀不到」,不是「這個帳號下的其他程式也讀不到」,frpc.exe 本來就得讀到明文 token 才能用,
/// 這是 frp 本身的設計限制,不是這裡能解決的)。
///
/// **穩定性重點**:frpc 意外結束(frps 連不上、網路抖動、token 被改過)不會無限立即重啟——
/// 退避重試,封頂在 1 分鐘,而且累積失敗次數到一定程度就直接放棄不再重試,改成讓
/// <see cref="LastError"/> 反映出來,不要讓一個壞掉的隧道設定造成背景無限重試迴圈長期占用
/// CPU/網路。
/// </summary>
public sealed class FrpTunnelService : IAsyncDisposable
{
    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    ];
    private const int MaxConsecutiveFailures = 8; // 跟 RestartBackoff 搭配,大約撐 3~4 分鐘的重試視窗後就放棄

    private readonly string _configDir;

    // code review 風格的坑,這次直接在設計時就避開:Start/Stop 都要能從不同執行緒安全呼叫
    // (GUI 的 Host 開/關本來就是這樣),所以整個生命週期狀態變化都包在這把鎖裡,不是
    // check-then-act 的裸欄位判斷。
    private readonly object _lock = new();
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _consecutiveFailures;
    private volatile bool _stopRequested;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public event Action? StateChanged;

    public FrpTunnelService(string? configDir = null) =>
        _configDir = configDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB");

    /// <summary>啟動隧道(或確認已經在跑)。<paramref name="localPort"/> 是 Host 自己監聽的
    /// 那個埠(TCP+TLS 主埠,§7 偽裝埠或 QUIC 不適用——frp 轉發的是普通 TCP)。
    /// <paramref name="instanceId"/> 是這台 Host 的身分憑證指紋前幾碼(見 HostListenerService.
    /// FingerprintHex)——真的在 Kyra 機器上炸出來的坑:proxy name 原本整個寫死成固定的
    /// "rdpb",只要她在第二台機器上也開了 frp(同一組 frps 帳號/token,之前真的這樣測過),
    /// 兩邊的 frpc 會用同一個 proxy name 搶同一個 remotePort——frps 只認第一個註冊成功的,
    /// 第二台之後開的那台完全連不上(或者更糟:registered 失敗但自己完全不知道,GUI 還顯示
    /// 「隧道運作中」,因為那只代表 frpc 行程本身有連上 frps 的控制頻道,不代表 proxy
    /// 真的註冊成功)——Client 連公開埠,流量被轉去另一台(可能早就關掉、不是打算連的那台)
    /// Host,TCP 連得上但對面馬上關閉,表現成 TLS 層的「unexpected EOF」,完全看不出真正
    /// 原因是撞名。proxy name 帶上這台機器的身分指紋,不同機器自然不會撞。</summary>
    public void Start(FrpSettings settings, int localPort, string instanceId)
    {
        lock (_lock)
        {
            if (IsRunning || _monitorTask is not null)
            {
                RdpB.Core.Diagnostics.AppLog.WriteLine(
                    $"[FrpTunnelService] Start 被呼叫但已經在跑(IsRunning={IsRunning}、_monitorTask 存在={_monitorTask is not null}),略過,不會開出第二個 frpc——如果妳明明看到多個 frpc.exe,代表不是這裡造成的,是前一次的 frpc 根本沒被 Stop 真正砍掉。");
                return;
            }

            // code review 抓到的真坑:這裡原本完全沒檢查 settings.Enabled——Kyra 在設定頁
            // 把「啟用 frp 內網穿透」取消勾選存檔,ServerAddr/Token 還留在磁碟上沒被清掉,
            // 下次啟動 Host 時這個方法照樣會把隧道開起來,等於使用者明確關掉了卻還是把
            // 本機 Host 曝露到公網。
            if (!settings.Enabled)
            {
                LastError = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.ServerAddr))
            {
                LastError = "frp 隧道沒有設定 frps 伺服器位址,略過";
                StateChanged?.Invoke();
                return;
            }
            if (string.IsNullOrEmpty(settings.Token))
            {
                LastError = "frp 隧道沒有設定 token,為了安全不會在沒有驗證的情況下啟動";
                StateChanged?.Invoke();
                return;
            }

            _stopRequested = false;
            _consecutiveFailures = 0;
            LastError = null;
            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => RunLoopAsync(settings, localPort, instanceId, _monitorCts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? monitorTask;
        lock (_lock)
        {
            _stopRequested = true;
            cts = _monitorCts;
            monitorTask = _monitorTask;
        }

        RdpB.Core.Diagnostics.AppLog.WriteLine(
            $"[FrpTunnelService] StopAsync 被呼叫——_monitorTask 是否存在:{monitorTask is not null}");

        // 真的在 Kyra 機器上靠診斷 log 抓到的 race condition(「按停止 Host 不會收回 frpc」的
        // 真因):以前這裡先 cts.Cancel() 再 KillCurrentProcess()——Cancel() 會讓 RunLoopAsync
        // 裡卡著的 `await process.WaitForExitAsync(ct)` 因為被取消直接丟
        // OperationCanceledException,跳過正常路徑上的 `_process = null`,直接衝進
        // `finally { process.Dispose(); }` 把這個 Process 物件整個丟棄——這個 Dispose 跟緊接在
        // Cancel() 後面執行的 KillCurrentProcess() 幾乎是同時在跑(一個在 RunLoopAsync 的背景
        // Task 續行上,一個在呼叫 StopAsync 的執行緒上),KillCurrentProcess 抓到的還是同一個
        // Process 參照,但物件常常已經在另一條路徑被丟棄,呼叫 Kill() 直接丟
        // "No process is associated with this object."——frpc.exe 實際上完全沒被砍到,RunLoopAsync
        // 那條背景 Task 也因為例外提早結束,沒有人再試第二次。
        //
        // 修法:順序反過來,**先真的 Kill 掉行程,再取消 token**。Kill() 成功的那一刻
        // OS 層面這個行程就真的死了,緊接著 WaitForExitAsync 會正常偵測到行程結束而返回
        // (不是被取消丟例外),走正常路徑清掉 _process,不會有競爭。Cancel() 留到最後,
        // 單純是為了中斷「正在退避等待重試」那個 Task.Delay(delay, ct)——這種情況下
        // _process 本來就是 null(process 已經自然結束了),KillCurrentProcess 提早 return,
        // 跟 Cancel 的執行順序無關,不會有競爭。
        KillCurrentProcess();
        cts?.Cancel();

        if (monitorTask is not null)
        {
            try { await monitorTask.ConfigureAwait(false); } catch { }
        }

        lock (_lock)
        {
            _monitorCts = null;
            _monitorTask = null;
            IsRunning = false;
        }
        RdpB.Core.Diagnostics.AppLog.WriteLine("[FrpTunnelService] StopAsync 完成");
        StateChanged?.Invoke();
    }

    // ── 關閉子行程 ──────────────────────────────────────────────────────────
    // 真的在 Kyra 機器上試過、又真的在 Kyra 機器上炸過的坑:這裡一度改成用
    // AttachConsole + GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT) 送優雅關閉訊號給 frpc,
    // 想讓 frps 立刻釋放 remotePort、不用等心跳逾時(見 StopAsync/RunLoopAsync 呼叫端
    // 上的註解,那次是為了解 frps `start error: port already used` 這個真的遇到的問題)。
    // 結果這段 Windows console 訊號處理連續兩次把「停止 Host」這個操作本身弄到閃退——
    // 不只一次,而且第二次已經是修過明顯的 delegate/GC 坑之後。這類 console attach/signal
    // 的行為在不同啟動方式(雙擊 vs 從終端機啟動)、不同 Windows 版本之間本來就有已知的
    // 細節落差,我這邊沒有能重現的環境,只能靠 Kyra 一來一回測,賭這個優化的風險比省下的
    // 等待時間(frps 心跳逾時,通常幾十秒到一兩分鐘)划不來。退回最單純、已知穩定的強制
    // Kill——「重啟 Host 後 remotePort 可能要等一下才能用」這個限制,改用「換一個公開埠」
    // 繞過(GUI 設定頁的 frp 分類)。
    private void KillCurrentProcess()
    {
        Process? p;
        lock (_lock) { p = _process; }
        if (p is null)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine("[FrpTunnelService] KillCurrentProcess:_process 是 null,沒有東西可以殺(這本身可能就是問題——代表 StopAsync 被呼叫時這個欄位已經空了)");
            return;
        }

        RdpB.Core.Diagnostics.AppLog.WriteLine($"[FrpTunnelService] KillCurrentProcess:準備砍 PID={p.Id},HasExited={SafeHasExited(p)}");
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                RdpB.Core.Diagnostics.AppLog.WriteLine($"[FrpTunnelService] KillCurrentProcess:Kill(PID={p.Id}) 呼叫完成,沒有丟例外");
            }
        }
        catch (Exception ex)
        {
            RdpB.Core.Diagnostics.AppLog.WriteLine($"[FrpTunnelService] KillCurrentProcess:Kill(PID={p.Id}) 丟例外:{ex.GetType().Name}:{ex.Message}");
        }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); } catch { return "未知"; }
    }

    /// <summary>真的在跑的主迴圈:啟動 frpc → 等它結束 → 如果不是主動要求停止,退避一段時間
    /// 再重啟,直到連續失敗太多次放棄。整個迴圈只在一條背景 Task 上跑,不會有兩個 frpc
    /// process 同時存在的疑慮。</summary>
    private async Task RunLoopAsync(FrpSettings settings, int localPort, string instanceId, CancellationToken ct)
    {
        string? configPath = null;
        try
        {
            string frpcPath;
            try
            {
                frpcPath = await FrpcRuntime.EnsureAvailableAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetError($"frpc.exe 準備失敗,隧道沒有啟動:{ex.Message}");
                return;
            }

            configPath = Path.Combine(_configDir, "frpc-generated.toml");
            Directory.CreateDirectory(_configDir);

            while (!ct.IsCancellationRequested)
            {
                // 真的在 Kyra 機器上連環炸出來的坑:instanceId 原本只綁身分憑證指紋,同一台
                // 機器每次都算出一樣的值——這解決了「兩台不同機器撞名」,但反而讓「同一台機器
                // 快速重啟」變成必然撞名:只要 frps 還沒靠心跳逾時發現上一個 frpc 工作階段死了
                // (Kyra 這次除錯過程重啟 Host 的速度遠比這個逾時快),新的 frpc 用同一個名字
                // 登記就會撞到自己前一個工作階段,log 裡看到的 `proxy ... already exists`
                // /`port already used` 都是這個同一個根因的不同症狀,换 remotePort 没用正是
                // 因為卡住的其實是名字,不是埠。這裡額外加一段**每次真的要連 frps 時才產生**
                // 的隨機碼——不只是每次 Start() 一份,是這個 while 迴圈每重試一輪都重新生成,
                // 這樣不管重啟多快、frpc 自己又因為什麼原因斷線重連,新產生的名字永遠是全新的,
                // 不會跟任何舊的(不管是自己的還是別台機器的)撞在一起。
                string sessionNonce = Guid.NewGuid().ToString("N")[..8];
                File.WriteAllText(configPath, BuildTomlConfig(settings, localPort, $"{instanceId}-{sessionNonce}"));

                var psi = new ProcessStartInfo
                {
                    FileName = frpcPath,
                    Arguments = $"-c \"{configPath}\"", // 只有設定檔路徑,token 不出現在命令列(見類別上的註解)
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = _configDir,
                };

                Process process;
                try
                {
                    process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 回傳 null");
                }
                catch (Exception ex)
                {
                    SetError($"啟動 frpc.exe 失敗:{ex.Message}");
                    return;
                }

                // 真的在 Kyra 機器上看到的坑(工作管理員裡 8 個 frpc.exe 殘留):RdpB.exe 被
                // 異常結束(當掉、taskkill、工作管理員強制關閉)時,這個子行程不會跟著自動死掉,
                // 累積起來就是一堆孤兒 frpc.exe 搶同一組 frps 帳號的註冊名額——見
                // ChildProcessJobTracker 類別上的註解,這裡把子行程指派進那個 Job,系統會保證
                // RdpB.exe 不管怎麼死,這個子行程都會被一起收掉。
                ChildProcessJobTracker.Track(process);

                // stdout/stderr 只為了診斷用,讀到的內容可能含伺服器位址等資訊但不會有 token
                // (token 只在設定檔裡)——用 Debug.WriteLine,不是真的存成檔案長期累積。
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) RdpB.Core.Diagnostics.AppLog.WriteLine($"[frpc] {e.Data}"); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) RdpB.Core.Diagnostics.AppLog.WriteLine($"[frpc:stderr] {e.Data}"); };

                // code review 抓到的真坑:這個 Process 物件原本在每輪迴圈結束之後(不管哪個
                // 出口)都只是把 _process 設回 null,從來沒呼叫過 Dispose()——意外結束+退避
                // 重試的情境下(frps 連不上、token 被改錯)最多可以連續累積到
                // MaxConsecutiveFailures(8)個沒釋放的 Process 物件/原生控制代碼,只能等
                // finalizer 才真的回收。用 try/finally 包住「這個 process 的使用期間」,
                // 不管走哪條退出路徑(主動停止/累計失敗放棄/正常走到下一輪重試)都保證釋放,
                // 而且放在 ExitCode 都讀完之後才 Dispose,不會影響上面那些錯誤訊息。
                try
                {
                    lock (_lock) { _process = process; }
                    RdpB.Core.Diagnostics.AppLog.WriteLine($"[FrpTunnelService] 新 frpc.exe 已啟動,PID={process.Id}");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    IsRunning = true;
                    _consecutiveFailures = 0;
                    LastError = null;
                    StateChanged?.Invoke();

                    await process.WaitForExitAsync(ct).ConfigureAwait(false);

                    RdpB.Core.Diagnostics.AppLog.WriteLine($"[FrpTunnelService] frpc.exe(PID={process.Id})的 WaitForExitAsync 返回了,結束代碼={SafeExitCode(process)}");
                    lock (_lock) { _process = null; }
                    IsRunning = false;

                    if (_stopRequested || ct.IsCancellationRequested) return; // 主動要求停止,不重啟

                    _consecutiveFailures++;
                    if (_consecutiveFailures > MaxConsecutiveFailures)
                    {
                        SetError($"frpc.exe 連續結束 {_consecutiveFailures} 次(結束代碼 {process.ExitCode}),放棄重試——檢查 frps 位址/token/網路是否正確。");
                        return;
                    }

                    var delay = RestartBackoff[Math.Min(_consecutiveFailures - 1, RestartBackoff.Length - 1)];
                    LastError = $"frpc.exe 意外結束(結束代碼 {process.ExitCode}),{delay.TotalSeconds:0} 秒後重試(第 {_consecutiveFailures} 次)";
                    StateChanged?.Invoke();

                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        finally
        {
            IsRunning = false;
            // 設定檔裡有明文 token(frpc 本身的限制,見類別上的註解),隧道停掉之後沒有
            // 繼續留著的必要——盡力刪除,刪不掉(例如檔案還被占用)不是致命問題。
            if (configPath is not null)
            {
                try { if (File.Exists(configPath)) File.Delete(configPath); } catch { }
            }
            StateChanged?.Invoke();
        }
    }

    private void SetError(string message)
    {
        LastError = message;
        IsRunning = false;
        StateChanged?.Invoke();
    }

    /// <summary>frp 設定檔用 TOML(frp 0.52+ 的格式)。只設最少必要的欄位——
    /// serverAddr/serverPort/auth.token 控制怎麼連 frps,proxies 區塊宣告「把本機
    /// localPort 轉發到 frps 公開的 remotePort」,type 用 tcp(§7 的 RDP 偽裝/QUIC 不透過
    /// 這條路,frp 轉發的是裸 TCP+TLS,跟直接走 SOCKS5/HTTP CONNECT 代理同一個道理——
    /// Client 端把這個 remotePort 當成 Host 的位址直接連,§6/§7 的偽裝邏輯不適用這種情境,
    /// GUI 那邊應該提醒使用者走 frp 隧道時連線模式選「透過代理」或手動指定不套偽裝)。</summary>
    private static string BuildTomlConfig(FrpSettings settings, int localPort, string instanceId) =>
        $"""
        serverAddr = "{EscapeToml(settings.ServerAddr)}"
        serverPort = {settings.ServerPort}

        [auth]
        method = "token"
        token = "{EscapeToml(settings.Token!)}"

        [[proxies]]
        name = "rdpb-{instanceId}"
        type = "tcp"
        localIP = "127.0.0.1"
        localPort = {localPort}
        remotePort = {settings.RemotePort}
        """;

    private static string EscapeToml(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
