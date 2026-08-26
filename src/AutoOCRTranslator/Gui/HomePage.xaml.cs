using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Utils;
using Wpf.Ui.Controls;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 启动页：截图模式切换 + 目标窗口选择 + 开始/停止识别 + 运行状态与统计（操作台）。
/// 识别循环由 CaptureSession 统一持有，本页仅订阅状态事件。
/// </summary>
public partial class HomePage : Page
{
    private CaptureSession? _session;
    private bool _suppressModeCheck; // 回退模式单选时拦截确认弹窗，避免递归

    public HomePage()
    {
        InitializeComponent();
        // 每次导航回本页时同步最新模式与配置摘要（设置页可能已修改）
        IsVisibleChanged += (_, _) => { if (IsVisible) { SyncModeUi(); RefreshConfigSummary(); } };
    }

    /// <summary>由主窗口注入会话；订阅状态 / 运行 / 统计事件。</summary>
    public void Initialize(CaptureSession session)
    {
        _session = session;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"v{version?.ToString(3) ?? session.Settings.App.Version}";
        AppNameText.Text = session.Settings.App.Name;
        session.StatusChanged += s => StatusLine.Text = s;
        session.RunningChanged += SetRunningState;
        session.StatsUpdated += OnStatsUpdated;
        // 初始状态：RunningChanged 只在变化时触发，启动时需手动设置一次按钮文案
        SetRunningState(session.IsRunning);
        RefreshWindowList();
        SyncModeUi();
        RefreshConfigSummary();
    }

    /// <summary>设置保存后刷新配置摘要（主窗口热重启回调时调用）。</summary>
    public void RefreshConfigSummary()
    {
        if (_session is null) return;
        var s = _session.Settings;
        ModeValue.Text = s.Capture.Mode == CaptureMode.FullScreen ? "全屏" : "目标窗口";
        RoiValue.Text = UiPresets.RoiPresetDisplay(s.Ocr.RoiPreset);
        string? dirKey = UiPresets.FindDirectionKey(s.Translate.SourceLang, s.Translate.TargetLang);
        DirectionValue.Text = dirKey is not null
            ? UiPresets.FindDirection(dirKey)!.Display
            : $"{UiPresets.LanguageDisplay(s.Translate.SourceLang)} → {UiPresets.LanguageDisplay(s.Translate.TargetLang)}";
        IntervalValue.Text = $"{s.Capture.IntervalMs}ms";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void RefreshWindowList()
    {
        if (_session is null) return;
        WindowInfo? selected = WindowCombo.SelectedItem as WindowInfo;
        var windows = _session.RefreshWindows();
        WindowCombo.ItemsSource = windows;
        // 按句柄恢复选中（窗口可能已被关闭或重命名）
        if (selected is not null)
            WindowCombo.SelectedItem = windows.FirstOrDefault(w => w.Hwnd == selected.Hwnd);
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowCombo.SelectedItem is WindowInfo info)
        {
            _session?.SelectWindow(info);
        }
        RefreshStartEnabled();
    }

    /// <summary>单一启停按钮：运行中点击即停止，否则启动。</summary>
    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.IsRunning == true) _session.Stop();
        else _session?.Start();
    }

    private void SetRunningState(bool running)
    {
        StartButton.Icon = new SymbolIcon
        {
            Symbol = running ? SymbolRegular.Stop24 : SymbolRegular.Play24,
            Width = 18,
            Height = 18,
        };
        StartButton.Content = running ? "停止识别" : "开始识别";
        StartButton.Appearance = running ? ControlAppearance.Danger : ControlAppearance.Primary;
        WindowCombo.IsEnabled = !running;
        RefreshButton.IsEnabled = !running;
        RefreshStartEnabled();
    }

    // ---------- 截图模式 ----------

    private bool IsFullScreenMode() => _session?.Settings.Capture.Mode == CaptureMode.FullScreen;

    /// <summary>按当前模式选中单选并刷新窗口选择区显隐。</summary>
    private void SyncModeUi()
    {
        if (_session is null) return;
        CheckModeRadio(_session.Settings.Capture.Mode.ToString());
        UpdateModeDependentUi();
    }

    /// <summary>选中指定 tag 的模式单选；_suppressModeCheck 期间拦截确认弹窗避免递归。</summary>
    private void CheckModeRadio(string tag)
    {
        _suppressModeCheck = true;
        try
        {
            foreach (RadioButton rb in CaptureModePanel.Children.OfType<RadioButton>())
                rb.IsChecked = rb.Tag as string == tag;
        }
        finally { _suppressModeCheck = false; }
    }

    /// <summary>全屏模式隐藏目标窗口选择控件（无需选窗），目标窗口模式恢复。</summary>
    private void UpdateModeDependentUi()
    {
        bool fs = IsFullScreenMode();
        WindowCombo.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = fs ? Visibility.Collapsed : Visibility.Visible;
        TargetWindowSubtitle.Text = fs ? "全屏模式：监测整个屏幕，无需选择窗口" : "选择要识别并翻译的游戏或应用窗口";
        RefreshStartEnabled();
    }

    /// <summary>开始按钮可用性：运行中恒可用；否则全屏模式可用，目标窗口模式依赖已选窗口。</summary>
    private void RefreshStartEnabled()
    {
        if (_session?.IsRunning == true) { StartButton.IsEnabled = true; return; }
        StartButton.IsEnabled = IsFullScreenMode() || WindowCombo.SelectedItem is not null;
    }

    private void CaptureModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        if (_suppressModeCheck || _session is null) return;
        var newMode = rb.Tag as string == "FullScreen" ? CaptureMode.FullScreen : CaptureMode.TargetWindow;
        if (newMode == _session.Settings.Capture.Mode) return;

        // 切换模式会清空已划分的区域（两模式 target rect 不同，旧 zone 不再适用）
        var result = System.Windows.MessageBox.Show(
            "切换截图模式会清空已划分的区域，是否继续？", "切换模式",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
        {
            _session.SetCaptureMode(newMode);
            UpdateModeDependentUi();
            RefreshConfigSummary();
        }
        else
        {
            CheckModeRadio(_session.Settings.Capture.Mode.ToString());
        }
    }

    private void OnStatsUpdated(int elapsedMs, int blocks, int pending, int skipped)
    {
        ElapsedValue.Text = elapsedMs.ToString();
        BlocksValue.Text = blocks.ToString();
        PendingValue.Text = pending.ToString();
        SkippedValue.Text = skipped.ToString();
    }
}
