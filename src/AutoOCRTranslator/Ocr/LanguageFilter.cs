using System.Text.RegularExpressions;

namespace AutoOCRTranslator.Ocr;

/// <summary>
/// 语言过滤（对应 Python 版 lang_filter.py）：
/// 判断文本是否属于源语言，避免把 URL、UI 按钮、纯数字等送入翻译流程。
/// </summary>
public static partial class LanguageFilter
{
    // 平假名 + 片假名
    [GeneratedRegex(@"[぀-ゟ゠-ヿ]")]
    private static partial Regex KanaRegex();

    // CJK 统一汉字
    [GeneratedRegex(@"[一-鿿]")]
    private static partial Regex HanRegex();

    // 纯英文
    [GeneratedRegex(@"^[a-zA-Z\s]+$")]
    private static partial Regex LatinRegex();

    // 韩文
    [GeneratedRegex(@"[가-힯ᄀ-ᇿ㄰-㆏]")]
    private static partial Regex HangulRegex();

    // 纯数字 / 纯符号
    [GeneratedRegex(@"^[\d\s\W]+$")]
    private static partial Regex DigitsOrSymbolsRegex();

    /// <summary>
    /// 综合判断一段文本是否值得翻译。
    /// 严格模式下日文只翻译包含假名的文本，避免把中文 UI 汉字误判为日文。
    /// </summary>
    public static bool ShouldTranslate(string? text, string sourceLang, bool strict = false, int minLength = 2)
    {
        if (string.IsNullOrEmpty(text)) return false;

        string s = text.Trim();
        if (s.Length < minLength) return false;

        // 过滤 URL
        if (s.Contains("http") || s.Contains("www.") || s.Contains(".com")) return false;

        // 过滤纯数字 / 纯符号
        if (DigitsOrSymbolsRegex().IsMatch(s)) return false;

        string lang = sourceLang.ToLowerInvariant();

        // 日文场景
        if (lang is "ja" or "japan")
        {
            if (KanaRegex().IsMatch(s)) return true;      // 含假名 → 日文
            if (LatinRegex().IsMatch(s)) return false;    // 明显英文 UI
            if (strict) return false;                     // 严格模式：纯汉字过滤
            return true;                                  // 非严格模式：放行日文汉字
        }

        return lang switch
        {
            "zh" or "zh-cn" or "zh-tw" => HanRegex().IsMatch(s),
            "en" => LatinRegex().IsMatch(s),
            "ko" => HangulRegex().IsMatch(s),
            _ => true, // 未定义的语言默认放行，避免误过滤
        };
    }
}
