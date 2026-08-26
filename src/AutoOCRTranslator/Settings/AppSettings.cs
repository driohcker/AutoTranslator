namespace AutoOCRTranslator.Settings;

/// <summary>
/// 应用配置模型，对应 config/settings.yaml（Python 版同款格式，字段名按下划线映射）。
/// 属性默认值与 Python 版 config/settings.yaml 保持一致。
/// </summary>
public sealed class AppSettings
{
    public AppSection App { get; set; } = new();
    public CaptureSection Capture { get; set; } = new();
    public OcrSection Ocr { get; set; } = new();
    public TranslateSection Translate { get; set; } = new();
    public CacheSection Cache { get; set; } = new();
    public OverlaySection Overlay { get; set; } = new();
    public LogOverlaySection LogOverlay { get; set; } = new();
}

public sealed class AppSection
{
    public string Name { get; set; } = "AutoOCRTranslator";
    public string Version { get; set; } = "1.0.0";
}

/// <summary>
/// 截图模式：目标窗口模式（target rect = 目标窗口矩形，随窗口移动）
/// 或全屏模式（target rect = 虚拟屏幕矩形，固定）。两种模式 zone 存储格式
/// 一致（相对 target rect 的归一化 [0,1]），切换模式会清空已划分区域。
/// </summary>
public enum CaptureMode
{
    TargetWindow,
    FullScreen,
}

public sealed class CaptureSection
{
    public CaptureMode Mode { get; set; } = CaptureMode.TargetWindow;
    public int IntervalMs { get; set; } = 300;
    public string TargetWindowTitle { get; set; } = "";
    public bool ChangeDetection { get; set; } = true;
    public int ChangeThreshold { get; set; } = 4;
}

public sealed class OcrSection
{
    public string Engine { get; set; } = "rapid";
    public string Lang { get; set; } = "japan";
    public bool UseGpu { get; set; } = false;
    public double DetDbThresh { get; set; } = 0.5;
    public double DetDbBoxThresh { get; set; } = 0.5;
    public double DropScore { get; set; } = 0.3;
    public int MaxWidth { get; set; } = 640;
    public int DetLimitSideLen { get; set; } = 640;
    public string RoiPreset { get; set; } = "custom_zones";
    public double[] RoiCustom { get; set; } = [0.1, 0.75, 0.8, 0.2];
    public List<List<double>> RoiZones { get; set; } = [];
}

public sealed class TranslateSection
{
    public string Provider { get; set; } = "google_free";
    public string SourceLang { get; set; } = "ja";
    public string TargetLang { get; set; } = "zh-CN";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string Proxy { get; set; } = "";
    public int Timeout { get; set; } = 5;
    public int MaxRetries { get; set; } = 1;
    public int Concurrency { get; set; } = 2;
    public bool FilterSourceLang { get; set; } = true;
    public bool StrictSourceLang { get; set; } = true;
}

public sealed class CacheSection
{
    public bool Enabled { get; set; } = true;
    public string DbPath { get; set; } = "data/cache/translations.db";
    public int TtlDays { get; set; } = 30;
    public string ActiveProfile { get; set; } = "default";
}

public sealed class OverlaySection
{
    public string FontFamily { get; set; } = "Microsoft YaHei";
    public int FontSize { get; set; } = 18;
    public string FontColor { get; set; } = "#FFFFFF";
    public string BgColor { get; set; } = "#80000000";
    public string BorderColor { get; set; } = "#FF000000";
    public int MaxWidth { get; set; } = 400;
}

public sealed class LogOverlaySection
{
    public bool Enabled { get; set; } = true;
    public int X { get; set; } = 1000;
    public int Y { get; set; } = 100;
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 320;
    public double Opacity { get; set; } = 0.85;
    public int FontSize { get; set; } = 12;
}
