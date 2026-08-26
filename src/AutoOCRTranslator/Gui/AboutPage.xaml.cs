using System.Windows.Controls;
using AutoOCRTranslator.Utils;

namespace AutoOCRTranslator.Gui;

/// <summary>关于页：应用信息、数据目录与操作提示（纯静态展示）。</summary>
public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        AppNameText.Text = "AutoOCRTranslator";
        VersionText.Text = $"v{version?.ToString(3) ?? "0.0.0"} · 自动 OCR 翻译工具";
        TechStackText.Text = "技术栈：WPF-UI 4 · RapidOcrNet · YamlDotNet · Serilog · SQLite（翻译缓存）";
        BaseDirText.Text = AppDirs.BaseDir;
    }
}
