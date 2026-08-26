using System.Diagnostics;
using System.Drawing;
using AutoOCRTranslator.Models;
using AutoOCRTranslator.Ocr;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using AutoOCRTranslator.Utils;
using Serilog;

namespace AutoOCRTranslator.Capture;

/// <summary>单个待识别区域：缩放后的图像、在全窗中的偏移、缩放比例。</summary>
public sealed record Zone(Bitmap Image, (int X, int Y) Offset, double ScaleRatio);

/// <summary>
/// 截图与变化检测循环（对应 Python 版 CaptureThread）。
/// 在后台 Task 中以固定间隔截帧，dHash 变化检测挡掉无变化帧；
/// 识别慢于间隔时不补睡、直接追上最新帧（deadline 追帧）。
/// 事件在后台线程触发，订阅者需自行调度回 UI 线程。
/// </summary>
public sealed class CaptureLoop : IDisposable
{
    /// <summary>有变化的一帧已准备好（Phase 3 后会在事件前插入本地 OCR）。订阅者不得持有图像引用。</summary>
    public event Action<IReadOnlyList<Zone>>? FrameCaptured;

    /// <summary>画面无变化被跳过的帧数。</summary>
    public event Action<int>? FrameSkipped;

    /// <summary>目标窗口无效，循环应停止。</summary>
    public event Action? WindowInvalid;

    /// <summary>本帧本地 OCR 完成（不联网）：(epoch, items, elapsedMs)。过期 epoch 的结果由 UI 丢弃。</summary>
    public event Action<int, IReadOnlyList<OcrItem>, double>? OcrReady;

    private readonly nint _hwnd;
    private readonly int _intervalMs;
    private readonly bool _changeDetection;
    private readonly AppSettings _settings;
    private readonly ICaptureSource _capture;
    private readonly ChangeDetector _changeDetector;
    private readonly IOcrEngine _ocrEngine;
    private readonly TranslationPipeline? _pipeline;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _skippedFrames;
    private int _epoch;

    public CaptureLoop(nint hwnd, AppSettings settings, TranslationPipeline? pipeline = null)
    {
        _hwnd = hwnd;
        _settings = settings;
        _pipeline = pipeline;
        _intervalMs = Math.Max(1, settings.Capture.IntervalMs);
        _changeDetection = settings.Capture.ChangeDetection;
        _changeDetector = new ChangeDetector(settings.Capture.ChangeThreshold);
        // 按模式选择截图源：全屏模式 BitBlt 整个虚拟屏，目标窗口模式抓目标窗口客户区
        _capture = settings.Capture.Mode == CaptureMode.FullScreen
            ? new ScreenCapture()
            : new WindowCapture();

        // 本地 OCR 引擎（模型懒加载）；按用户设置的置信度阈值构建（设置页滑块）
        _ocrEngine = new RapidOcrEngine(Math.Clamp(settings.Ocr.DropScore, 0, 1));
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(Loop);
        Log.Information("捕获循环启动，模式={Mode}，间隔 {Interval}ms，变化检测={ChangeDetection}，目标 hwnd={Hwnd}",
            _settings.Capture.Mode, _intervalMs, _changeDetection, _hwnd);
    }

