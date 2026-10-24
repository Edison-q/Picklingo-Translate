using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Picklingo.Services;

/// <summary>
/// 翻译核心：优先 Bing 免费接口（质量好、无需注册），失败自动降级 MyMemory（零注册兜底）。
/// 会话参数（key/token/IG + cookie）随页面自动获取，过期自动刷新。
/// </summary>
internal static class Translator
{
    private const string BingHome = "https://cn.bing.com/translator";
    private const string BingApi = "https://cn.bing.com/ttranslatev3?isVertical=1&IID=translator.5024";
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly Regex ParamsRegex =
        new(@"params_AbusePreventionHelper\s*=\s*\[(\d+),""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex IgRegex =
        new(@"IG:""([0-9A-Fa-f]{32})""", RegexOptions.Compiled);

    private static string? _key;
    private static string? _token;
    private static string? _ig;
    private static DateTime _sessionUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim SessionLock = new(1, 1);

    /// <summary>翻译文本；方向自动（含中文→译英，否则→译中）。</summary>
    public static async Task<string> TranslateAsync(string text, CancellationToken ct = default)
    {
        var (from, to) = DetectDirection(text);

        try
        {
            return await BingTranslateAsync(text, from, to, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bing 不可用时静默降级到 MyMemory 兜底
            return await MyMemoryTranslateAsync(text, from, to, ct);
        }
    }

    /// <summary>
    /// 语言方向：按“主体语言”判断——汉字数与英文单词数比较，中文为主译英、英文为主译中。
    /// 修复：旧规则“只要含一个汉字就译英”，导致一大段英文里夹个中文词时被误判成中文段落。
    /// </summary>
    public static (string From, string To) DetectDirection(string text)
    {
        int hanzi = 0;
        int latinWords = 0;
        bool inWord = false;
        foreach (var ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF')
            {
                hanzi++;
                inWord = false;
            }
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
            {
                if (!inWord)
                {
                    latinWords++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }

        bool chineseDominant = hanzi > 0 && hanzi >= latinWords;
        return chineseDominant ? ("zh-Hans", "en") : ("en", "zh-Hans");
    }

    // ---------------- Bing ----------------

    private static async Task<string> BingTranslateAsync(string text, string from, string to, CancellationToken ct)
    {
        await EnsureSessionAsync(forceRefresh: false, ct);
        try
        {
            return await DoBingRequestAsync(text, from, to, ct);
        }
        catch
        {
            // 会话可能已过期/参数变化：强制刷新一次再试
            await EnsureSessionAsync(forceRefresh: true, ct);
            return await DoBingRequestAsync(text, from, to, ct);
        }
    }

    private static async Task EnsureSessionAsync(bool forceRefresh, CancellationToken ct)
    {
        await SessionLock.WaitAsync(ct);
        try
        {
            bool fresh = DateTime.UtcNow - _sessionUtc < TimeSpan.FromMinutes(50);
            if (!forceRefresh && fresh && _key is not null && _token is not null && _ig is not null)
            {
                return;
            }

            string html = await Http.GetStringAsync(BingHome, ct);
            var m = ParamsRegex.Match(html);
            var ig = IgRegex.Match(html);
            if (!m.Success || !ig.Success)
            {
                throw new InvalidOperationException("Bing 页面参数提取失败（页面结构可能已变化）");
            }

            _key = m.Groups[1].Value;
            _token = m.Groups[2].Value;
            _ig = ig.Groups[1].Value;
            _sessionUtc = DateTime.UtcNow;
        }
        finally
        {
            SessionLock.Release();
        }
    }

    private static async Task<string> DoBingRequestAsync(string text, string from, string to, CancellationToken ct)
    {
        string url = $"{BingApi}&IG={_ig}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = from,
            ["text"] = text,
            ["to"] = to,
            ["token"] = _token!,
            ["key"] = _key!,
        });

        using var resp = await Http.PostAsync(url, content, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Bing 返回 HTTP {(int)resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        string? result = doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException("Bing 返回了空译文");
        }

        return result;
    }

    // ---------------- MyMemory（兜底） ----------------

    private static async Task<string> MyMemoryTranslateAsync(string text, string from, string to, CancellationToken ct)
    {
        string pair = $"{MapLang(from)}|{MapLang(to)}";
        string url =
            $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(pair)}";
        string body = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        string? result = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException("MyMemory 返回了空译文");
        }

        return result;
    }

    /// <summary>MyMemory 使用的语言代码与 Bing 略有差异，做最小映射。</summary>
    private static string MapLang(string lang) => lang switch
    {
        "zh-Hans" => "zh-CN",
        _ => lang,
    };

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        client.DefaultRequestHeaders.Referrer = new Uri(BingHome);
        client.DefaultRequestHeaders.Add("Origin", "https://cn.bing.com");
        return client;
    }
}
