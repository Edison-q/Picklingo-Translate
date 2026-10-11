using System.IO;

namespace Picklingo.Services;

/// <summary>统一日志：写 exe 同目录 picklingo.log，带毫秒时间戳；任何失败都不影响主流程。</summary>
internal static class Logging
{
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "picklingo.log");
            lock (Gate)
            {
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }
}
