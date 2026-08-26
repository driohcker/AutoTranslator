using System.IO;
using System.Threading;
using System.Windows;
using AutoOCRTranslator.Cache;
using AutoOCRTranslator.Gui;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using AutoOCRTranslator.Utils;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace AutoOCRTranslator;

/// <summary>
/// 应用入口：单实例保护、日志初始化、配置加载、托盘常驻与主窗口展示。
/// 关闭主窗口时最小化到托盘（点击托盘退出才真正结束进程）。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "AutoOCRTranslator_SingleInstance";

    private TaskbarIcon? _trayIcon;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未处理异常落日志（而不是静默吞掉），便于排查启动/运行期问题
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI 线程未处理异常: {Message}", args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception ?? new Exception("未知异常"), "AppDomain 未处理异常");

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("AutoOCRTranslator 已在运行中。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        string baseDir = AppDirs.BaseDir;
        LogSetup.Configure(Path.Combine(baseDir, "logs"));
        Log.Information("AutoOCRTranslator 启动，工作目录: {BaseDir}", baseDir);

        string configPath = Path.Combine(baseDir, "config", "settings.yaml");
        AppSettings settings = SettingsLoader.Load(configPath);
        Log.Information("配置加载完成: {ConfigPath}", configPath);

        // 翻译缓存（SQLite，WAL）与异步翻译管线：识别循环只投递纯文本，翻译不阻塞
        string dbPath = settings.Cache.DbPath;
        if (!Path.IsPathRooted(dbPath)) dbPath = Path.Combine(baseDir, dbPath);
        TranslationCache? cache = settings.Cache.Enabled ? new TranslationCache(dbPath) : null;
        cache?.CleanupExpired(settings.Cache.TtlDays);
        var pipeline = new TranslationPipeline(settings.Translate, cache);

        var mainWindow = new MainWindow(settings, configPath, pipeline);
        MainWindow = mainWindow;
        mainWindow.Show();
        Log.Information("主窗口已显示，开始初始化托盘");

        SetupTray(mainWindow);
        Log.Information("主窗口已显示，等待用户选择目标窗口");
    }

    /// <summary>托盘图标：双击显示/隐藏主窗口；菜单含显示与退出。</summary>
    private void SetupTray(MainWindow mainWindow)
    {
        mainWindow.Closing += (_, args) =>
        {
            if (_exiting) return;
            // 关闭按钮改为最小化到托盘
            args.Cancel = true;
            mainWindow.Hide();
            Log.Information("主窗口已最小化到托盘");
        };

        _trayIcon = new TaskbarIcon
        {
            Icon = TrayIcon.Create(),
            ToolTipText = "AutoOCRTranslator",
            ContextMenu = new System.Windows.Controls.ContextMenu(),
        };
        // 任务栏图标（运行时窗口任务栏按钮）：与托盘共用同一渲染位图
        mainWindow.Icon = TrayIcon.CreateImageSource();

        var showItem = new System.Windows.Controls.MenuItem { Header = "显示/隐藏主窗口" };
        showItem.Click += (_, _) => ToggleMainWindow(mainWindow);
        var quitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
        quitItem.Click += (_, _) =>
        {
            _exiting = true;
            mainWindow.Show();
            Shutdown();
        };
        _trayIcon.ContextMenu.Items.Add(showItem);
        _trayIcon.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        _trayIcon.ContextMenu.Items.Add(quitItem);
        _trayIcon.TrayMouseDoubleClick += (_, _) => ToggleMainWindow(mainWindow);
    }

    private static void ToggleMainWindow(Window mainWindow)
    {
        if (mainWindow.IsVisible)
        {
            mainWindow.Hide();
        }
        else
        {
            mainWindow.Show();
            mainWindow.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        Log.Information("AutoOCRTranslator 退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
