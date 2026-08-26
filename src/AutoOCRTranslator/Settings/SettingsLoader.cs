using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AutoOCRTranslator.Settings;

/// <summary>加载/保存 config/settings.yaml（Python 版同款格式，字段名按下划线映射）。</summary>
public static class SettingsLoader
{
    /// <summary>读取配置文件；文件缺失时写出默认配置并返回默认值。</summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            Save(path, defaults);
            return defaults;
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<AppSettings>(File.ReadAllText(path));
    }

    /// <summary>保存配置（完整覆盖写回）。</summary>
    public static void Save(string path, AppSettings settings)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, serializer.Serialize(settings));
    }
}
