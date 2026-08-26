using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate.Providers;

namespace AutoOCRTranslator.Translate;

/// <summary>翻译器工厂（对应 Python 版 create_translator）。</summary>
public static class TranslatorFactory
{
    /// <summary>根据配置创建翻译器。</summary>
    /// <exception cref="TranslationException">提供者名称未知或缺少凭据时抛出。</exception>
    public static ITranslator Create(TranslateSection config) => config.Provider switch
    {
        "google_free" => new GoogleFreeTranslator(config.Timeout, config.MaxRetries, ProxyOrNull(config.Proxy)),
        "tencent" => new TencentTranslator(config.ApiKey, config.ApiSecret, config.Timeout, ProxyOrNull(config.Proxy)),
        "deep_l" => new DeepLTranslator(config.ApiKey, config.ApiSecret, config.Timeout, ProxyOrNull(config.Proxy)),
        "aliyun" => new AliyunTranslator(config.ApiKey, config.ApiSecret, config.Timeout, ProxyOrNull(config.Proxy)),
        _ => throw new TranslationException($"未知的翻译提供者: {config.Provider}"),
    };

    private static string? ProxyOrNull(string proxy) => string.IsNullOrWhiteSpace(proxy) ? null : proxy;
}
