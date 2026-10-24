using System.Runtime.InteropServices;

namespace Picklingo.Interop;

/// <summary>
/// Win32 原生接口集合：全局热键、键盘模拟、窗口样式、光标位置、剪贴板序列号、低级鼠标钩子。
/// 所有 P/Invoke 集中在此，业务代码不直接碰原生调用。
/// </summary>
internal static class NativeMethods
{
    // ==================== 全局热键 ====================

    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ==================== 键盘模拟（SendInput） ====================

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_ALT = 0x12;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_C = 0x43;

    // 左右分开的修饰键（查询物理状态用，见 GetAsyncKeyState）
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    /// <summary>
    /// 查询某个键当前是否被按下（最高位=1 表示按下）。
    /// 用于“等待用户手指离开修饰键”：热键触发瞬间 Alt/Ctrl 可能还压着，
    /// 若立即模拟 Ctrl+C，实际组合会变成 Ctrl+Alt+C 之类的错误组合，导致复制失败。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>发送一次按键“按下”或“抬起”事件。</summary>
    public static void SendKey(ushort vk, bool keyUp)
    {
        INPUT input = default;
        input.type = INPUT_KEYBOARD;
        input.U.ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ==================== 窗口样式 / 定位 ====================

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ==================== 光标 / 显示器 / DPI ====================

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>取指定显示器的有效 DPI（物理坐标↔WPF 逻辑坐标换算用）。dpiType 0=有效DPI。</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ==================== 剪贴板 ====================

    /// <summary>剪贴板内容变化时系统会自增此序列号——用于校验“模拟 Ctrl+C 是否真的复制到了东西”。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    // ---- 原生 Win32 剪贴板写入（绕开 WPF/OLE 路径）----
    // 背景（2026-10-11 排查）：WPF 的写入路径会让“下一次其他程序复制”被拖慢约 5 秒；
    // 而手动复制（原生写路径）完全正常——因此还原改用最裸的原生 API。

    public const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>用原生 Win32 API 把 Unicode 文本写进剪贴板；成功返回 true。</summary>
    public static bool TrySetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            int bytes = (text.Length + 1) * 2;
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero)
            {
                return false;
            }

            IntPtr target = GlobalLock(hMem);
            if (target == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return false;
            }

            char[] chars = text.ToCharArray();
            Marshal.Copy(chars, 0, target, chars.Length);
            Marshal.WriteInt16(target, chars.Length * 2, 0); // 结尾 '\0'
            GlobalUnlock(hMem);

            if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
            {
                GlobalFree(hMem); // 交不出去才由我们释放；成功时内存归系统
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// 把当前剪贴板的延迟渲染数据立即物化并移交系统保管，令本进程不再是剪贴板的“活 owner”。
    /// 动机：本进程一旦成为剪贴板 owner，下一方复制时系统需与 owner“交接”，owner 忙则卡约 5 秒。
    /// 返回 0（S_OK）表示成功。
    /// </summary>
    [DllImport("ole32.dll")]
    public static extern int OleFlushClipboard();

    // ==================== 低级鼠标钩子 ====================

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ==================== 系统度量 / 光标 ====================

    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CXPADDEDBORDER = 92;

    public const int IDC_ARROW = 32512;
    public const int IDC_SIZENWSE = 32642;
    public const int IDC_SIZENESW = 32643;
    public const int IDC_SIZEWE = 32644;
    public const int IDC_SIZENS = 32645;
    public const int IDC_SIZEALL = 32646;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>按指定 DPI 取系统度量（PerMonitorV2 下必须用这个，GetSystemMetrics 不跟随每屏 DPI）。</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    /// <summary>立即设置系统光标（WPF 的 Cursor 属性要等下一次鼠标移动才生效，太慢）。</summary>
    public static void SetSystemCursor(int idc)
    {
        SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)idc));
    }
}
