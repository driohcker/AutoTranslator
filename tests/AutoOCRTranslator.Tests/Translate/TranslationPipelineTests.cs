using System.IO;
using AutoOCRTranslator.Cache;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using Xunit;

namespace AutoOCRTranslator.Tests.Translate;

/// <summary>
/// 翻译管线：事件携带正确 epoch、缓存命中不联网、批量条数不匹配逐条回退、
/// 未知 provider 以原文兜底。全部用 FakeTranslator，无网络。
/// </summary>
public class TranslationPipelineTests
{
    /// <summary>可控假翻译器：统计调用次数，可模拟批量条数不匹配。</summary>
    private sealed class FakeTranslator : ITranslator
    {
        public int CallCount;
        public bool MangleBatch; // true 时对含换行的输入返回错误条数

        public string Translate(string text, string sourceLang, string targetLang)
        {
            Interlocked.Increment(ref CallCount);
            if (MangleBatch && text.Contains('\n')) return "MERGE:" + text.Length; // 1 条而非 N 条
            return "[T]" + text;
        }
    }

    private static TranslateSection Config() => new()
    {
        Provider = "fake",
        Concurrency = 2,
        Timeout = 5,
        MaxRetries = 1,
        SourceLang = "ja",
        TargetLang = "zh-CN",
    };

    /// <summary>测试注入构造：直接使用 FakeTranslator。</summary>
    private static TranslationPipeline CreatePipeline(FakeTranslator translator, TranslationCache? cache = null, int concurrency = 2)
        => new(translator, concurrency, cache);

    /// <summary>提交任务并等待事件；返回 (epoch, results) 或超时 null。</summary>
    private static (int Epoch, IReadOnlyList<TranslationResult> Results)? SubmitAndWait(
        TranslationPipeline pipeline, int epoch, List<TranslationItem> items, string src = "ja", string tgt = "zh-CN")
    {
        using var done = new ManualResetEventSlim();
        (int epoch, IReadOnlyList<TranslationResult> results)? captured = null;
        pipeline.TranslationFinished += (e, results) =>
        {
            captured = (e, results);
            done.Set();
        };
        pipeline.Submit(epoch, items, src, tgt);
        return done.Wait(TimeSpan.FromSeconds(10)) ? captured : null;
    }

    [Fact]
    public void Event_CarriesEpochAndResults()
    {
        using var pipeline = CreatePipeline(new FakeTranslator());
        var result = SubmitAndWait(pipeline, epoch: 7,
            [new TranslationItem(0, "こんにちは"), new TranslationItem(1, "おはよう")]);

        Assert.NotNull(result);
        Assert.Equal(7, result.Value.Epoch);
        Assert.Equal(2, result.Value.Results.Count);
        Assert.Equal("[T]こんにちは", result.Value.Results[0].Translated);
        Assert.Equal("おはよう", result.Value.Results[1].Original);
    }

    [Fact]
    public void CacheHit_DoesNotCallNetwork()
    {
        using var cache = new TranslationCache(Path.Combine(Path.GetTempPath(), $"pipe_cache_{Guid.NewGuid():N}.db"));
        cache.Set("こんにちは", "ja", "zh-CN", "你好");
        var translator = new FakeTranslator();
        using var pipeline = CreatePipeline(translator, cache);

        var result = SubmitAndWait(pipeline, epoch: 1, [new TranslationItem(0, "こんにちは")]);
        Assert.NotNull(result);
        Assert.Equal("你好", result.Value.Results[0].Translated);
        Assert.Equal(0, translator.CallCount); // 缓存命中，未命中文本为 0 → 不调用网络
    }

    [Fact]
    public void CacheMiss_TranslatesOnlyMissing()
    {
        using var cache = new TranslationCache(Path.Combine(Path.GetTempPath(), $"pipe_cache2_{Guid.NewGuid():N}.db"));
        cache.Set("命中", "ja", "zh-CN", "已缓存");
        var translator = new FakeTranslator();
        using var pipeline = CreatePipeline(translator, cache);

        var result = SubmitAndWait(pipeline, epoch: 1,
            [new TranslationItem(0, "命中"), new TranslationItem(1, "未命中")]);
        Assert.NotNull(result);
        Assert.Equal("已缓存", result.Value.Results[0].Translated);
        Assert.Equal("[T]未命中", result.Value.Results[1].Translated);
        Assert.Equal(1, translator.CallCount); // 只翻译未命中的一条
    }

    [Fact]
    public void UnknownProvider_FallsBackToOriginal()
    {
        var config = Config();
        config.Provider = "not_exist";
        using var pipeline = new TranslationPipeline(config, cache: null);

        var result = SubmitAndWait(pipeline, epoch: 1, [new TranslationItem(0, "こんにちは")]);
        Assert.NotNull(result);
        Assert.Equal("こんにちは", result.Value.Results[0].Translated); // 原文兜底
    }

    [Fact]
    public void BatchMismatch_FallsBackPerItem()
    {
        var translator = new FakeTranslator { MangleBatch = true };
        using var pipeline = CreatePipeline(translator);

        var result = SubmitAndWait(pipeline, epoch: 3,
            [new TranslationItem(0, "甲"), new TranslationItem(1, "乙"), new TranslationItem(2, "丙")]);

        Assert.NotNull(result);
        Assert.Equal(3, result.Value.Results.Count);
        Assert.Equal("[T]甲", result.Value.Results[0].Translated);
        Assert.Equal("[T]乙", result.Value.Results[1].Translated);
        Assert.Equal("[T]丙", result.Value.Results[2].Translated);
        Assert.Equal(1 + 3, translator.CallCount); // 1 次批量 + 3 次逐条
    }

    [Fact]
    public void Submit_EmptyItems_ReturnsMinusOne()
    {
        using var pipeline = CreatePipeline(new FakeTranslator());
        Assert.Equal(-1, pipeline.Submit(1, [], "ja", "zh-CN"));
    }

    [Fact]
    public void GetFromCache_ReturnsCachedOrNull()
    {
        using var cache = new TranslationCache(Path.Combine(Path.GetTempPath(), $"pipe_cache3_{Guid.NewGuid():N}.db"));
        cache.Set("abc", "en", "zh-CN", "甲乙丙");
        using var pipeline = CreatePipeline(new FakeTranslator(), cache);

        Assert.Equal("甲乙丙", pipeline.GetFromCache("abc", "en", "zh-CN"));
        Assert.Null(pipeline.GetFromCache("不在", "en", "zh-CN"));
    }
}
