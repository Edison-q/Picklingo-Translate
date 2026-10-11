using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Picklingo.Services;

/// <summary>托盘图标与菜单：暂停/继续取词、退出。</summary>
internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;

    public bool Paused { get; private set; }

    public event Action? ExitRequested;

    public TrayService()
    {
        _pauseItem = new ToolStripMenuItem("暂停取词");
        _pauseItem.Click += (_, _) =>
        {
            Paused = !Paused;
            _pauseItem.Text = Paused ? "继续取词" : "暂停取词";
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("退出 Picklingo");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "Picklingo — 选中文字按 Alt+Q 翻译",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    /// <summary>托盘气泡提示（注册热键失败等场景用）。</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(6000);
        }
        catch
        {
            // 气泡失败不影响主流程
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>
/// 代码生成托盘图标（当前方案：蓝色圆底 + 白色 "PL"）。
/// 以后用户设计好正式图标后替换为 .ico 资源。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(0x2F, 0x6F, 0xED));
            g.FillEllipse(brush, 1, 1, 29, 29);

            using var font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("PL", font, Brushes.White, new RectangleF(1, 1, 29, 29), sf);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
