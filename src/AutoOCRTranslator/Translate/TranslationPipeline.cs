using System.Diagnostics;
using System.Threading.Channels;
using AutoOCRTranslator.Cache;
using AutoOCRTranslator.Settings;
using Serilog;

namespace AutoOCRTranslator.Translate;

/// <summary>翻译任务中的单个待翻译文本（Id 为该项在提交列表中的索引）。</summary>
public sealed record TranslationItem(int Id, string Text);

/// <summary>一次翻译任务：纯文本 + epoch，不含任何图像。</summary>
public sealed record TranslationJob(int JobId, int Epoch, IReadOnlyList<TranslationItem> Items, string SourceLang, string TargetLang);

/// <summary>翻译结果：按 Id 与提交的 TranslationItem 一一对应。</summary>
public sealed record TranslationResult(int Id, string Original, string Translated);

/// <summary>
/// 异步翻译管线（对应 Python 版翻译子进程 + 管理器）。
/// 与识别循环彻底解耦：识别循环只负责投递纯文本任务，网络翻译在后台 Channel 消费线程中执行；
/// 慢结果返回时由 UI 按 epoch 丢弃过期数据。
/// </summary>
public sealed class TranslationPipeline : IDisposable
{
    /// <summary>翻译完成：(epoch, results)。结果按原文文本与当前帧 merge。</summary>
    public event Action<int, IReadOnlyList<TranslationResult>>? TranslationFinished;

    private readonly Channel<TranslationJob> _channel;
    private readonly ITranslator _translator;
    private readonly TranslationCache? _cache;
    private readonly List<Task> _workers = [];
    private int _jobCounter;

    /// <summary>翻译缓存实例（可能为空：设置里禁用了缓存）。缓存管理页用它列举/删除/导出/导入。</summary>
    public TranslationCache? Cache => _cache;

    public TranslationPipeline(TranslateSection config, TranslationCache? cache)
    {
        _cache = cache;
        _channel = Channel.CreateUnbounded<TranslationJob>();
        try
        {
            _translator = TranslatorFactory.Create(config);
        }
        catch (TranslationException e)
        {
            Log.Warning("翻译器初始化失败（{Provider}）：{Error}", config.Provider, e.Message);
            _translator = new FallbackTranslator(e.Message);
        }

        int concurrency = Math.Clamp(config.Concurrency, 1, 8);
        StartWorkers(concurrency);
        Log.Information("翻译管线启动：provider={Provider}, 并发={Concurrency}, 缓存={CacheEnabled}",
            config.Provider, concurrency, cache is not null);
    }

    /// <summary>测试注入用：直接指定翻译器实现，跳过工厂创建。</summary>
    internal TranslationPipeline(ITranslator translator, int concurrency, TranslationCache? cache)
    {
        _translator = translator;
        _cache = cache;
        _channel = Channel.CreateUnbounded<TranslationJob>();
        StartWorkers(Math.Clamp(concurrency, 1, 8));
    }

    private void StartWorkers(int concurrency)
    {
        for (int i = 0; i < concurrency; i++)
        {
            _workers.Add(Task.Run(WorkerLoop));
        }
    }

    /// <summary>缓存查询（识别循环内同步调用，命中即填译文、不联网；未命中返回 null）。</summary>
    public string? GetFromCache(string text, string sourceLang, string targetLang)
        => _cache?.Get(text, sourceLang, targetLang);

    /// <summary>提交翻译任务（纯文本 + epoch），立即返回，不阻塞识别循环。返回任务 ID，失败返回 -1。</summary>
    public int Submit(int epoch, IReadOnlyList<TranslationItem> items, string sourceLang, string targetLang)
    {
        if (items.Count == 0) return -1;
        var job = new TranslationJob(Interlocked.Increment(ref _jobCounter), epoch, items, sourceLang, targetLang);
        if (_channel.Writer.TryWrite(job)) return job.JobId;
        return -1;
    }

