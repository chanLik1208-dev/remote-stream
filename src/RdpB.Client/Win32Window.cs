using System.Runtime.InteropServices;

namespace RdpB.Client;

/// <summary>
/// <see cref="StreamWindow"/> 專用的原生視窗/GDI P/Invoke——跟 <see cref="Input.Native"/>
/// (滑鼠/鍵盤/hook)分開放,那邊是輸入相關,這邊是「開一個真的 Win32 視窗、把 BGRA 畫面畫
/// 上去」相關,職責不同所以分成兩個檔案。
/// </summary>
internal static class Win32Window
{
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;

    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;
    public const uint WM_NEWFRAME = WM_APP + 1; // 自訂訊息:解碼出一張新畫面了,該重繪
    public const uint WM_DECODE_PENDING = WM_APP + 2; // 自訂訊息:佇列裡有新的 H.264 封包等著解碼
    public const uint WM_TAMPERING_DETECTED = WM_APP + 3; // 自訂訊息:AntiTamperMonitor 在背景執行緒上抓到東西,轉送到視窗自己的執行緒處理
    public const uint WM_HUD_TICK = WM_APP + 4; // 自訂訊息:HUD 更新頻率 Timer 到點,該結算實際 fps + 重繪
    public const uint WM_BYE_RECEIVED = WM_APP + 5; // 自訂訊息:收到 Host 主動送來的 Bye(附斷線原因),轉送到視窗自己的執行緒彈窗

    public const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;

    public const int SW_SHOW = 5;
    public const int SRCCOPY = 0x00CC0020;
    public const int BI_RGB = 0;
    public const int DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public Input.Native.RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);
    public const uint MB_OK = 0x0, MB_ICONWARNING = 0x30;

    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out Input.Native.RECT lpRect);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    // 計畫 §8 Kyra 回報的真坑:「壞 fps 時 HUD 會閃爍」——OnPaint 原本直接把影像幀跟 HUD
    // 文字一筆一筆畫在真正的視窗 DC 上(StretchDIBits 一次、DrawHudBlocks 裡好幾次
    // AlphaBlend),沒有離屏緩衝區——使用者在螢幕上看到的是這些 GDI 呼叫一筆一筆疊上去的
    // 過程,不是一次性的完成品。畫面更新頻率低、不規律(低 fps/網路不穩)時,這個「正在畫」
    // 的中間狀態被看到的機率變高,表現成閃爍。標準解法是離屏合成:所有東西先畫進一塊記憶體
    // DC,畫完之後用這個 BitBlt 一次性搬到真正的視窗 DC,使用者只會看到最終結果,不會看到
    // 中間過程。
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int StretchDIBits(
        IntPtr hdc, int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight,
        byte[] bits, ref BITMAPINFOHEADER bmi, int usage, int rop);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr hdc, uint color);

    public const int TRANSPARENT = 1;

    // §8「連線中斷,正在重連…」疊字覆蓋層用——solid 底色 banner(不是真的半透明 alpha blend,
    // GDI 做那個要另外疊一張 AlphaBlend 的離屏 bitmap,畫面都已經停格了,犧牲一點視覺效果
    // 換取簡單可靠,老實承認這不是計畫原文想像的那種半透明疊層)。
    [DllImport("user32.dll")] public static extern bool FillRect(IntPtr hdc, ref Input.Native.RECT lprc, IntPtr hbr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref Input.Native.RECT lprc, uint format);

    public const uint DT_CENTER = 0x0001, DT_VCENTER = 0x0004, DT_SINGLELINE = 0x0020, DT_WORDBREAK = 0x0010;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowText(IntPtr hWnd, string lpString);

    /// <summary>Windows 10 2004(build 19041)+ 才支援 <see cref="WDA_EXCLUDEFROMCAPTURE"/>。
    /// 在更舊的版本上呼叫會回傳 false(不是例外),呼叫端要自己判斷結果、不能假設一定成功——
    /// 不是真的 HDCP,是 OS 原生的「排除於擷取」機制,見 PrivacySettings.BlockScreenCapture
    /// 上的註解。</summary>
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // ── HUD 真半透明(AlphaBlend)+ 可調字級(CreateFont)──────────────────────
    // 計畫 §4/§8 的 HUD 透明度設定——跟「連線中斷」banner 那種故意簡化成 solid 色塊不同
    // (這裡的 HUD 本來就是疊在畫面上長時間看的東西,值得花這個工夫做真的半透明):建一個
    // 跟 HUD 方框同尺寸的記憶體 DC + 32bpp DIB section,填滿背景色,用 msimg32.dll 的
    // AlphaBlend 把這塊離屏 bitmap 以指定透明度疊到真正的畫面 DC 上。
    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    public const byte AC_SRC_OVER = 0x00;

    [DllImport("msimg32.dll")]
    public static extern bool AlphaBlend(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc,
        BLENDFUNCTION blendFunction);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER bmi, int usage, out IntPtr bits, IntPtr hSection, int offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFont(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string? pszFaceName);

    public const uint DEFAULT_CHARSET = 1, OUT_DEFAULT_PRECIS = 0, CLIP_DEFAULT_PRECIS = 0,
        DEFAULT_QUALITY = 0, DEFAULT_PITCH = 0, FF_DONTCARE = 0;
}
