using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AutoOCRTranslator.Translate.Providers;

/// <summary>
/// DeepL API 提供者（对应 Python 版 deepl），支持 Free（:fx）和 Pro API。
/// </summary>
public sealed class DeepLTranslator : ITranslator
{
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "JA", ["japan"] = "JA",
        ["zh"] = "ZH", ["zh-cn"] = "ZH", ["zh-tw"] = "ZH", ["ch"] = "ZH", ["ch_tra"] = "ZH",
        ["en"] = "EN", ["ko"] = "KO",
    };

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public DeepLTranslator(string apiKey = "", string apiSecret = "", int timeout = 10, string? proxy = null)
    {
        _apiKey = string.IsNullOrEmpty(apiKey) ? apiSecret : apiKey;
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new TranslationException("DeepL 翻译需要提供 API Key");
        }

        // 以 :fx 结尾的是免费版 API
        _baseUrl = _apiKey.EndsWith(":fx", StringComparison.Ordinal)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeout)) };
    }

    public string Translate(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var payload = new Dictionary<string, object>
        {
            ["text"] = new[] { text },
            ["target_lang"] = NormalizeLang(targetLang),
        };
        string source = NormalizeLang(sourceLang);
        if (!string.IsNullOrEmpty(source)) payload["source_lang"] = source;

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

        string json;
        try
        {
            json = _http.SendAsync(request).GetAwaiter().GetResult().EnsureSuccessStatusCode()
                .Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new TranslationException($"DeepL 网络请求失败: {e.Message}", e);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var translations = doc.RootElement.GetProperty("translations").EnumerateArray().ToList();
            if (translations.Count == 0)
            {
                throw new TranslationException("DeepL 返回空翻译结果");
            }
            return translations[0].GetProperty("text").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or TranslationException)
        {
            throw new TranslationException($"DeepL 响应格式异常: {e.Message}", e);
        }
    }

    /// <summary>标准化语言代码为 DeepL 格式。</summary>
    private static string NormalizeLang(string lang) =>
        LangMap.TryGetValue(lang, out string? mapped) ? mapped : lang.ToUpperInvariant();
}
