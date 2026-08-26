using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Controls;
using NumberBox = Wpf.Ui.Controls.NumberBox;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 设置页：预设驱动的卡片式设置。
/// 任何变更防抖 600ms 自动保存并热重启识别循环（Saved 事件由主窗口接管）；
/// 页面切走（Unloaded）时先把未落盘的变更落盘，避免丢输入。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private AppSettings? _settings;
    private string _configPath = "";
    private MainWindow? _owner;
    private ISnackbarService? _snackbar;
    private List<double[]> _zones = [];
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressSave; // LoadValues 期间拦截一切保存
    private bool _suppressModeCheck; // 回退模式单选时拦截确认弹窗，避免递归
    private bool _injected;

    public SettingsPage()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        Unloaded += (_, _) => FlushPendingSave();
        // 每次导航回本页时重新加载（首页可能已改截图模式等，需同步单选状态）
        IsVisibleChanged += (_, _) => { if (IsVisible && _injected) { LoadValues(); UpdateProviderFields(); } };
    }

    /// <summary>保存成功（主窗口据此用新配置重启识别循环）。</summary>
    public event Action? Saved;

    /// <summary>XAML 实例化后调用：注入配置、宿主窗口与 Snackbar 服务。</summary>
    public void Initialize(AppSettings settings, string configPath, MainWindow owner, ISnackbarService snackbar)
    {
        _settings = settings;
        _configPath = configPath;
        _owner = owner;
        _snackbar = snackbar;
        if (_injected) return;
        _injected = true;
        LoadValues();
        UpdateProviderFields();
    }

    /// <summary>把未落盘的变更立即保存（切页 / 退出前调用）。</summary>
    public void FlushPendingSave()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    // ---------- 加载 ----------

    private void LoadValues()
    {
        _suppressSave = true;
        try
        {
            ProviderCombo.SelectedValue = _settings!.Translate.Provider;

            string? dirKey = UiPresets.FindDirectionKey(_settings.Translate.SourceLang, _settings.Translate.TargetLang);
            DirectionCombo.SelectedValue = dirKey ?? "custom";
            bool customLoad = dirKey is null;
            CustomLangGrid.Visibility = customLoad ? Visibility.Visible : Visibility.Collapsed;
            PresetHint.Visibility = customLoad ? Visibility.Collapsed : Visibility.Visible;
            DirectionExpander.IsExpanded = customLoad;
            SelectLang(SourceLangCombo, _settings.Translate.SourceLang);
            SelectLang(TargetLangCombo, _settings.Translate.TargetLang);
            FilterSourceLangSwitch.IsChecked = _settings.Translate.FilterSourceLang;
            StrictSourceLangSwitch.IsChecked = _settings.Translate.StrictSourceLang;

            CheckRadioByTag(RoiPresetPanel, _settings.Ocr.RoiPreset, "subtitle");
            UpdateRoiVisibility();
            DropScoreSlider.Value = Math.Clamp(_settings.Ocr.DropScore, 0, 1);
            MaxWidthNumberBox.Value = _settings.Ocr.MaxWidth;
            DetLimitNumberBox.Value = _settings.Ocr.DetLimitSideLen;

            CheckRadioByTag(IntervalPanel, UiPresets.FindCaptureIntervalKey(_settings.Capture.IntervalMs), "custom");
            UpdateIntervalVisibility();
            IntervalNumberBox.Value = _settings.Capture.IntervalMs;
            ChangeDetectionSwitch.IsChecked = _settings.Capture.ChangeDetection;

            CheckRadioByTag(CaptureModePanel, _settings.Capture.Mode.ToString(), "TargetWindow");

            CheckRadioByTag(OverlayStylePanel, UiPresets.FindOverlayStyleKey(_settings.Overlay.FontSize, _settings.Overlay.FontColor), "custom");
            CheckRadioByTag(OverlayBgPanel, UiPresets.FindOverlayBackgroundKey(_settings.Overlay.BgColor), "bg_custom");
            FontFamilyBox.Text = _settings.Overlay.FontFamily;
            FontSizeNumberBox.Value = _settings.Overlay.FontSize;
            FontColorBox.Text = _settings.Overlay.FontColor;
            BgColorBox.Text = _settings.Overlay.BgColor;
            OverlayMaxWidthNumberBox.Value = _settings.Overlay.MaxWidth;

            if (_settings.Ocr.RoiCustom.Length == 4)
            {
                RoiXBox.Value = _settings.Ocr.RoiCustom[0];
                RoiYBox.Value = _settings.Ocr.RoiCustom[1];
                RoiWBox.Value = _settings.Ocr.RoiCustom[2];
                RoiHBox.Value = _settings.Ocr.RoiCustom[3];
            }

            _zones = _settings.Ocr.RoiZones.Select(z => z.ToArray()).ToList();
            UpdateZonesLabel();
        }
        finally
        {
            _suppressSave = false;
        }
    }

    private static void SelectLang(ComboBox combo, string code)
    {
        foreach (var item in combo.Items)
        {
            if (item is UiPresets.LangOption o && string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>按 Tag 选中预设单选卡片；tag 为空或找不到时选中 fallbackTag。</summary>
    private static void CheckRadioByTag(Panel panel, string? tag, string fallbackTag)
    {
        string target = string.IsNullOrEmpty(tag) ? fallbackTag : tag;
        foreach (RadioButton rb in RadiosIn(panel))
            rb.IsChecked = rb.Tag as string == target;
    }

    private static IEnumerable<RadioButton> RadiosIn(Panel panel) => panel.Children.OfType<RadioButton>();

    // ---------- 控件事件 ----------

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProviderFields();
        MarkDirty();
    }

    private void DirectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = DirectionCombo.SelectedValue as string == "custom";
        CustomLangGrid.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        DirectionExpander.IsExpanded = custom;
        if (!custom && DirectionCombo.SelectedValue is string key)
        {
            var dir = UiPresets.FindDirection(key);
            if (dir is not null)
            {
                SelectLang(SourceLangCombo, dir.SourceLang);
                SelectLang(TargetLangCombo, dir.TargetLang);
            }
        }
        MarkDirty();
    }

    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => MarkDirty();

    private void Toggle_Changed(object sender, RoutedEventArgs e) => MarkDirty();

    private void RoiPresetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        UpdateRoiVisibility();
        MarkDirty();
    }

    private void IntervalRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true }) return;
        UpdateIntervalVisibility();
        MarkDirty();
    }

    private void CaptureModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        if (_suppressSave || _suppressModeCheck) return;
        var newMode = rb.Tag as string == "FullScreen" ? CaptureMode.FullScreen : CaptureMode.TargetWindow;
        var curMode = _settings!.Capture.Mode;
        if (newMode == curMode) return;

        // 切换模式会清空已划分的区域（两模式 target rect 不同，旧 zone 不再适用）
        var result = System.Windows.MessageBox.Show(
            "切换截图模式会清空已划分的区域，是否继续？", "切换模式",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _zones.Clear();
            UpdateZonesLabel();
            MarkDirty();
        }
        else
        {
            // 回退到当前模式的选择；期间拦截确认弹窗避免递归
            _suppressModeCheck = true;
            try { CheckRadioByTag(CaptureModePanel, curMode.ToString(), "TargetWindow"); }
            finally { _suppressModeCheck = false; }
        }
    }

    private void OverlayStyleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        string key = rb.Tag as string ?? "custom";
        var style = UiPresets.OverlayStyles.FirstOrDefault(s => s.Key == key);
        if (style is { IsCustom: false })
        {
            FontSizeNumberBox.Value = style.FontSize;
            FontColorBox.Text = style.FontColor;
        }
        MarkDirty();
    }

    private void OverlayBgRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        string key = rb.Tag as string ?? "bg_custom";
        var bg = UiPresets.OverlayBackgrounds.FirstOrDefault(b => b.Key == key);
        if (bg.BgColor.Length > 0) BgColorBox.Text = bg.BgColor;
        MarkDirty();
    }

    private void DropScoreSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        DropScoreValue.Text = $"{Math.Round(DropScoreSlider.Value * 100)}%";
        MarkDirty();
    }

    private void AdvancedNumber_Changed(object sender, RoutedEventArgs e) => MarkDirty();

    private void AdvancedText_Changed(object sender, TextChangedEventArgs e) => MarkDirty();

    private void Credential_Changed(object sender, RoutedEventArgs e) => MarkDirty();

    private void TestButton_Click(object sender, RoutedEventArgs e) => TestTranslation();

    private void ZonesButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = _settings!.Capture.Mode;
        nint hwnd = _owner?.TargetHwnd ?? nint.Zero;
        // 目标窗口模式必须有窗口；全屏模式 target rect = 整个虚拟屏，无需窗口
        if (mode == CaptureMode.TargetWindow && hwnd == nint.Zero)
        {
            System.Windows.MessageBox.Show("请先在启动页选择目标窗口，再进行区域划分。", "提示",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        Window? host = Window.GetWindow(this);
        try
        {
            // 先构造（此时窗口可见，GetWindowRect 有效；最小化后目标窗口矩形会失效），再最小化并显示全屏蒙版
            var selector = new ZoneSelector(_zones, hwnd, mode) { Owner = host };
            if (host is not null) host.WindowState = WindowState.Minimized;
            // 全屏蒙版划分：在透明蒙版上框选（无需截图，底层画面动态可见），完成后记录相对 target rect 的区域
            _zones = selector.Select() ?? _zones;
            UpdateZonesLabel();
            MarkDirty();
        }
        catch (InvalidOperationException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "提示",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            if (host is not null) host.WindowState = WindowState.Normal;
        }
    }

    // ---------- 自动保存 ----------

    /// <summary>标记变更：600ms 防抖后统一保存（LoadValues 期间由 _suppressSave 拦截）。</summary>
    private void MarkDirty()
    {
        if (_suppressSave || _settings is null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        if (_settings is null) return;
        try
        {
            ApplyToSettings(BuildSettings());
            SettingsLoader.Save(_configPath, _settings);
            Log.Information("设置已保存: {ConfigPath}", _configPath);
            if (IsLoaded) ShowSnackbar("设置已自动保存", "更改已写入配置并生效", ControlAppearance.Success);
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置失败");
            if (IsLoaded) ShowSnackbar("保存失败", ex.Message, ControlAppearance.Danger);
        }
    }

    private void ShowSnackbar(string title, string message, ControlAppearance appearance)
        => _snackbar?.Show(title, message, appearance, null, TimeSpan.FromSeconds(1.5));

    /// <summary>测试当前翻译配置。</summary>
    private void TestTranslation()
    {
        if (_settings is null) return;
        try
        {
            var snapshot = BuildSettings();
            ITranslator translator = TranslatorFactory.Create(snapshot.Translate);
            string result = translator.Translate("hello", snapshot.Translate.SourceLang, snapshot.Translate.TargetLang);
            if (!string.IsNullOrEmpty(result))
                _snackbar?.Show("测试翻译成功", $"hello → {result}", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            else
                _snackbar?.Show("测试翻译失败", "翻译结果为空，请检查配置。", ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _snackbar?.Show("测试翻译失败", ex.Message, ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
    }

    // ---------- 配置组装 ----------

    /// <summary>把界面当前值组装成临时 AppSettings 快照（含类型解析）。</summary>
    private AppSettings BuildSettings()
    {
        var s = new AppSettings
        {
            Translate = new TranslateSection
            {
                Provider = ProviderCombo.SelectedValue as string ?? "google_free",
                SourceLang = SelectedLangCode(SourceLangCombo, "ja"),
                TargetLang = SelectedLangCode(TargetLangCombo, "zh-CN"),
                ApiKey = ApiKeyBox.Password,
                ApiSecret = ApiSecretBox.Password,
                Proxy = ProxyBox.Text.Trim(),
                FilterSourceLang = FilterSourceLangSwitch.IsChecked == true,
                StrictSourceLang = StrictSourceLangSwitch.IsChecked == true,
                Timeout = _settings!.Translate.Timeout,
                MaxRetries = _settings.Translate.MaxRetries,
                Concurrency = _settings.Translate.Concurrency,
            },
            Ocr = new OcrSection
            {
                Engine = _settings.Ocr.Engine,
                Lang = _settings.Ocr.Lang,
                UseGpu = _settings.Ocr.UseGpu,
                DropScore = DropScoreSlider.Value,
                MaxWidth = NumberInt(MaxWidthNumberBox, _settings.Ocr.MaxWidth),
                DetLimitSideLen = NumberInt(DetLimitNumberBox, _settings.Ocr.DetLimitSideLen),
                RoiPreset = SelectedRoiPreset(),
                RoiCustom = ParseRoiCustom(),
                RoiZones = _zones.Select(z => z.ToList()).ToList(),
            },
            Capture = new CaptureSection
            {
                Mode = SelectedCaptureMode(),
                IntervalMs = SelectedIntervalMs(),
                ChangeDetection = ChangeDetectionSwitch.IsChecked == true,
                ChangeThreshold = _settings.Capture.ChangeThreshold,
                TargetWindowTitle = _settings.Capture.TargetWindowTitle,
            },
            Overlay = new OverlaySection
            {
                FontFamily = FontFamilyBox.Text.Trim(),
                FontSize = NumberInt(FontSizeNumberBox, _settings.Overlay.FontSize),
                FontColor = FontColorBox.Text.Trim(),
                BgColor = BgColorBox.Text.Trim(),
                BorderColor = _settings.Overlay.BorderColor,
                MaxWidth = NumberInt(OverlayMaxWidthNumberBox, _settings.Overlay.MaxWidth),
            },
            Cache = _settings.Cache,
            LogOverlay = _settings.LogOverlay,
            App = _settings.App,
        };
        return s;
    }

    private void ApplyToSettings(AppSettings s)
    {
        _settings!.Translate = s.Translate;
        _settings.Ocr = s.Ocr;
        _settings.Capture = s.Capture;
        _settings.Overlay = s.Overlay;
    }

    private static string SelectedLangCode(ComboBox combo, string fallback) =>
        combo.SelectedItem is UiPresets.LangOption o ? o.Code : fallback;

    private string SelectedRoiPreset()
    {
        foreach (RadioButton rb in RadiosIn(RoiPresetPanel))
            if (rb.IsChecked == true) return rb.Tag as string ?? "subtitle";
        return "subtitle";
    }

    private int SelectedIntervalMs()
    {
        foreach (RadioButton rb in RadiosIn(IntervalPanel))
        {
            if (rb.IsChecked != true) continue;
            string key = rb.Tag as string ?? "normal";
            if (key == "custom") return Math.Max(1, NumberInt(IntervalNumberBox, 300));
            var preset = UiPresets.CaptureIntervals.FirstOrDefault(i => i.Key == key);
            if (preset is not null) return preset.Ms;
        }
        return 300;
    }

    private CaptureMode SelectedCaptureMode()
    {
        foreach (RadioButton rb in RadiosIn(CaptureModePanel))
            if (rb.IsChecked == true) return rb.Tag as string == "FullScreen" ? CaptureMode.FullScreen : CaptureMode.TargetWindow;
        return CaptureMode.TargetWindow;
    }

    private static int NumberInt(NumberBox box, int fallback)
    {
        double? v = box.Value;
        return v is null ? fallback : (int)Math.Round(v.Value);
    }

    private double[] ParseRoiCustom()
    {
        double x = RoiXBox.Value ?? 0.1, y = RoiYBox.Value ?? 0.75;
        double w = RoiWBox.Value ?? 0.8, h = RoiHBox.Value ?? 0.2;
        return [Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Math.Clamp(w, 0, 1), Math.Clamp(h, 0, 1)];
    }

    // ---------- 显隐联动 ----------

    /// <summary>根据所选提供者显示/隐藏凭证与代理输入框。</summary>
    private void UpdateProviderFields()
    {
        string provider = ProviderCombo.SelectedValue as string ?? "google_free";
        bool google = provider == "google_free";
        bool deepL = provider == "deep_l";
        bool tencent = provider == "tencent";

        ApiKeyLabel.Text = tencent ? "SecretId" : deepL ? "Auth Key" : "AccessKey ID";
        ApiKeyLabel.Visibility = google ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyBox.Visibility = google ? Visibility.Collapsed : Visibility.Visible;

        ApiSecretLabel.Visibility = tencent ? Visibility.Visible : Visibility.Collapsed;
        ApiSecretBox.Visibility = tencent ? Visibility.Visible : Visibility.Collapsed;

        ProxyLabel.Visibility = google || deepL ? Visibility.Visible : Visibility.Collapsed;
        ProxyBox.Visibility = google || deepL ? Visibility.Visible : Visibility.Collapsed;

        // 副标题提示该服务需要哪些凭据，引导用户点开填写
        ProviderSubtitle.Text = provider switch
        {
            "google_free" => "Google 免费网页 · 无需密钥（可选填代理）",
            "deep_l" => "DeepL API · 需 Auth Key（可选代理）",
            "tencent" => "腾讯云 · 需 SecretId / SecretKey",
            "aliyun" => "阿里云 · 需 AccessKey ID",
            _ => "选择翻译提供者；点开填写所需凭据",
        };
    }

    /// <summary>自定义矩形参数仅在 custom 预设下显示；区域划分仅在 custom_zones 下显示。</summary>
    private void UpdateRoiVisibility()
    {
        string preset = SelectedRoiPreset();
        ZonesRow.Visibility = preset == "custom_zones" ? Visibility.Visible : Visibility.Collapsed;
        CustomRoiExpander.Visibility = preset == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateIntervalVisibility()
    {
        bool custom = RadiosIn(IntervalPanel).Any(r => r.Tag as string == "custom" && r.IsChecked == true);
        CustomIntervalExpander.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateZonesLabel() => ZonesLabel.Text = $"已划分 {_zones.Count} 个区域";
}
