using System;

namespace AutoOCRTranslator.Settings;

/// <summary>
/// UI 预设表：设置页与启动页的预设选项（翻译方向、覆盖层样式与背景、截图频率、识别区域）。
/// XAML 控件只声明 Content 文案与 Tag（预设 Key），代码后置经本类反查取值 —— 单一事实源。
/// </summary>
public static class UiPresets
{
    /// <summary>语言下拉选项。</summary>
    public sealed record LangOption(string Code, string Display);

    // ---- 识别区域（与 ImageRoi.RoiOcrParams 的 key 一致） ----
    public static readonly (string Key, string Display)[] RoiPresets =
    {
        ("subtitle", "字幕对话"),
        ("bottom", "底部全宽"),
        ("full", "全屏"),
        ("custom", "自定义矩形"),
        ("custom_zones", "自定义划分"),
    };

    /// <summary>识别区域预设 key → 显示名（找不到原样返回）。</summary>
    public static string RoiPresetDisplay(string key)
    {
        foreach (var (k, d) in RoiPresets)
            if (k == key) return d;
        return key;
    }

    // ---- 翻译方向 ----
    public sealed record TranslationDirection(string Key, string Display, string SourceLang, string TargetLang, bool IsCustom = false);

    public static readonly TranslationDirection[] TranslationDirections =
    {
        new("ja:zh-CN", "日语 → 简体中文", "ja", "zh-CN"),
        new("en:zh-CN", "英语 → 简体中文", "en", "zh-CN"),
        new("ko:zh-CN", "韩语 → 简体中文", "ko", "zh-CN"),
        new("zh-TW:zh-CN", "繁体中文 → 简体中文", "zh-TW", "zh-CN"),
        new("ja:en", "日语 → 英语", "ja", "en"),
        new("custom", "自定义…", "", "", IsCustom: true),
    };

    public static readonly LangOption[] SourceLanguages =
    {
        new("ja", "日语"), new("zh-CN", "简体中文"), new("zh-TW", "繁体中文"), new("zh", "中文"),
        new("en", "英语"), new("ko", "韩语"), new("ru", "俄语"), new("fr", "法语"),
        new("de", "德语"), new("es", "西班牙语"), new("pt", "葡萄牙语"), new("it", "意大利语"),
    };

    public static readonly LangOption[] TargetLanguages =
    {
        new("zh-CN", "简体中文"), new("zh-TW", "繁体中文"), new("en", "英语"), new("ja", "日语"),
        new("ko", "韩语"), new("ru", "俄语"), new("fr", "法语"), new("de", "德语"), new("es", "西班牙语"),
    };

    /// <summary>按源/目标语言反查预设 Key；不匹配返回 null（应选"自定义"）。</summary>
    public static string? FindDirectionKey(string sourceLang, string targetLang)
    {
        foreach (var d in TranslationDirections)
            if (!d.IsCustom
                && string.Equals(d.SourceLang, sourceLang, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.TargetLang, targetLang, StringComparison.OrdinalIgnoreCase))
                return d.Key;
        return null;
    }

    /// <summary>按预设 Key 反查方向；找不到返回 null。</summary>
    public static TranslationDirection? FindDirection(string key) =>
        Array.Find(TranslationDirections, d => d.Key == key);

    /// <summary>语言码 → 显示名（找不到原样返回）。</summary>
    public static string LanguageDisplay(string code)
    {
        foreach (var o in SourceLanguages)
            if (string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)) return o.Display;
        foreach (var o in TargetLanguages)
            if (string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)) return o.Display;
        return code;
    }

    // ---- 覆盖层样式（字号 + 字色组合） ----
    public sealed record OverlayStyle(string Key, string Display, int FontSize, string FontColor, bool IsCustom = false);

    public static readonly OverlayStyle[] OverlayStyles =
    {
        new("clean", "清爽白字", 18, "#FFFFFF"),
        new("bold", "大字醒目", 24, "#FFFFFF"),
        new("compact", "紧凑小字", 14, "#E8FFFFFF"),
        new("classic", "复古米黄", 18, "#FFE8C8"),
        new("custom", "自定义…", 0, "", IsCustom: true),
    };

    /// <summary>按当前字号+字色匹配样式预设；不匹配返回 null（应选"自定义"）。</summary>
    public static string? FindOverlayStyleKey(int fontSize, string fontColor)
    {
        foreach (var s in OverlayStyles)
            if (!s.IsCustom && s.FontSize == fontSize
                && string.Equals(s.FontColor, fontColor, StringComparison.OrdinalIgnoreCase))
                return s.Key;
        return null;
    }

    // ---- 覆盖层背景 ----
    public static readonly (string Key, string Display, string BgColor)[] OverlayBackgrounds =
    {
        ("bg_default", "半透明黑", "#80000000"),
        ("bg_dark", "深黑", "#CC000000"),
        ("bg_red", "暗红", "#80301818"),
        ("bg_none", "无背景", "#00000000"),
        ("bg_custom", "自定义…", ""),
    };

    /// <summary>按背景色匹配预设；不匹配返回 null（应选"自定义"）。</summary>
    public static string? FindOverlayBackgroundKey(string bgColor)
    {
        foreach (var (k, _, c) in OverlayBackgrounds)
            if (c.Length > 0 && string.Equals(c, bgColor, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    // ---- 截图频率 ----
    public sealed record CaptureInterval(string Key, string Display, int Ms, bool IsCustom = false);

    public static readonly CaptureInterval[] CaptureIntervals =
    {
        new("fast", "流畅 100ms", 100),
        new("normal", "标准 300ms", 300),
        new("eco", "省电 500ms", 500),
        new("custom", "自定义…", 0, IsCustom: true),
    };

    /// <summary>按毫秒匹配预设 Key；不匹配返回 null（应选"自定义"）。</summary>
    public static string? FindCaptureIntervalKey(int ms)
    {
        foreach (var i in CaptureIntervals)
            if (!i.IsCustom && i.Ms == ms) return i.Key;
        return null;
    }
}