    public void Stop()
    {
        if (_loopTask is null) return;
        _cts?.Cancel();
        try
        {
            _loopTask.Wait(2000);
        }
        catch (AggregateException)
        {
        }
        _loopTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        var token = _cts!.Token;
        try
        {
            _capture.SetTarget(_hwnd);
        }
        catch (Exception e)
        {
            Log.Warning("设置目标窗口失败: {Error}", e.Message);
            WindowInvalid?.Invoke();
            return;
        }

        long intervalTicks = _intervalMs * Stopwatch.Frequency / 1000;

        while (!token.IsCancellationRequested)
        {
            long deadline = Stopwatch.GetTimestamp() + intervalTicks;

            if (!_capture.IsValid())
            {
                Log.Warning("目标窗口无效，捕获循环退出");
                WindowInvalid?.Invoke();
                return;
            }

            Bitmap? frame = _capture.Capture();
            if (frame is null)
            {
                SleepTo(deadline, token);
                continue;
            }

            List<Zone> zones;
            using (frame)
            {
                try
                {
                    zones = BuildZones(frame);
                }
                catch (Exception e)
                {
                    Log.Warning("ROI 预处理失败: {Error}", e.Message);
                    SleepTo(deadline, token);
                    continue;
                }
            }

            if (zones.Count == 0)
            {
                SleepTo(deadline, token);
                continue;
            }

            // 便宜优先：画面无变化则跳过本帧，不进入后续处理
            if (_changeDetection)
            {
                try
                {
                    bool changed = _changeDetector.Check(zones.Select(z => z.Image).ToList());
                    if (!changed)
                    {
                        _skippedFrames++;
                        FrameSkipped?.Invoke(_skippedFrames);
                        foreach (Zone z in zones) z.Image.Dispose();
                        SleepTo(deadline, token);
                        continue;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning("变化检测失败: {Error}", e.Message);
                }
                _skippedFrames = 0;
            }

            // 发送预览（后台线程内转换，UI 只接收 BitmapSource）
            FrameCaptured?.Invoke(zones);

            // 本地 OCR（识别循环内，不联网；翻译由异步管线承接）
            var stopwatch = Stopwatch.StartNew();
            var allItems = new List<OcrItem>();
            foreach (Zone zone in zones)
            {
                try
                {
                    allItems.AddRange(OcrFlow.RunOcrFlow(
                        zone.Image, zone.ScaleRatio, _ocrEngine, zone.Offset,
                        _settings.Translate.SourceLang, _settings.Translate.TargetLang,
                        _settings.Translate.FilterSourceLang, _settings.Translate.StrictSourceLang,
                        cacheGet: text => _pipeline?.GetFromCache(text,
                            _settings.Translate.SourceLang, _settings.Translate.TargetLang)));
                }
                catch (Exception e)
                {
                    Log.Warning("本地 OCR 失败: {Error}", e.Message);
                }
            }
            stopwatch.Stop();
            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

            _epoch++;
            OcrReady?.Invoke(_epoch, allItems, elapsedMs);

            // 异步投递翻译任务：仅未命中缓存的文本，纯文本不携带图像，识别循环零等待
            if (_pipeline is not null)
            {
                var pending = allItems
                    .Where(i => string.IsNullOrEmpty(i.Translated))
                    .Select((item, index) => new TranslationItem(index, item.Original))
                    .ToList();
                if (pending.Count > 0)
                {
                    int jobId = _pipeline.Submit(_epoch, pending,
                        _settings.Translate.SourceLang, _settings.Translate.TargetLang);
                    if (jobId < 0) Log.Warning("翻译任务提交失败");
                }
            }
            Log.Information("OCR 完成：{Count} 个文本块，耗时 {Elapsed:F0}ms", allItems.Count, elapsedMs);

            foreach (Zone z in zones) z.Image.Dispose();

            if (token.IsCancellationRequested) return;
            SleepTo(deadline, token);
        }

        Log.Information("捕获循环已结束");
    }

    private List<Zone> BuildZones(Bitmap frame)
    {
        string preset = _settings.Ocr.RoiPreset;
        if (preset == "custom_zones")
        {
            var roiZones = _settings.Ocr.RoiZones;
            if (roiZones is null || roiZones.Count == 0)
            {
                roiZones = [[0.0, 0.0, 1.0, 1.0]];
            }
            int maxWidth = ImageRoi.RoiOcrParams["custom_zones"].MaxWidth;
            return ImageRoi.PrepareMultiZones(frame, roiZones.Select(z => z.ToArray()).ToList(), maxWidth)
                .Select(z => new Zone(z.Image, z.Offset, z.ScaleRatio))
                .ToList();
        }

        double[] customRoi = _settings.Ocr.RoiCustom.Length == 4
            ? _settings.Ocr.RoiCustom
            : [0.1, 0.75, 0.8, 0.2];
        int mw = ImageRoi.RoiOcrParams.GetValueOrDefault(preset, ImageRoi.RoiOcrParams["subtitle"]).MaxWidth;
        var zone = ImageRoi.PrepareSingleZone(frame, preset, customRoi, mw);
        return [new Zone(zone.Image, zone.Offset, zone.ScaleRatio)];
    }

    /// <summary>睡眠到 deadline；已过期则立即返回（追帧）。</summary>
    private static void SleepTo(long deadlineTicks, CancellationToken token)
    {
        long remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0) return;
        int ms = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
        token.WaitHandle.WaitOne(Math.Max(1, ms));
    }
}
