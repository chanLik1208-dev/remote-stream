using System.Runtime.InteropServices;

namespace RdpB.App.Services;

/// <summary>
/// 真的在 Kyra 機器上看到的坑(工作管理員裡同時有 8 個 frpc.exe 殘留):Windows 預設行為是
/// 「父行程死了,子行程不會跟著死」——這個除錯過程 RdpB.exe 被強制關閉(taskkill /F、或任何
/// 不是走正常 StopAsync 流程的結束方式)很多次,每一次 frpc.exe 子行程都變成孤兒繼續跑,
/// 疊到後來好幾個孤兒同時搶同一組 frps 帳號的註冊名額,表現成怎麼換名字/換 port 都沒用的
/// 「port already used」——不管 <see cref="FrpTunnelService"/> 自己的 Kill 邏輯寫得多正確,
/// 只要 RdpB.exe 本身是被異常結束的(當掉、被工作管理員砍、taskkill),那段邏輯根本沒機會
/// 執行,子行程就是會變孤兒。
///
/// 真正能堵住這個問題的做法是 Windows 的 **Job Object**:把子行程(frpc.exe/mihomo.exe)
/// 指派進一個設了 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> 旗標的 job——這個 job 的控制代碼
/// 整個行程生命週期內只開一次、不主動關閉,**作業系統保證**:不管 RdpB.exe 是正常結束、
/// 當掉、還是被外部強制終止,這個 job 的控制代碼最終都會被系統收回,收回的那一刻,所有還
/// 指派在這個 job 裡的子行程會被系統自動全部強制終止——不依賴我們自己的程式碼有沒有機會
/// 執行到清理邏輯,是作業系統層級的保證,這正是這裡需要的。
/// </summary>
internal static class ChildProcessJobTracker
{
    private static readonly IntPtr JobHandle;

    static ChildProcessJobTracker()
    {
        JobHandle = CreateJobObject(IntPtr.Zero, null);
        if (JobHandle == IntPtr.Zero) return; // 建不出來就放棄這個保護,呼叫端的 Kill 邏輯還是原本的防線

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
            SetInformationJobObject(JobHandle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    /// <summary>把子行程指派進這個 job——指派之後,這個行程的生死就跟著 RdpB.exe 綁在一起了,
    /// 不用呼叫端自己記得在每一條結束路徑都清理乾淨。指派失敗(行程已經結束、權限問題等)
    /// 不拋例外,呼叫端原本的 Kill 邏輯仍然是正常情況下的防線,這裡只是多一層系統保證。</summary>
    public static void Track(System.Diagnostics.Process process)
    {
        if (JobHandle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(JobHandle, process.Handle); } catch { /* 盡力而為,不是致命錯誤 */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
