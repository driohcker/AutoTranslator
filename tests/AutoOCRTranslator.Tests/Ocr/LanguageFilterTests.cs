using AutoOCRTranslator.Ocr;
using Xunit;

namespace AutoOCRTranslator.Tests.Ocr;

/// <summary>语言过滤：日文/中文/英文识别、URL/纯数字/符号排除、严格模式。</summary>
public class LanguageFilterTests
{
    [Theory]
    [InlineData("こんにちは")]
    [InlineData("日本語のテキスト")]
    [InlineData("カタカナ")] // 片假名
    [InlineData("世界")]     // 纯汉字非严格 → 放行
    public void Japanese_AlwaysTranslates(string text)
    {
        // 日文场景：含假名恒翻译；纯汉字在非严格模式下也放行
        Assert.True(LanguageFilter.ShouldTranslate(text, "ja"));
    }

    [Fact]
    public void Japanese_StrictMode_RejectsPureHan()
    {
        Assert.True(LanguageFilter.ShouldTranslate("こんにちは", "ja", strict: true));  // 含假名 → 翻译
        Assert.False(LanguageFilter.ShouldTranslate("世界", "ja", strict: true));       // 纯汉字 → 过滤
    }

    [Theory]
    [InlineData("Hello World")]
    [InlineData("Settings")]
    [InlineData("File Explorer")]
    public void Japanese_PureLatin_IsRejected(string text)
    {
        Assert.False(LanguageFilter.ShouldTranslate(text, "ja"));
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("www.google.com")]
    [InlineData("http://127.0.0.1:7890")]
    public void Url_IsRejected(string text)
    {
        Assert.False(LanguageFilter.ShouldTranslate(text, "ja"));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("!!##@@")]
    [InlineData("42.5")]
    public void DigitsOrSymbols_IsRejected(string text)
    {
        Assert.False(LanguageFilter.ShouldTranslate(text, "ja"));
    }

    [Fact]
    public void Chinese_HanMatches()
    {
        Assert.True(LanguageFilter.ShouldTranslate("你好世界", "zh"));
        Assert.False(LanguageFilter.ShouldTranslate("hello", "zh"));
    }

    [Fact]
    public void English_LatinMatches()
    {
        Assert.True(LanguageFilter.ShouldTranslate("Hello world", "en"));
        Assert.False(LanguageFilter.ShouldTranslate("こんにちは", "en"));
    }

    [Fact]
    public void Korean_HangulMatches()
    {
        Assert.True(LanguageFilter.ShouldTranslate("안녕하세요", "ko"));
        Assert.False(LanguageFilter.ShouldTranslate("hello", "ko"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")] // 长度不足
    [InlineData(null)]
    public void EmptyOrTooShort_IsRejected(string? text)
    {
        Assert.False(LanguageFilter.ShouldTranslate(text, "ja"));
    }

    [Fact]
    public void UnknownLang_DefaultsToTranslate()
    {
        Assert.True(LanguageFilter.ShouldTranslate("任意内容", "xx"));
    }
}
