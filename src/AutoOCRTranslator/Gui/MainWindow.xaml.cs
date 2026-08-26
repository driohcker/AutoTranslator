using System.Windows;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigatedEventArgs = Wpf.Ui.Controls.NavigatedEventArgs;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 主窗口：FluentWindow + 侧边导航（启动 / 识别 / 设置 / 关于）+ 全局 Snackbar。
/// CaptureSession 统一持有识别循环与覆盖层，导航页仅订阅事件；设置保存后热重启识别。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly AppSettings _settings;
    private readonly string _configPath;
    private readonly TranslationPipeline _pipeline;
    private readonly CaptureSession _session;
    private readonly SnackbarService _snackbarService = new();
    private HomePage? _homePage;
    private bool _darkTheme = true;
    private bool _homeInjected;
    private bool _captureInjected;
    private bool _settingsInjected;
    private bool _cacheInjected;

    public MainWindow(AppSettings settings, string configPath, TranslationPipeline pipeline)
    {
        InitializeComponent();
        _settings = settings;
        _configPath = configPath;
        _pipeline = pipeline;
        _session = new CaptureSession(settings, configPath, pipeline);
        _snackbarService.SetSnackbarPresenter(AppSnackbar);
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"{settings.App.Name} v{version?.ToString(3) ?? settings.App.Version}";
        AppTitleBar.Title = Title;
        NavView.Navigated += OnNavigated;
        // 首屏显示启动页（导航页由 TargetPageType 延迟创建，Loaded 后再导航）
        Loaded += (_, _) =>
        {
            Serilog.Log.Information("主窗口 Loaded，开始导航启动页");
            NavView.Navigate(typeof(HomePage));
        };
    }

    /// <summary>当前选中的目标窗口句柄（设置页区域划分用）。</summary>
    public nint? TargetHwnd => _session.TargetHwnd;

    protected override void OnClosed(EventArgs e)
    {
        _session.Shutdown();
        _pipeline.Dispose();
        base.OnClosed(e);
    }

    /// <summary>导航页首次创建时注入依赖：启动/识别页注入会话，设置页注入配置与 Snackbar。</summary>
    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        switch (args.Page)
        {
            case HomePage homePage when !_homeInjected:
                _homeInjected = true;
                _homePage = homePage;
                homePage.Initialize(_session);
                break;
            case CapturePage capturePage when !_captureInjected:
                _captureInjected = true;
                capturePage.Initialize(_session);
                break;
            case SettingsPage settingsPage when !_settingsInjected:
                _settingsInjected = true;
                settingsPage.Saved += OnSettingsSaved;
                settingsPage.Initialize(_settings, _configPath, this, _snackbarService);
                break;
            case CachePage cachePage when !_cacheInjected:
                _cacheInjected = true;
                cachePage.Initialize(_pipeline, _settings, _configPath, _snackbarService);
                break;
        }
    }

    /// <summary>设置保存后：识别循环用新配置热重启，启动页配置摘要同步刷新。</summary>
    private void OnSettingsSaved()
    {
        _session.RestartIfRunning();
        _homePage?.RefreshConfigSummary();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _darkTheme = !_darkTheme;
        ApplicationThemeManager.Apply(_darkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);
        ThemeButton.Icon = new SymbolIcon
        {
            Symbol = _darkTheme ? SymbolRegular.WeatherMoon16 : SymbolRegular.WeatherSunny16,
            Width = 16,
            Height = 16,
        };
    }

    /// <summary>最小化到托盘（与关闭按钮行为一致，由 App.xaml.cs 的托盘逻辑接管）。</summary>
    private void TrayButton_Click(object sender, RoutedEventArgs e) => Hide();
}
