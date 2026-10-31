using System.Runtime.InteropServices;
using RdpB.Core.Protocol;

namespace RdpB.Client.Input;

/// <summary>
/// 計畫 §9/§5「手把」——Host 端的注入(<c>RdpB.Host.Input.GamepadSink</c>,灌進虛擬
/// Xbox 360 手把)老早就做完了,但 Client 端讀本機手把、送過去那一半完全沒有,整條路斷在
/// 這裡(計畫盤點時才發現這個落差)。這個類別補上那一半。
///
/// **用 XInput,不是 DirectInput/RawInput**:plan §9 原文搬的是 remote-stream 的
/// `GamepadSender`/`GamepadSink`,那組本來就是 XInput 架構(Xbox 相容手把,Windows 內建
/// 驅動,不用額外裝任何東西)——<see cref="GamepadSink.Handle"/> 的 12-byte payload 格式
/// (buttons/triggers/thumbsticks)本來就是直接照抄 XINPUT_GAMEPAD 的欄位版面,這裡讀
/// XInputGetState 出來的結構直接複製進 payload,不需要任何位元重新對應。
///
/// **範圍老實講清楚**:只支援 XInput 相容手把(Xbox 系列、大多數第三方「Xbox 模式」手把),
/// 不支援純 DirectInput 的老手把,也只讀第一個手把(dwUserIndex=0)——多手把、
/// 非 XInput 手把都是之後才考慮的範圍。</summary>
public sealed class GamepadSender : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    // xinput1_4.dll 是 Windows 8+ 內建的版本——這個方案本來就只支援 Windows 10/11
    // (計畫文件開宗明義),不用照顧更舊的 xinput9_1_0.dll 相容性。
    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

    private const int ErrorSuccess = 0;

    // 手把輸入的延遲敏感度跟鍵鼠差不多(遊戲場景尤其在意),~60Hz 跟畫面更新頻率同量級,
    // 不像音訊/畫面那樣需要另外調校——沒變化時 Poll() 本身會跳過送出,閒置時幾乎零頻寬成本。
    private const int PollIntervalMs = 16;

    private readonly Action<Channel, MessageType, byte[]> _send;
    private readonly Func<bool> _isActive;
    private System.Threading.Timer? _timer;
    private uint _lastPacketNumber = uint.MaxValue; // 第一次一定送一次,不管封包號碼湊巧是多少
    private bool _wasConnected;

    /// <summary>code review 抓到的坑:System.Threading.Timer **不保證 callback 不重疊**——
    /// 如果某次 Poll()(例如 _send 卡住、或剛好遇到 GC 暫停)跑超過 PollIntervalMs,下一次
    /// tick 可能在它還沒跑完時又觸發一次,兩次同時讀寫 _lastPacketNumber/_wasConnected 沒有
    /// 保護,最壞情況重複送一次、或手把拔掉時的歸零狀態晚一拍送出。用 Interlocked 當一個
    /// 簡單的重入旗標——寧可整個跳過這一輪(下一輪 16ms 後馬上又會跑),也不要兩輪同時跑。</summary>
    private int _polling;

    /// <param name="send">送到 Host 的 callback——跟 StreamWindow.SendInput 同一個簽名,
    /// 不用另外接 Session。</param>
    /// <param name="isActive">是不是「擷取中」(跟鍵鼠同一個 <see cref="CaptureStateMachine"/>
    /// 狀態)——沒在操作時不送,避免搖桿零位漂移這類雜訊一直往外送。</param>
    public GamepadSender(Action<Channel, MessageType, byte[]> send, Func<bool> isActive)
    {
        _send = send;
        _isActive = isActive;
    }

    public void Start() => _timer = new System.Threading.Timer(_ => Poll(), null, 0, PollIntervalMs);

    /// <summary>真的在 Kyra 機器上炸出來的坑:這個方法是 System.Threading.Timer 的 callback,
    /// 跑在 ThreadPool 背景執行緒上——.NET 對背景執行緒上沒接住的例外是直接讓**整個行程**
    /// 崩潰,不是優雅地只斷掉這個功能。原本完全沒有 catch,只要 XInputGetState(呼叫
    /// xinput1_4.dll)在某台機器上因為任何原因丟例外(DLL 載不到、驅動問題……),表現就是
    /// 整個 RDP-B 閃退,而且時間點剛好卡在串流視窗剛初始化、GamepadSender.Start() 剛被
    /// 呼叫那一刻——跟「配對完沒反應然後閃退」這個回報完全吻合。
    ///
    /// 修法:接住任何例外,記一次 log(不是每 16ms 都洗一次,DLL 載不到不會中途自己好),
    /// 直接停掉這個 Timer——手把本來就是錦上添花的功能,載入失敗不該連累整條串流連線。</summary>
    private volatile bool _gaveUp;

    private void Poll()
    {
        if (_gaveUp || !_isActive()) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return; // 上一輪還沒跑完,這輪跳過
        try
        {
            PollCore();
        }
        catch (Exception ex)
        {
            _gaveUp = true;
            RdpB.Core.Diagnostics.AppLog.WriteLine(
                $"[GamepadSender] 讀取手把失敗,放棄這個功能(不影響串流本身):{ex}");
            _timer?.Dispose();
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void PollCore()
    {
        int result = XInputGetState(0, out var state);
        if (result != ErrorSuccess)
        {
            if (_wasConnected)
            {
                // 手把剛拔掉——送一次全部歸零的狀態,避免 Host 端的虛擬手把卡在拔掉前的
                // 最後狀態(例如搖桿卡在推到底,角色會一直往同個方向跑,直到下次插上手把
                // 才會被新狀態蓋掉)。
                SendPayload(default);
                _wasConnected = false;
                _lastPacketNumber = uint.MaxValue;
            }
            return;
        }
        _wasConnected = true;

        if (state.dwPacketNumber == _lastPacketNumber) return; // 沒變就不浪費頻寬送
        _lastPacketNumber = state.dwPacketNumber;

        SendPayload(state.Gamepad);
    }

    private void SendPayload(XInputGamepad g) =>
        _send(Channel.Input, MessageType.Pad,
            BuildPayload(g.wButtons, g.bLeftTrigger, g.bRightTrigger, g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY));

    /// <summary>跟 GamepadSink.Handle 的 payload 格式一字不差對應(小端):
    /// buttons(2) + leftTrigger(1) + rightTrigger(1) + thumbLX(2) + thumbLY(2)
    /// + thumbRX(2) + thumbRY(2) = 12 bytes。拆成 public static 方法,不用真的插著手把
    /// 呼叫 XInputGetState 也能單獨測這段序列化邏輯對不對(見 GamepadSenderTests)。</summary>
    public static byte[] BuildPayload(ushort buttons, byte leftTrigger, byte rightTrigger,
        short thumbLX, short thumbLY, short thumbRX, short thumbRY)
    {
        var payload = new byte[12];
        payload[0] = (byte)(buttons & 0xFF);
        payload[1] = (byte)(buttons >> 8);
        payload[2] = leftTrigger;
        payload[3] = rightTrigger;
        WriteI16(payload, 4, thumbLX);
        WriteI16(payload, 6, thumbLY);
        WriteI16(payload, 8, thumbRX);
        WriteI16(payload, 10, thumbRY);
        return payload;
    }

    private static void WriteI16(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public void Dispose() => _timer?.Dispose();
}
