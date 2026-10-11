using System.Threading;
using System.Windows;

namespace Picklingo.Services;

/// <summary>
/// 剪贴板备份（轻量版）：只备份"文本"内容，用完还原文本；其余情况不深度克隆、不还原。
/// 设计说明：早期实现会遍历并克隆剪贴板的全部格式，这会向数据属主进程逐一发起渲染请求，
/// 遇到不响应或被独占的属主时会阻塞数秒，导致连续取词偶发失败——因此改为只碰最基础的文本格式。
/// </summary>
internal sealed class ClipboardBackup
{
    private string? _text;
    private bool _hasText;

    /// <summary>诊断用：备份结果摘要。</summary>
    public string Status { get; private set; } = "读取失败";

    /// <summary>拍一张剪贴板"文本"快照（非文本内容不处理）。</summary>
    public static ClipboardBackup Capture()
    {
        var b = new ClipboardBackup();
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    string? text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    b._text = text;
                    b._hasText = text is not null;
                    b.Status = text is null ? "无文本" : $"文本{text.Length}字符";
                }
                else
                {
                    b.Status = "非文本(不备份)";
                }

                return b;
            }
            catch
            {
                Thread.Sleep(30); // 剪贴板被别的进程短暂占用，稍候重试
            }
        }

        b.Status = "读取失败(不还原)";
        return b;
    }

    /// <summary>把文本快照还原回剪贴板（走原生 Win32 路径；无备份或原本非文本时不动剪贴板）。</summary>
    public void Restore()
    {
        if (!_hasText || _text is null)
        {
            return;
        }

        // 设计说明（2026-10-11）：不用 WPF/COM 剪贴板，改用原生 Win32 API 写入——
        // 手动复制（其他程序的写路径）完全正常，而 WPF 写路径会让“下一次其他程序复制”被拖慢约 5 秒；
        // 原生路径数据立即物化、不产生任何 OLE 对象/延迟渲染关系。
        for (int i = 0; i < 4; i++)
        {
            if (Picklingo.Interop.NativeMethods.TrySetClipboardText(_text))
            {
                Logging.Log("剪贴板还原完成（原生 Win32 路径）");
                return;
            }

            Thread.Sleep(30);
        }

        Logging.Log("剪贴板还原失败（原生路径重试耗尽）");
    }
}
