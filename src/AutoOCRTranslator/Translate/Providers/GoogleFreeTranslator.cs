using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AutoOCRTranslator.Translate.Providers;

/// <summary>
/// Google Translate 免费网页接口提供者（对应 Python 版 google_free）。
/// 非官方接口，仅作为默认免费方案；不稳定时可切换其他提供者。
/// </summary>
public sealed class GoogleFreeTranslator : ITranslator
{
    // 多个备用 endpoint，按顺序尝试
    private static readonly string[] Endpoints =
    [
        "https://translate.googleapis.com/translate_a/single",
        "https://translate.google.com/translate_a/single",
    ];

    private readonly HttpClient _http;
    private readonly int _maxRetries;

    public GoogleFreeTranslator(int timeout = 5, int maxRetries = 1, string? proxy = null)
    {
        _maxRetries = maxRetries;
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

        string query = $"?client=gtx&sl={Uri.EscapeDataString(sourceLang)}&tl={Uri.EscapeDataString(targetLang)}&dt=t&q={Uri.EscapeDataString(text)}";

        Exception? lastError = null;
        foreach (string url in Endpoints)
        {
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    string json = _http.GetStringAsync(url + query).GetAwaiter().GetResult();
                    return ParseResponse(json);
                }
                catch (TranslationException)
                {
                    throw; // 响应格式异常，不再重试（与 Python 版一致）
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    lastError = e;
                    if (attempt < _maxRetries) Thread.Sleep(500 * (attempt + 1));
                }
            }
        }

        throw new TranslationException(
            $"Google 翻译请求失败（已尝试 {Endpoints.Length} 个接口，共 {Endpoints.Length * (_maxRetries + 1)} 次）: {lastError?.Message}. " +
            "建议：1) 配置 HTTP 代理；2) 切换其他翻译服务。");
    }

    /// <summary>解析 Google Translate 返回的 JSON（data[0][*][0] 为译文片段）。</summary>
    private static string ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parts = new StringBuilder();
            foreach (JsonElement sentence in doc.RootElement[0].EnumerateArray())
            {
                if (sentence.GetArrayLength() > 0 && sentence[0].ValueKind == JsonValueKind.String)
                {
                    parts.Append(sentence[0].GetString());
                }
            }
            return parts.ToString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new TranslationException($"翻译响应格式异常: {e.Message}", e);
        }
    }
}
