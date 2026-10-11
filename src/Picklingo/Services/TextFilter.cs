namespace Picklingo.Services;

/// <summary>文本过滤：决定选中内容是否值得翻译、是否需要截断。</summary>
internal static class TextFilter
{
    /// <summary>翻译接口的单次上限（Bing 约 5000 字符）。</summary>
    public const int MaxLength = 5000;

    /// <summary>纯数字/符号/空白的选择直接忽略；至少含一个字母或汉字才翻译。</summary>
    public static bool ShouldTranslate(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
            {
                return true; // 英文、汉字、日文假名等 Unicode 字母都算
            }
        }

        return false;
    }

    /// <summary>超长文本截断到接口上限。返回（要翻译的文本, 是否被截断）。</summary>
    public static (string Text, bool Truncated) Clamp(string text)
    {
        if (text.Length <= MaxLength)
        {
            return (text, false);
        }

        return (text[..MaxLength], true);
    }
}
