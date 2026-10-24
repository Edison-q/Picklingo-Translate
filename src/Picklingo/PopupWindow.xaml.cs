using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Picklingo.Interop;
using Picklingo.Services;
using Clipboard = System.Windows.Clipboard;

namespace Picklingo;

/// <summary>
/// 鼠标旁的翻译卡片：不抢焦点、置顶、无边框；点卡片外任意处自动消失。
/// </summary>
public partial class PopupWindow : Window
{
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _lastMonitor = IntPtr.Zero;

    // 与 PopupWindow.xaml 里的默认值保持一致
    private const double DefaultMaxWidth = 400;
    private const double DefaultResultMaxHeight = 340;

    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private ScrollViewer? _smoothViewer;
    private double _smoothTarget;

    private IntPtr _hook = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _hookProc;

    public PopupWindow()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        _scrollTimer.Tick += OnScrollTick;
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
        ResultScroll.ScrollToTop();
        SourceScroll.ScrollToTop();

        if (!IsVisible)
        {
            ResetSizeMode(); // 每次新弹出：恢复“按内容自动大小”（手动调整只在当次有效）
            Opacity = 0; // 先隐形，定位完成后再显现，避免闪一下
            Show();
        }

        RepositionAndReveal();
    }

    /// <summary>恢复自动尺寸模式。</summary>
    private void ResetSizeMode()
    {
        _resizing = false;
        _dragging = false;
        _hoverZone = DragZone.None;
        RevertToAutoSize();
    }

    /// <summary>把窗口交还给“按内容自动大小”。</summary>
    private void RevertToAutoSize()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = double.NaN;
        Height = double.NaN;
        ContentGrid.MaxWidth = DefaultMaxWidth;
        ResultScroll.MaxHeight = DefaultResultMaxHeight;
    }

    /// <summary>把窗口实际尺寸同步回 WPF 的 Width/Height（防止后续布局把窗口拉回旧尺寸）。</summary>
    private void SyncSizeProps()
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var r))
        {
            return;
        }

        double scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96.0);
        Width = (r.Right - r.Left) / scale;
        Height = (r.Bottom - r.Top) / scale;
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
        _hoverZone = DragZone.None;
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
            _dragging = false;
            _resizing = false;
            _hoverZone = DragZone.None;
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

    // ---------------- 鼠标按住拖动 / 边框调整大小 ----------------

    private bool _dragging;
    private int _dragMouseX;
    private int _dragMouseY;
    private int _dragWinX;
    private int _dragWinY;
    private int _dragWinW;
    private int _dragWinH;
    private NativeMethods.RECT _dragWork;

    private enum DragZone { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private DragZone _hoverZone = DragZone.None;
    private bool _resizing;
    private DragZone _resizeZone = DragZone.None;
    private int _resizeMouseX;
    private int _resizeMouseY;
    private int _resizeStartLeft;
    private int _resizeStartTop;
    private int _resizeStartW;
    private int _resizeStartH;
    private int _resizeMinW;
    private int _resizeMinH;
    private NativeMethods.RECT _resizeWork;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                if (_resizing)
                {
                    DoResize(data.pt);
                }
                else if (_dragging)
                {
                    DoDragMove(data.pt);
                }
                else
                {
                    UpdateHoverCursor(data.pt);
                }
            }
            else if (msg == NativeMethods.WM_LBUTTONUP)
            {
                if (_dragging || _resizing)
                {
                    bool wasResizing = _resizing;
                    DragZone resizeZone = _resizeZone;
                    _dragging = false;
                    _resizing = false;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetDragVisual(false, DragZone.Move);
                        if (wasResizing)
                        {
                            // 把最终尺寸同步回 WPF，否则后续布局会把窗口拉回旧尺寸
                            SyncSizeProps();
                            if (NativeMethods.GetWindowRect(_hwnd, out var r))
                            {
                                Log($"拉伸结束 {resizeZone}：窗口 {r.Right - r.Left}x{r.Bottom - r.Top}");
                            }
                        }

                        _hoverZone = DragZone.None; // 下一次移动重新计算光标（拖动/拉伸结束后都要重置）
                    }));
                }
            }
            else if (msg == NativeMethods.WM_LBUTTONDOWN
                     || msg == NativeMethods.WM_RBUTTONDOWN
                     || msg == NativeMethods.WM_MBUTTONDOWN)
            {
                if (NativeMethods.GetWindowRect(_hwnd, out var winRect)
                    && TryGetCardScreenRect(winRect, out var cardRect))
                {
                    // 唯一几何来源：悬停与点击都用“可见卡片”的矩形。
                    // （窗口外圈 14 DIP 是给阴影的透明留白，绝不能拿 GetWindowRect 当卡片用。）
                    var zone = GetZone(data.pt, cardRect);

                    if (zone == DragZone.None)
                    {
                        Log($"点击卡片外 → 关闭（鼠标 {data.pt.X},{data.pt.Y}；卡片 {RectText(cardRect)}）");
                        Dispatcher.BeginInvoke(new Action(() => Hide()));
                    }
                    else if (msg == NativeMethods.WM_LBUTTONDOWN)
                    {
                        Log($"按下 {zone}（鼠标 {data.pt.X},{data.pt.Y}；窗口 {RectText(winRect)}；卡片 {RectText(cardRect)}；热区 {GetGrabPx()}px）");
                        if (zone == DragZone.Move)
                        {
                            // 复制按钮、滚动条都不参与“拖动整张卡片”
                            if (!IsPointInCopyButton(data.pt.X, data.pt.Y)
                                && !IsPointOnScrollBar(data.pt.X, data.pt.Y))
                            {
                                StartDrag(data.pt, winRect);
                            }
                        }
                        else
                        {
                            StartResize(zone, data.pt, winRect);
                        }
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>取“可见卡片”的屏幕物理矩形：窗口矩形向内收 14 DIP（外层给阴影留的透明边距）。</summary>
    private bool TryGetCardScreenRect(NativeMethods.RECT winRect, out NativeMethods.RECT card)
    {
        uint dpi = _hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(_hwnd) : 96;
        double scale = dpi == 0 ? 1.0 : Math.Max(1.0, dpi / 96.0);
        int m = (int)Math.Round(14 * scale); // 与 XAML 外层 Grid 的 Margin=14 保持一致
        card = new NativeMethods.RECT
        {
            Left = winRect.Left + m,
            Top = winRect.Top + m,
            Right = winRect.Right - m,
            Bottom = winRect.Bottom - m,
        };
        return card.Right > card.Left && card.Bottom > card.Top;
    }

    private static string RectText(NativeMethods.RECT r) => $"{r.Left},{r.Top}-{r.Right},{r.Bottom}";

    /// <summary>
    /// 拉伸热区宽度（物理像素）：用系统同一套度量 + 本窗口 DPI。
    /// 旧实现用 GetSystemMetrics（不跟随 per-monitor DPI），200% 下只有 8px；
    /// 且悬停判定误用了窗口矩形，热区整体落在卡片外 12~28px 的透明阴影里——这就是“热区偏远、一按就没”的根因。
    /// </summary>
    private int GetGrabPx()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        int grab = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSIZEFRAME, dpi)
                   + NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXPADDEDBORDER, dpi);
        if (grab <= 0)
        {
            grab = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSIZEFRAME)
                   + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXPADDEDBORDER);
        }

        double scale = Math.Max(1.0, dpi / 96.0);
        int min = (int)Math.Round(5 * scale);
        int max = (int)Math.Round(10 * scale);
        return Math.Clamp(grab, min, Math.Max(min, max));
    }

    /// <summary>
    /// 判断鼠标落在卡片的哪一块：边缘 = 拉伸区（允许往卡片外多算半圈，容错更像系统窗口），
    /// 内部 = 拖动区，卡片+外圈之外 = None（点这里应关闭卡片）。
    /// </summary>
    private DragZone GetZone(NativeMethods.POINT pt, NativeMethods.RECT card)
    {
        int grab = GetGrabPx();
        int outer = grab / 2; // 卡片外仍算拉伸区的一圈，避免“瞄边缘差 1px 就把卡片关掉”

        if (pt.X < card.Left - outer || pt.X > card.Right + outer
            || pt.Y < card.Top - outer || pt.Y > card.Bottom + outer)
        {
            return DragZone.None;
        }

        // 小卡片保护：热区最多占短边 1/3，否则边角热区会把整张卡吞掉
        int shortSide = Math.Min(card.Right - card.Left, card.Bottom - card.Top);
        grab = Math.Clamp(grab, 2, Math.Max(2, shortSide / 3));

        bool left = pt.X <= card.Left + grab;
        bool right = pt.X >= card.Right - grab;
        bool top = pt.Y <= card.Top + grab;
        bool bottom = pt.Y >= card.Bottom - grab;

        if (left && top) return DragZone.TopLeft;
        if (right && top) return DragZone.TopRight;
        if (left && bottom) return DragZone.BottomLeft;
        if (right && bottom) return DragZone.BottomRight;
        if (left) return DragZone.Left;
        if (right) return DragZone.Right;
        if (top) return DragZone.Top;
        if (bottom) return DragZone.Bottom;
        return DragZone.Move;
    }

    private void UpdateHoverCursor(NativeMethods.POINT pt)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var winRect)
            || !TryGetCardScreenRect(winRect, out var card))
        {
            return;
        }

        var zone = GetZone(pt, card);
        if (zone == _hoverZone)
        {
            return;
        }

        _hoverZone = zone;
        Log($"悬停 {zone}（鼠标 {pt.X},{pt.Y}；卡片 {RectText(card)}；热区 {GetGrabPx()}px）");
        Dispatcher.BeginInvoke(new Action(() => ApplyZoneCursor(zone)));
    }

    private void ApplyZoneCursor(DragZone zone)
    {
        Cursor = CursorFor(zone);

        // 立即生效（WPF 的 Cursor 属性要等下一次鼠标移动才应用）
        NativeMethods.SetSystemCursor(IdcFor(zone));
    }

    private static Cursor CursorFor(DragZone zone) => zone switch
    {
        DragZone.Left or DragZone.Right => Cursors.SizeWE,
        DragZone.Top or DragZone.Bottom => Cursors.SizeNS,
        DragZone.TopLeft or DragZone.BottomRight => Cursors.SizeNWSE,
        DragZone.TopRight or DragZone.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private static int IdcFor(DragZone zone) => zone switch
    {
        DragZone.Left or DragZone.Right => NativeMethods.IDC_SIZEWE,
        DragZone.Top or DragZone.Bottom => NativeMethods.IDC_SIZENS,
        DragZone.TopLeft or DragZone.BottomRight => NativeMethods.IDC_SIZENWSE,
        DragZone.TopRight or DragZone.BottomLeft => NativeMethods.IDC_SIZENESW,
        _ => NativeMethods.IDC_ARROW,
    };

    private void StartDrag(NativeMethods.POINT pt, NativeMethods.RECT rect)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (!NativeMethods.GetMonitorInfo(mon, ref mi))
        {
            return;
        }

        _dragWork = mi.rcWork;
        _dragging = true;
        _dragMouseX = pt.X;
        _dragMouseY = pt.Y;
        _dragWinX = rect.Left;
        _dragWinY = rect.Top;
        _dragWinW = rect.Right - rect.Left;
        _dragWinH = rect.Bottom - rect.Top;
        Dispatcher.BeginInvoke(new Action(() => SetDragVisual(true, DragZone.Move)));
    }

    private void DoDragMove(NativeMethods.POINT pt)
    {
        int newX = _dragWinX + (pt.X - _dragMouseX);
        int newY = _dragWinY + (pt.Y - _dragMouseY);

        newX = Math.Clamp(newX, _dragWork.Left, Math.Max(_dragWork.Left, _dragWork.Right - _dragWinW));
        newY = Math.Clamp(newY, _dragWork.Top, Math.Max(_dragWork.Top, _dragWork.Bottom - _dragWinH));

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, newX, newY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void StartResize(DragZone zone, NativeMethods.POINT pt, NativeMethods.RECT rect)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (!NativeMethods.GetMonitorInfo(mon, ref mi))
        {
            return;
        }

        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi == 0 ? 1.0 : Math.Max(1.0, dpi / 96.0);
        int beforeW = rect.Right - rect.Left;
        int beforeH = rect.Bottom - rect.Top;

        // 先把尺寸钉死再切“固定尺寸”模式：否则切模式的一瞬间 WPF 可能用未定义的宽高，窗口塌成 0（卡片消失）
        double pinnedW = ActualWidth > 1 ? ActualWidth : beforeW / scale;
        double pinnedH = ActualHeight > 1 ? ActualHeight : beforeH / scale;

        SizeToContent = SizeToContent.Manual;
        Width = pinnedW;
        Height = pinnedH;
        ContentGrid.MaxWidth = double.PositiveInfinity;
        ResultScroll.MaxHeight = double.PositiveInfinity;
        UpdateLayout();

        // 兜底：万一切换后窗口异常变小，立刻退回自动尺寸——宁可这次拉不动，也不能让卡片消失
        if (!NativeMethods.GetWindowRect(_hwnd, out var after)
            || after.Right - after.Left < beforeW / 2
            || after.Bottom - after.Top < beforeH / 2)
        {
            RevertToAutoSize();
            UpdateLayout();
            Log($"拉伸放弃：切换固定尺寸后窗口异常（切换前 {beforeW}x{beforeH}，切换后 {after.Right - after.Left}x{after.Bottom - after.Top}）");
            return;
        }

        _resizeMinW = (int)Math.Round(248 * scale);
        _resizeMinH = (int)Math.Round(130 * scale);

        _resizing = true;
        _resizeZone = zone;
        _resizeMouseX = pt.X;
        _resizeMouseY = pt.Y;
        _resizeStartLeft = rect.Left;
        _resizeStartTop = rect.Top;
        _resizeStartW = beforeW;
        _resizeStartH = beforeH;
        _resizeWork = mi.rcWork;
        Log($"拉伸开始 {zone}：窗口 {beforeW}x{beforeH}px，固定模式 {pinnedW:F0}x{pinnedH:F0} DIP，最小 {_resizeMinW}x{_resizeMinH}px");
        Dispatcher.BeginInvoke(new Action(() => SetDragVisual(true, zone)));
    }

    private void DoResize(NativeMethods.POINT pt)
    {
        int dx = pt.X - _resizeMouseX;
        int dy = pt.Y - _resizeMouseY;

        int left = _resizeStartLeft;
        int top = _resizeStartTop;
        int w = _resizeStartW;
        int h = _resizeStartH;
        int startRight = _resizeStartLeft + _resizeStartW;
        int startBottom = _resizeStartTop + _resizeStartH;
        var z = _resizeZone;

        // 拖左边/上边时固定对边：钳制的是“移动的那条边”，宽高由两条边算出来
        // （旧实现只钳 left 不同步 w，贴到屏幕边缘时会跳一下）
        if (z is DragZone.Left or DragZone.TopLeft or DragZone.BottomLeft)
        {
            int maxLeft = Math.Max(_resizeWork.Left, startRight - _resizeMinW);
            left = Math.Clamp(_resizeStartLeft + dx, _resizeWork.Left, maxLeft);
            w = startRight - left;
        }
        else if (z is DragZone.Right or DragZone.TopRight or DragZone.BottomRight)
        {
            int maxW = Math.Max(_resizeMinW, _resizeWork.Right - _resizeStartLeft);
            w = Math.Clamp(_resizeStartW + dx, _resizeMinW, maxW);
        }

        if (z is DragZone.Top or DragZone.TopLeft or DragZone.TopRight)
        {
            int maxTop = Math.Max(_resizeWork.Top, startBottom - _resizeMinH);
            top = Math.Clamp(_resizeStartTop + dy, _resizeWork.Top, maxTop);
            h = startBottom - top;
        }
        else if (z is DragZone.Bottom or DragZone.BottomLeft or DragZone.BottomRight)
        {
            int maxH = Math.Max(_resizeMinH, _resizeWork.Bottom - _resizeStartTop);
            h = Math.Clamp(_resizeStartH + dy, _resizeMinH, maxH);
        }

        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, left, top, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>“复制”按钮区域不参与拖动（按钮可用时才排除；禁用状态整卡可拖）。</summary>
    private bool IsPointInCopyButton(int x, int y)
    {
        if (!CopyButton.IsEnabled)
        {
            return false;
        }

        try
        {
            var tl = CopyButton.PointToScreen(new Point(0, 0));
            var br = CopyButton.PointToScreen(new Point(CopyButton.ActualWidth, CopyButton.ActualHeight));
            return x >= tl.X && x <= br.X && y >= tl.Y && y <= br.Y;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>滚动条区域不参与卡片拖动（否则拖滑块会把整张卡拖走）。</summary>
    private bool IsPointOnScrollBar(int x, int y)
        => IsPointInScrollBar(ResultScroll, x, y) || IsPointInScrollBar(SourceScroll, x, y);

    private static bool IsPointInScrollBar(ScrollViewer sv, int x, int y)
    {
        if (sv.ComputedVerticalScrollBarVisibility != Visibility.Visible)
        {
            return false;
        }

        if (sv.Template?.FindName("PART_VerticalScrollBar", sv) is not FrameworkElement bar || !bar.IsVisible)
        {
            return false;
        }

        try
        {
            var tl = bar.PointToScreen(new Point(0, 0));
            var br = bar.PointToScreen(new Point(bar.ActualWidth, bar.ActualHeight));
            return x >= tl.X && x <= br.X && y >= tl.Y && y <= br.Y;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>拖动/拉伸时的视觉反馈：移动或拉伸光标 + 更强的投影（“拿起来了”的感觉）。</summary>
    private void SetDragVisual(bool active, DragZone zone)
    {
        Cursor = active ? CursorFor(zone) : Cursors.Arrow;
        NativeMethods.SetSystemCursor(active ? IdcFor(zone) : NativeMethods.IDC_ARROW);
        if (CardBorder.Effect is DropShadowEffect shadow)
        {
            shadow.BlurRadius = active ? 26 : 16;
            shadow.ShadowDepth = active ? 5 : 2;
            shadow.Opacity = active ? 0.34 : 0.22;
        }
    }

    private static void Log(string message) => Logging.Log(message);

    // ---------------- 滚动 ----------------

    /// <summary>
    /// 滚轮：不用系统默认的“一格一跳”，改成约 150ms 的缓动（像触控板/浏览器），
    /// 快滚时目标位置会累积，不会丢格。
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;

        double from = _scrollTimer.IsEnabled && ReferenceEquals(_smoothViewer, sv)
            ? _smoothTarget
            : sv.VerticalOffset;

        _smoothViewer = sv;
        _smoothTarget = Math.Clamp(from - e.Delta / 120.0 * 56, 0, sv.ScrollableHeight);

        if (!_scrollTimer.IsEnabled)
        {
            _scrollTimer.Start();
        }
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        if (_smoothViewer is null)
        {
            _scrollTimer.Stop();
            return;
        }

        double diff = _smoothTarget - _smoothViewer.VerticalOffset;
        if (Math.Abs(diff) < 0.8)
        {
            _smoothViewer.ScrollToVerticalOffset(_smoothTarget);
            _scrollTimer.Stop();
            return;
        }

        _smoothViewer.ScrollToVerticalOffset(_smoothViewer.VerticalOffset + diff * 0.32);
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
