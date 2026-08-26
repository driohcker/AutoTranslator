namespace AutoOCRTranslator.Translate;

/// <summary>翻译相关异常（对应 Python 版 TranslationError）。</summary>
public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }

    public TranslationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>翻译提供者抽象接口（对应 Python 版 TranslationProvider）。</summary>
public interface ITranslator
{
    /// <summary>
    /// 翻译文本。
    /// </summary>
    /// <param name="text">待翻译文本。</param>
    /// <param name="sourceLang">源语言代码，如 ja、en、zh-CN。</param>
    /// <param name="targetLang">目标语言代码，如 zh-CN、en。</param>
    /// <returns>翻译后的文本。</returns>
    /// <exception cref="TranslationException">翻译失败时抛出。</exception>
    string Translate(string text, string sourceLang, string targetLang);

    /// <summary>测试翻译服务是否可用（默认实现翻译一个英文句子）。</summary>
    bool Test()
    {
        try
        {
            return !string.IsNullOrEmpty(Translate("hello", "en", "zh-CN"));
        }
        catch
        {
            return false;
        }
    }
}
