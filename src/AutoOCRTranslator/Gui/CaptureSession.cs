using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoOCRTranslator.Capture;
using AutoOCRTranslator.Models;
using AutoOCRTranslator.Overlay;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using AutoOCRTranslator.Utils;
using Serilog;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 识别会话协调器：统一持有 CaptureLoop 与 OverlayWindow，
/// 向「启动页（控制）」「识别页（展示）」广播状态 / 帧 / 结果事件。
/// 全部事件经 Dispatcher 调度回 UI 线程，页面订阅后可直接更新控件。
/// 功能逻辑（截图 / 变化检测 / OCR / 翻译 / 覆盖层）与原先 CapturePage 保持一致，仅属主迁移。
/// </summary>
public sealed class CaptureSession : IDisposable
{
    /// <summary>状态行文本（正在识别 / 已停止 / 窗口失效 / 窗口数等）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>运行状态变化（true = 识别中）。</summary>
    public event Action<bool>? RunningChanged;

    /// <summary>预览帧（已 Freeze，UI 线程）。</summary>
    public event Action<BitmapSource>? FramePreviewed;

    /// <summary>OCR 结果列表（含译文 merge 后的最新状态）。</summary>
    public event Action<IReadOnlyList<OcrItem>>? ItemsUpdated;

    /// <summary>统计 (elapsedMs, blocks, pending, skippedFrames)。</summary>
    public event Action<int, int, int, int>? StatsUpdated;

    private readonly AppSettings _settings;
    private readonly string _configPath;
    private readonly TranslationPipeline _pipeline;
    private readonly Dispatcher _dispatcher;
    private CaptureLoop? _loop;
    private OverlayWindow? _overlay;
    private nint? _targetHwnd;
    private int _skippedFrames;
    private int _currentEpoch;
    private double _lastElapsedMs;
    private List<OcrItem> _currentItems = [];

    public CaptureSession(AppSettings settings, string configPath, TranslationPipeline pipeline)
    {
        _settings = settings;
        _configPath = configPath;
        _pipeline = pipeline;
        _dispatcher = Application.Current.Dispatcher;
    }

    /// <summary>共享配置对象（页面只读展示用；写入统一走设置页）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>当前选中的目标窗口句柄（设置页区域划分用）。</summary>
    public nint? TargetHwnd => _targetHwnd;

    public bool IsRunning => _loop is not null;

    /// <summary>枚举可见窗口并广播状态。</summary>
    public IReadOnlyList<WindowInfo> RefreshWindows()
    {
        var windows = WindowEnumerator.GetVisibleWindows();
        RaiseStatus($"共 {windows.Count} 个可见窗口");
        return windows;
    }

    public void SelectWindow(WindowInfo info)
    {
        _targetHwnd = info.Hwnd;
        RaiseStatus($"已选择目标窗口：{info.Title}");
    }

    /// <summary>启动识别；目标窗口模式未选窗口时仅提示，全屏模式无需窗口。</summary>
    public void Start()
    {
        if (_loop is not null) return;
        if (_settings.Capture.Mode == CaptureMode.FullScreen)
        {
            // 全屏模式 target rect = 整个虚拟屏，不依赖目标窗口
            StartLoop(nint.Zero);
            return;
        }
        if (_targetHwnd is not nint hwnd)
        {
            RaiseStatus("请先选择目标窗口");
            return;
        }
        StartLoop(hwnd);
    }

    public void Stop() => StopLoop();

    /// <summary>设置保存后热重启：运行中则按新配置重启识别循环与覆盖层。</summary>
    public void RestartIfRunning()
    {
        bool wasRunning = _loop is not null;
        if (!wasRunning) return;
        StopLoop();
        // 全屏模式无需窗口（hwnd=0）；目标窗口模式沿用已选窗口
        nint hwnd = _targetHwnd ?? nint.Zero;
        StartLoop(hwnd);
    }

    /// <summary>
    /// 切换截图模式：清空已划分区域（两模式 target rect 不同，旧 zone 不再适用）、
    /// 落盘配置、运行中则按新模式热重启。确认弹窗由调用方先完成。
    /// </summary>
    public void SetCaptureMode(CaptureMode mode)
    {
        if (_settings.Capture.Mode == mode) return;
        _settings.Capture.Mode = mode;
        _settings.Ocr.RoiZones.Clear();
        SettingsLoader.Save(_configPath, _settings);
        Log.Information("截图模式切换为 {Mode}，已清空区域并保存", mode);
        RestartIfRunning();
    }

    /// <summary>停止识别并关闭覆盖层（应用退出时调用）。</summary>
    public void Shutdown()
    {
        StopLoop();
        _overlay?.Close();
        _overlay = null;
    }

    public void Dispose() => Shutdown();