    public void Stop()
    {
        _channel.Writer.TryComplete();
        try
        {
            Task.WaitAll([.. _workers], TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }
        _workers.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>消费者循环：从 Channel 取任务，处理（网络 IO 阻塞在该线程，与识别循环隔离）。</summary>
    private async Task WorkerLoop()
    {
        await foreach (TranslationJob job in _channel.Reader.ReadAllAsync())
        {
            try
            {
                ProcessJob(job);
            }
            catch (Exception e)
            {
                Log.Warning("翻译任务执行失败: {Error}", e.Message);
            }
        }
    }

    /// <summary>处理一个翻译任务：查缓存 → 批量翻译 → 写回缓存（对应 Python 版 _process_job）。</summary>
    private void ProcessJob(TranslationJob job)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<TranslationResult>(job.Items.Count);
        var toTranslate = new List<TranslationItem>();

        // 第一步：缓存命中直接出译文
        foreach (TranslationItem item in job.Items)
        {
            string? translated = _cache?.Get(item.Text, job.SourceLang, job.TargetLang);
            if (translated is not null)
            {
                results.Add(new TranslationResult(item.Id, item.Text, translated));
            }
            else
            {
                toTranslate.Add(item);
            }
        }

        // 第二步：未命中的文本批量翻译（网络调用，消费线程中执行）
        if (toTranslate.Count > 0)
        {
            List<string> parts = BatchTranslate(toTranslate.Select(i => i.Text).ToList(), job.SourceLang, job.TargetLang);
            for (int i = 0; i < toTranslate.Count; i++)
            {
                string translated = parts[i];
                if (_cache is not null && !string.IsNullOrEmpty(translated))
                {
                    _cache.Set(toTranslate[i].Text, job.SourceLang, job.TargetLang, translated);
                }
                results.Add(new TranslationResult(toTranslate[i].Id, toTranslate[i].Text, translated));
            }
        }

        stopwatch.Stop();
        Log.Information("翻译任务完成: job_id={JobId}, epoch={Epoch}, items={Count}, 耗时 {Elapsed:F0}ms",
            job.JobId, job.Epoch, job.Items.Count, stopwatch.Elapsed.TotalMilliseconds);
        TranslationFinished?.Invoke(job.Epoch, results);
    }

    /// <summary>
    /// 批量翻译：多条文本以换行合并为一次 API 调用；条数不匹配或失败时逐条回退；
    /// 单条失败以原文兜底（显示原文而非空白）。对应 Python 版 _batch_translate。
    /// </summary>
    private List<string> BatchTranslate(List<string> texts, string sourceLang, string targetLang)
    {
        if (texts.Count == 0) return [];
        if (texts.Count == 1)
        {
            try
            {
                return [_translator.Translate(texts[0], sourceLang, targetLang)];
            }
            catch (Exception e)
            {
                Log.Warning("翻译失败 '{Text}': {Error}", texts[0], e.Message);
                return [texts[0]];
            }
        }

        string joined = string.Join("\n", texts.Select(t => t.Replace("\n", " ")));
        try
        {
            string result = _translator.Translate(joined, sourceLang, targetLang);
            string[] parts = result.Split('\n');
            if (parts.Length == texts.Count) return [.. parts];
            Log.Warning("批量翻译条数不匹配（期望 {Expected}，实际 {Actual}），逐条回退", texts.Count, parts.Length);
        }
        catch (Exception e)
        {
            Log.Warning("批量翻译失败，逐条回退: {Error}", e.Message);
        }

        // 回退：逐条翻译，失败以原文兜底
        var results = new List<string>(texts.Count);
        foreach (string text in texts)
        {
            try
            {
                results.Add(_translator.Translate(text, sourceLang, targetLang));
            }
            catch (Exception e)
            {
                Log.Warning("单条翻译失败，以原文兜底: {Error}", e.Message);
                results.Add(text);
            }
        }
        return results;
    }

    /// <summary>翻译器初始化失败时的兜底：直接返回原文（识别流程永不中断）。</summary>
    private sealed class FallbackTranslator(string reason) : ITranslator
    {
        public string Translate(string text, string sourceLang, string targetLang)
        {
            Log.Warning("翻译被禁用（{Reason}），返回原文", reason);
            return text;
        }
    }
}
