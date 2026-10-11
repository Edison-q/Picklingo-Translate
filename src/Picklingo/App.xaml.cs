using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Picklingo.Interop;
using Picklingo.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Picklingo;

/// <summary>
/// 程序入口：单实例 → 托盘 → 全局热键 → 触发“取词 → 翻译 → 弹窗”链路。
/// </summary>
public partial class App : Application
{
    private const int VkQ = 0x51;

    private Mutex? _mutex;
    private HwndSource? _messageSink;
    private HotKeyService? _hotKey;
    private TrayService? _tray;
    private PopupWindow? _popup;
    private bool _busy;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 单实例 ----
        _mutex = new Mutex(initiallyOwned: true, name: @"Local\Picklingo.SingleInstance", createdNew: out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Picklingo 已经在运行了（右下角托盘里找它）。", "Picklingo",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // ---- 隐藏的消息窗口：接收全局热键消息 ----
        var sinkParams = new HwndSourceParameters("PicklingoMessageSink")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1,
        };
        _messageSink = new HwndSource(sinkParams);
        _messageSink.AddHook(WndProc);

        // ---- 托盘 ----
        _tray = new TrayService();
        _tray.ExitRequested += Shutdown;

        // ---- 弹窗（预创建，平时隐藏） ----
        _popup = new PopupWindow();

        // ---- 全局热键（默认 Alt+Q；可在 exe 同目录 picklingo.ini 里修改） ----
        var (mods, vk, spec) = HotKeyConfig.Load();
        _hotKey = new HotKeyService(_messageSink.Handle);
        _hotKey.Pressed += OnHotKeyPressed;
        bool ok = _hotKey.Register(mods, vk);
        if (ok)
        {
            Log($"已启动，热键 {spec} 注册成功。");
        }
        else
        {
            Log($"热键 {spec} 注册失败（可能被其他软件占用）。");
            _tray.ShowBalloon($"Picklingo：热键 {spec} 注册失败",
                $"快捷键 {spec} 可能被其他软件占用了。可以打开 Picklingo 同目录的 picklingo.ini，改 hotkey= 换一个再重启。");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _hotKey?.HandleWindowMessage(msg, wParam);
        return IntPtr.Zero;
    }

    private async void OnHotKeyPressed()
    {
        if (_busy || _tray?.Paused == true)
        {
            return;
        }

        _busy = true;
        try
        {
            Log("热键触发，取词中…");
            string? text = SelectionCapture.TryCapture();
            if (text is null)
            {
                Log("没取到选中文本（未选中或复制失败），忽略。");
                return;
            }

            if (!TextFilter.ShouldTranslate(text))
            {
                Log($"纯数字/符号，跳过（{text.Length} 字符）。");
                return;
            }

            var (clamped, truncated) = TextFilter.Clamp(text);
            Log($"取词成功：{clamped.Length} 字符{(truncated ? "（已截断）" : "")}：{Preview(clamped)}");

            _popup!.ShowAtCursor(clamped, truncated);

            try
            {
                string result = await Translator.TranslateAsync(clamped);
                Log($"翻译完成：{Preview(result)}");
                _popup.SetResult(result);
            }
            catch (Exception ex)
            {
                Log("翻译失败：" + ex);
                _popup.SetError("翻译失败：" + FriendlyMessage(ex));
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        HttpRequestException => "网络请求失败，检查一下网络",
        TaskCanceledException => "请求超时",
        _ => ex.Message,
    };

    private static string Preview(string s) => s.Length <= 60 ? s : s[..60] + "…";

    protected override void OnExit(ExitEventArgs e)
    {
        _hotKey?.Dispose();
        _tray?.Dispose();
        _popup?.Close();
        _messageSink?.Dispose();
        Log("已退出。");
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>简易日志（转发到统一 Logging），只用于排障，不记录完整翻译内容。</summary>
    private static void Log(string message) => Logging.Log(message);
}
