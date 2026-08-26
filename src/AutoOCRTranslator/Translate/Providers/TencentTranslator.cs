using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoOCRTranslator.Translate.Providers;

/// <summary>
/// 腾讯云机器翻译（TMT）提供者（对应 Python 版 tencent），TC3-HMAC-SHA256 签名。
/// 需要 SecretId（api_key）和 SecretKey（api_secret）。
/// </summary>
public sealed class TencentTranslator : ITranslator
{
    private const string Host = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string Version = "2018-03-21";
    private const string Action = "TextTranslate";
    private const string Region = "ap-guangzhou";
    private static readonly Uri Url = new($"https://{Host}");

    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "ja", ["japan"] = "ja",
        ["zh"] = "zh", ["zh-cn"] = "zh", ["zh-tw"] = "zh-TW", ["ch"] = "zh", ["ch_tra"] = "zh-TW",
        ["en"] = "en", ["ko"] = "ko",
    };

    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly HttpClient _http;

    public TencentTranslator(string secretId = "", string secretKey = "", int timeout = 10, string? proxy = null)
    {
        if (string.IsNullOrEmpty(secretId) || string.IsNullOrEmpty(secretKey))
        {
            throw new TranslationException("腾讯翻译需要提供 SecretId 和 SecretKey（对应设置中的 API Key 和 API Secret）");
        }
        _secretId = secretId;
        _secretKey = secretKey;
        _region = Region;

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
            ["SourceText"] = text,
            ["Source"] = NormalizeLang(sourceLang),
            ["Target"] = NormalizeLang(targetLang),
            ["ProjectId"] = 0,
        };
        string payloadJson = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, Url);
        foreach (KeyValuePair<string, string> header in BuildHeaders(payloadJson))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        string json;
        try
        {
            json = _http.SendAsync(request).GetAwaiter().GetResult().EnsureSuccessStatusCode()
                .Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new TranslationException($"腾讯翻译网络请求失败: {e.Message}", e);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Response", out JsonElement response) &&
                response.TryGetProperty("Error", out JsonElement error))
            {
                throw new TranslationException(
                    $"腾讯翻译接口错误 [{error.GetProperty("Code").GetString()}]: {error.GetProperty("Message").GetString()}");
            }
            return response.GetProperty("TargetText").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or TranslationException)
        {
            throw new TranslationException($"腾讯翻译响应格式异常: {e.Message}", e);
        }
    }

    /// <summary>标准化语言代码。</summary>
    private static string NormalizeLang(string lang) =>
        LangMap.TryGetValue(lang, out string? mapped) ? mapped : lang;

    /// <summary>构建带 TC3-HMAC-SHA256 签名的请求头。</summary>
    private Dictionary<string, string> BuildHeaders(string payloadJson)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd");

        string payloadHash = Sha256Hex(payloadJson);

        // 规范请求
        string canonicalHeaders = $"content-type:application/json; charset=utf-8\nhost:{Host}\n";
        const string signedHeaders = "content-type;host";
        string canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        // 待签名字符串
        string credentialScope = $"{date}/{Service}/tc3_request";
        string hashedCanonicalRequest = Sha256Hex(canonicalRequest);
        string stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{hashedCanonicalRequest}";

        // 计算签名
        byte[] secretDate = HmacSha256(Encoding.UTF8.GetBytes($"TC3{_secretKey}"), date);
        byte[] secretService = HmacSha256(secretDate, Service);
        byte[] secretSigning = HmacSha256(secretService, "tc3_request");
        string signature = Hex(HmacSha256(secretSigning, stringToSign));

        return new Dictionary<string, string>
        {
            ["Authorization"] = $"TC3-HMAC-SHA256 Credential={_secretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}",
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Host"] = Host,
            ["X-TC-Action"] = Action,
            ["X-TC-Version"] = Version,
            ["X-TC-Timestamp"] = timestamp.ToString(),
            ["X-TC-Region"] = _region,
        };
    }

    private static string Sha256Hex(string input) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static byte[] HmacSha256(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
