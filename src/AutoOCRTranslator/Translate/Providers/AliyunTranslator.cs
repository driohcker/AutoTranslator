using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoOCRTranslator.Translate.Providers;

/// <summary>
/// 阿里云机器翻译提供者（对应 Python 版 aliyun），HMAC-SHA1 签名。
/// 需要 AccessKey ID（api_key）和 AccessKey Secret（api_secret）。
/// </summary>
public sealed class AliyunTranslator : ITranslator
{
    private const string Endpoint = "https://mt.aliyuncs.com/";

    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "ja", ["japan"] = "ja",
        ["zh"] = "zh", ["zh-cn"] = "zh", ["zh-tw"] = "zh-tw", ["ch"] = "zh", ["ch_tra"] = "zh-tw",
        ["en"] = "en", ["ko"] = "ko",
    };

    private readonly string _accessKeyId;
    private readonly string _accessKeySecret;
    private readonly HttpClient _http;

    public AliyunTranslator(string apiKey = "", string apiSecret = "", int timeout = 10, string? proxy = null)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            throw new TranslationException("阿里云翻译需要提供 AccessKey ID 和 AccessKey Secret");
        }
        _accessKeyId = apiKey;
        _accessKeySecret = apiSecret;

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

        var parameters = new Dictionary<string, string>
        {
            ["Format"] = "JSON",
            ["Version"] = "2018-10-12",
            ["AccessKeyId"] = _accessKeyId,
            ["SignatureMethod"] = "HMAC-SHA1",
            ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["SignatureVersion"] = "1.0",
            ["SignatureNonce"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["Action"] = "TranslateGeneral",
            ["FormatType"] = "text",
            ["SourceLanguage"] = NormalizeLang(sourceLang),
            ["TargetLanguage"] = NormalizeLang(targetLang),
            ["SourceText"] = text,
            ["Scene"] = "general",
        };
        parameters["Signature"] = Sign(parameters);

        // 手工拼接查询串（values 已按阿里云规则编码，签名值也已编码，无需二次编码）
        string query = string.Join("&", parameters.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        string json;
        try
        {
            json = _http.GetStringAsync($"{Endpoint}?{query}").GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new TranslationException($"阿里云翻译网络请求失败: {e.Message}", e);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("Code", out JsonElement code) && code.GetString() is string codeStr && codeStr != "200")
            {
                throw new TranslationException($"阿里云翻译接口错误 [{codeStr}]: {root.GetProperty("Message").GetString()}");
            }
            return root.GetProperty("Data").GetProperty("Translated").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or TranslationException)
        {
            throw new TranslationException($"阿里云翻译响应格式异常: {e.Message}", e);
        }
    }

    /// <summary>标准化语言代码。</summary>
    private static string NormalizeLang(string lang) =>
        LangMap.TryGetValue(lang, out string? mapped) ? mapped : lang;

    /// <summary>计算阿里云 HMAC-SHA1 签名。</summary>
    private string Sign(Dictionary<string, string> parameters)
    {
        string canonicalQuery = string.Join("&", parameters
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}"));
        string stringToSign = $"GET&%2F&{PercentEncode(canonicalQuery)}";
        byte[] signature = HMACSHA1.HashData(Encoding.UTF8.GetBytes($"{_accessKeySecret}&"), Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(signature);
    }

    /// <summary>阿里云要求的 URL 编码：空格 %20、* %2A、+ %2B、~ 保留（.NET EscapeDataString 恰符合）。</summary>
    private static string PercentEncode(string value) => Uri.EscapeDataString(value);
}
