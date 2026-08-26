using System.IO;

namespace AutoOCRTranslator.Utils;

/// <summary>
/// 应用基础目录：单文件发布（PublishSingleFile）下 AppContext.BaseDirectory
/// 可能指向临时解压目录，这里固定取 exe 所在目录 —— config/models 始终在 exe 旁边。
/// </summary>
public static class AppDirs
{
    public static string BaseDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
