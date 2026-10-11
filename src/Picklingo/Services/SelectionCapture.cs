using System.Diagnostics;
using System.Threading;
using System.Windows;
using Picklingo.Interop;
using Clipboard = System.Windows.Clipboard;

namespace Picklingo.Services;

/// <summary>
/// 取词器：模拟 Ctrl+C 复制当前选中的文字，读取后立即把用户剪贴板还原；全程不抢焦点。
/// 没有选中文本（剪贴板无变化）时静默返回 null，调用方不弹窗。
/// 注意：本方法内含轮询（最长约 1.5 秒），应在程序的消息线程上调用（WPF 剪贴板 API 要求 STA）。
/// </summary>
internal static class SelectionCapture
{
    private const int ClipboardWaitMs = 800;
    private const int PollIntervalMs = 12;

    /// <summary>尝试获取当前选中文字；无选中返回 null。</summary>
    public static string? TryCapture()
    {
        var total = Stopwatch.StartNew();

        // 1) 先备份用户剪贴板（只碰文本格式，读操作、不改动它）
        var swStep = Stopwatch.StartNew();
        var backup = ClipboardBackup.Capture();
        long tBackup = swStep.ElapsedMilliseconds;
        uint seqBefore = NativeMethods.GetClipboardSequenceNumber();

        // 2) 关键：热键触发瞬间，用户的手指可能还压着 Alt / Ctrl / Shift（热键组合的一部分）。
        //    必须等它们物理松开，否则随后模拟的 Ctrl+C 会被污染成 Ctrl+Alt+C 之类的错误组合，复制失败。
        swStep.Restart();
        WaitForModifiersReleased();
        long tWaitKeys = swStep.ElapsedMilliseconds;

        // 3) 模拟 Ctrl+C
        swStep.Restart();
        NativeMethods.SendKey(NativeMethods.VK_CONTROL, keyUp: false);
        NativeMethods.SendKey(NativeMethods.VK_C, keyUp: false);
        NativeMethods.SendKey(NativeMethods.VK_C, keyUp: true);
        NativeMethods.SendKey(NativeMethods.VK_CONTROL, keyUp: true);

        // 4) 等剪贴板序列号变化（复制成功时系统必然自增它；没选中文字则不会变）
        string? text = null;
        bool seqChanged = false;
        var swWait = Stopwatch.StartNew();
        while (swWait.ElapsedMilliseconds < ClipboardWaitMs)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != seqBefore)
            {
                seqChanged = true;
                text = ReadClipboardText();
                break;
            }

            Thread.Sleep(PollIntervalMs);
        }

        long tCopyWait = swStep.ElapsedMilliseconds;

        // 5) 把用户剪贴板放回去。还原的最后一步会调用 OleFlushClipboard 立即移交数据所有权，
        //    避免“本进程成为剪贴板 owner、拖慢下一次复制”的问题（2026-10-10 排查结论）。
        backup.Restore();

        text = text?.Trim();
        string? result = string.IsNullOrEmpty(text) ? null : text;

        Logging.Log(
            $"取词诊断：备份[{backup.Status} {tBackup}ms] 等键[{tWaitKeys}ms] 复制等待[{tCopyWait}ms 序号变={seqChanged}] " +
            $"读到[{(result is null ? "无" : result.Length + "字符")}] 总[{total.ElapsedMilliseconds}ms]");

        return result;
    }

    /// <summary>
    /// 等待所有修饰键（Ctrl/Alt/Shift/Win，含左右两侧）物理松开，最多等 650ms。
    /// 超时（用户一直按着不放）则退回到“注入释放”的兜底策略。
    /// </summary>
    private static void WaitForModifiersReleased()
    {
        const int timeoutMs = 650;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!IsAnyModifierDown())
            {
                return;
            }

            Thread.Sleep(6);
        }

        // 兜底：还没松开，注入释放事件
        NativeMethods.SendKey(NativeMethods.VK_ALT, keyUp: true);
        NativeMethods.SendKey(NativeMethods.VK_SHIFT, keyUp: true);
        NativeMethods.SendKey(NativeMethods.VK_LWIN, keyUp: true);
        NativeMethods.SendKey(NativeMethods.VK_RWIN, keyUp: true);
        Thread.Sleep(16);
    }

    private static bool IsAnyModifierDown()
        => IsKeyDown(NativeMethods.VK_LCONTROL) || IsKeyDown(NativeMethods.VK_RCONTROL)
        || IsKeyDown(NativeMethods.VK_LMENU) || IsKeyDown(NativeMethods.VK_RMENU)
        || IsKeyDown(NativeMethods.VK_LSHIFT) || IsKeyDown(NativeMethods.VK_RSHIFT)
        || IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN);

    private static bool IsKeyDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static string? ReadClipboardText()
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch
            {
                Thread.Sleep(20); // 剪贴板被短暂占用，稍候重试
            }
        }

        return null;
    }
}
