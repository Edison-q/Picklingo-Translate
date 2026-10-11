using System.IO;
using Picklingo.Interop;

namespace Picklingo.Services;

/// <summary>
/// 快捷键配置：从 exe 同目录的 picklingo.ini 读取 hotkey=（如 Alt+Q / Ctrl+Q / Ctrl+Shift+Q）。
/// 文件不存在时自动生成一个带注释的默认配置；解析失败回退 Alt+Q。
/// 修改后需要重启 Picklingo 生效。
/// </summary>
internal static class HotKeyConfig
{
    public const string DefaultSpec = "Alt+Q";

    public static (uint Modifiers, uint Vk, string Spec) Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "picklingo.ini");
        EnsureFile(path);

        string spec = DefaultSpec;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                if (line[..eq].Trim().Equals("hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    spec = line[(eq + 1)..].Trim();
                    break;
                }
            }
        }
        catch
        {
            // 读取失败用默认
        }

        return Parse(spec);
    }

    private static (uint Modifiers, uint Vk, string Spec) Parse(string spec)
    {
        uint mods = 0;
        uint vk = 0;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mods |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    mods |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    mods |= NativeMethods.MOD_SHIFT;
                    break;
                case "win":
                    mods |= NativeMethods.MOD_WIN;
                    break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        vk = char.ToUpperInvariant(part[0]);
                    }

                    break;
            }
        }

        if (vk == 0 || mods == 0)
        {
            // 解析失败：回退默认（Alt+Q）
            return (NativeMethods.MOD_ALT, 'Q', DefaultSpec);
        }

        return (mods, vk, spec);
    }

    private static void EnsureFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(path,
                "# Picklingo 配置文件\r\n" +
                "# hotkey = 全局快捷键：修饰键(Ctrl/Alt/Shift/Win) + 一个字母或数字，用 + 连接\r\n" +
                "# 示例：Alt+Q / Ctrl+Q / Ctrl+Shift+Q\r\n" +
                "# 修改后重启 Picklingo 生效\r\n" +
                "hotkey=" + DefaultSpec + "\r\n");
        }
        catch
        {
            // 目录只读等场景，忽略
        }
    }
}
