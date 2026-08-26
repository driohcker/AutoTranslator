using System.IO;
using Serilog;

namespace AutoOCRTranslator.Utils;

/// <summary>Serilog 日志初始化（文件滚动日志，保留 7 天）。</summary>
public static class LogSetup
{
    public static void Configure(string logDir)
    {
        // 控制台强制 UTF-8 输出（匹配 VS Code 终端）；无控制台时（双击运行）忽略
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台 */ }
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                encoding: System.Text.Encoding.UTF8)
            // 控制台输出：dotnet watch run / dotnet run 的终端里实时看日志
            .WriteTo.Console()
            // Debug 输出：F5 调试时显示在 VS Code 的调试控制台（Release 构建自动无输出）
            .WriteTo.Debug()
            .CreateLogger();
    }
}
