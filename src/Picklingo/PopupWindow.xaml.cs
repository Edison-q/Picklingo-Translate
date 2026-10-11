using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Picklingo.Interop;
using Clipboard = System.Windows.Clipboard;

namespace Picklingo;

/// <summary>
/// 鼠标旁的翻译卡片：不抢焦点、置顶、无边框；点卡片外任意处自动消失。
/// </summary>
public partial class PopupWindow : Window
{
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _lastMonitor = IntPtr.Zero;

    private IntPtr _hook = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _hookProc;

    public PopupWindow()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // 关键：不激活 + 不出现在 Alt+Tab
        long ex = (long)NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
    }

    /// <summary>在鼠标附近显示卡片（先显示原文 + 加载态，译文稍后填充）。</summary>
    public void ShowAtCursor(string source, bool truncated)
    {
        SourceText.Text = source;
        ResultText.Text = string.Empty;
        StatusText.Text = truncated ? "内容较长，仅翻译前 5000 字符…" : "翻译中…";
        CopyButton.Content = "复制";
        CopyButton.IsEnabled = false;

        if (!IsVisible)
        {
            Opacity = 0; // 先隐形，定位完成后再显现，避免闪一下
            Show();
        }

        RepositionAndReveal();
    }

    /// <summary>填充译文。</summary>
    public void SetResult(string translated)
    {
        ResultText.Text = translated;
        StatusText.Text = string.Empty;
        CopyButton.IsEnabled = true;
        ClampIntoWorkArea(); // 译文变长后确保不超出屏幕
    }

    /// <summary>显示错误（保留原文，便于重试）。</summary>
    public void SetError(string message)
    {
        ResultText.Text = message;
        StatusText.Text = "重新选中文字，再按一次快捷键即可重试";
        CopyButton.IsEnabled = false;
        ClampIntoWorkArea();
    }

    // ---------------- 定位 ----------------

    private void RepositionAndReveal()
    {
        UpdateLayout();

        NativeMethods.GetCursorPos(out var pt);
        _lastMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(_lastMonitor, ref mi);
        NativeMethods.GetDpiForMonitor(_lastMonitor, 0, out uint dpi, out _);
        double scale = dpi / 96.0;

        int w = (int)Math.Ceiling(ActualWidth * scale);
        int h = (int)Math.Ceiling(ActualHeight * scale);

        // 默认弹在鼠标右下方；贴到屏幕边就翻到另一侧（全部用物理像素计算）
        int x = pt.X + 2;
        int y = pt.Y + 6;
        if (x + w > mi.rcWork.Right)
        {
            x = pt.X - w - 2;
        }

        if (y + h > mi.rcWork.Bottom)
        {
            y = pt.Y - h - 6;
        }

        x = Math.Max(mi.rcWork.Left, x);
        y = Math.Max(mi.rcWork.Top, y);

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        Opacity = 1;
        EnsureHookInstalled();
    }

    /// <summary>内容变长后，如果底部超出屏幕就整体上移。</summary>
    private void ClampIntoWorkArea()
    {
        if (_hwnd == IntPtr.Zero || _lastMonitor == IntPtr.Zero)
        {
            return;
        }

        UpdateLayout();
        NativeMethods.GetWindowRect(_hwnd, out var rect);

        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(_lastMonitor, ref mi);

        if (rect.Bottom > mi.rcWork.Bottom)
        {
            int newY = Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - (rect.Bottom - rect.Top));
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, newY, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    // ---------------- 点卡片外关闭（全局鼠标钩子） ----------------

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            RemoveHook();
        }
    }

    private void EnsureHookInstalled()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hookProc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc,
            NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_LBUTTONDOWN
                || msg == NativeMethods.WM_RBUTTONDOWN
                || msg == NativeMethods.WM_MBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (NativeMethods.GetWindowRect(_hwnd, out var rect))
                {
                    bool inside = data.pt.X >= rect.Left && data.pt.X <= rect.Right
                                  && data.pt.Y >= rect.Top && data.pt.Y <= rect.Bottom;
                    if (!inside)
                    {
                        Dispatcher.BeginInvoke(new Action(() => Hide()));
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ---------------- 复制按钮 ----------------

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ResultText.Text);
        }
        catch
        {
            // 剪贴板被占用等，忽略
        }

        CopyButton.Content = "已复制 ✓";
        await Task.Delay(1200);
        CopyButton.Content = "复制";
    }
}