    private void StartLoop(nint hwnd)
    {
        _pipeline.TranslationFinished += OnTranslationFinished;
        _loop = new CaptureLoop(hwnd, _settings, _pipeline);
        _loop.FrameCaptured += OnFrameCaptured;
        _loop.FrameSkipped += OnFrameSkipped;
        _loop.OcrReady += OnOcrReady;
        _loop.WindowInvalid += OnWindowInvalid;
        _loop.Start();

        // 覆盖层：跟随目标窗口，显示译文
        _overlay?.Close();
        _overlay = new OverlayWindow(hwnd, _settings.Overlay, _settings.Capture.Mode);
        // 补一次当前帧：防止「首次 OCR 早于 overlay 创建」导致画面静止时无内容
        _overlay.UpdateItems(_currentItems, _settings.Ocr.RoiZones);

        RaiseStatus($"正在识别（{_settings.Capture.IntervalMs}ms/帧，变化检测生效中）");
        RaiseRunning(true);
    }

    private void StopLoop()
    {
        if (_loop is null) return;
        _loop.Stop();
        _loop.FrameCaptured -= OnFrameCaptured;
        _loop.FrameSkipped -= OnFrameSkipped;
        _loop.OcrReady -= OnOcrReady;
        _loop.WindowInvalid -= OnWindowInvalid;
        _loop = null;
        _pipeline.TranslationFinished -= OnTranslationFinished;
        _overlay?.Hide();
        RaiseStatus($"已停止，共跳过 {_skippedFrames} 帧无变化画面");
        RaiseRunning(false);
    }

    private void OnFrameCaptured(IReadOnlyList<Zone> zones)
    {
        // 转换在后台线程完成（Freeze 拷贝），UI 线程只接收 BitmapSource
        try
        {
            var preview = ImageConvert.ToBitmapSource(zones[0].Image);
            Raise(() => FramePreviewed?.Invoke(preview));
        }
        catch (Exception e)
        {
            Log.Warning("预览帧转换失败: {Error}", e.Message);
        }
    }

    private void OnFrameSkipped(int totalSkipped)
    {
        _skippedFrames = totalSkipped;
        Raise(RaiseStats);
    }

    private void OnOcrReady(int epoch, IReadOnlyList<OcrItem> items, double elapsedMs)
    {
        Raise(() =>
        {
            _currentEpoch = epoch;
            _currentItems = items.ToList();
            _lastElapsedMs = elapsedMs;
            ItemsUpdated?.Invoke(_currentItems);
            _overlay?.UpdateItems(_currentItems, _settings.Ocr.RoiZones);
            int pending = _currentItems.Count(i => string.IsNullOrEmpty(i.Translated));
            RaiseStatus(pending > 0
                ? $"识别到 {items.Count} 个文本块（{pending} 个待翻译），耗时 {elapsedMs:F0}ms"
                : $"识别到 {items.Count} 个文本块，耗时 {elapsedMs:F0}ms");
            RaiseStats();
        });
    }

    /// <summary>
    /// 翻译完成回填：过期 epoch 直接丢弃（画面已变）；否则按原文文本 merge 译文，
    /// 重复文本按出现顺序匹配（对应 Python 版 _on_translation_finished）。
    /// </summary>
    private void OnTranslationFinished(int epoch, IReadOnlyList<TranslationResult> results)
    {
        Raise(() =>
        {
            if (epoch < _currentEpoch || _currentItems.Count == 0) return;

            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (TranslationResult result in results)
            {
                counters.TryGetValue(result.Original, out int occurrence);
                counters[result.Original] = occurrence + 1;

                int seen = 0;
                for (int i = 0; i < _currentItems.Count; i++)
                {
                    if (_currentItems[i].Original != result.Original) continue;
                    if (seen == occurrence)
                    {
                        _currentItems[i] = _currentItems[i] with { Translated = result.Translated };
                        break;
                    }
                    seen++;
                }
            }
            ItemsUpdated?.Invoke(_currentItems);
            _overlay?.UpdateItems(_currentItems, _settings.Ocr.RoiZones);
            RaiseStats();
        });
    }

    private void OnWindowInvalid()
    {
        Raise(() =>
        {
            StopLoop();
            RaiseStatus("目标窗口已失效，捕获已停止");
        });
    }

    private void RaiseStats()
    {
        int pending = _currentItems.Count(i => string.IsNullOrEmpty(i.Translated));
        StatsUpdated?.Invoke((int)_lastElapsedMs, _currentItems.Count, pending, _skippedFrames);
    }

    private void RaiseStatus(string message) => Raise(() => StatusChanged?.Invoke(message));

    private void RaiseRunning(bool running) => Raise(() => RunningChanged?.Invoke(running));

    /// <summary>统一调度回 UI 线程；应用退出阶段 Dispatcher 已关闭时静默跳过。</summary>
    private void Raise(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(action);
    }
}
