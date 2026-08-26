using System.IO;
using AutoOCRTranslator.Cache;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AutoOCRTranslator.Tests.Cache;

/// <summary>SQLite 翻译缓存：写入/命中/语言键隔离/清理/统计。</summary>
public class TranslationCacheTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"cache_test_{Guid.NewGuid():N}.db");
    private readonly TranslationCache _cache;

    public TranslationCacheTests()
    {
        _cache = new TranslationCache(_dbPath);
    }

    [Fact]
    public void SetThenGet_ReturnsTranslation()
    {
        _cache.Set("こんにちは", "ja", "zh-CN", "你好");
        Assert.Equal("你好", _cache.Get("こんにちは", "ja", "zh-CN"));
    }

    [Fact]
    public void Get_Miss_ReturnsNull()
    {
        Assert.Null(_cache.Get("不存在", "ja", "zh-CN"));
    }

    [Fact]
    public void LanguagePair_IsIsolated()
    {
        _cache.Set("hello", "en", "zh-CN", "你好");
        _cache.Set("hello", "en", "ja", "こんにちは");

        Assert.Equal("你好", _cache.Get("hello", "en", "zh-CN"));
        Assert.Equal("こんにちは", _cache.Get("hello", "en", "ja"));
    }

    [Fact]
    public void SetTwice_UpdatesTranslation_AndCountsHit()
    {
        _cache.Set("test", "en", "zh-CN", "v1");
        _cache.Set("test", "en", "zh-CN", "v2");

        Assert.Equal("v2", _cache.Get("test", "en", "zh-CN"));
        (int count, int hits) = _cache.Stats();
        Assert.Equal(1, count);
        Assert.Equal(2, hits); // Set 的 ON CONFLICT 分支累加 1 + Get 命中累加 1
    }

    [Fact]
    public void Get_Hit_IncrementsHitCount()
    {
        _cache.Set("test", "en", "zh-CN", "你好");
        _cache.Get("test", "en", "zh-CN");
        _cache.Get("test", "en", "zh-CN");

        (_, int hits) = _cache.Stats();
        Assert.Equal(2, hits);
    }

    [Fact]
    public void CleanupExpired_RemovesOldEntries()
    {
        _cache.Set("old", "en", "zh-CN", "旧");
        _cache.Set("new", "en", "zh-CN", "新");

        // 把 old 条目的访问时间改到 31 天前
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE translations SET last_accessed = datetime('now','-31 days') WHERE source_text = 'old'";
            cmd.ExecuteNonQuery();
        }

        int deleted = _cache.CleanupExpired(30);
        Assert.Equal(1, deleted);
        Assert.Null(_cache.Get("old", "en", "zh-CN"));
        Assert.NotNull(_cache.Get("new", "en", "zh-CN"));
    }

    [Fact]
    public void CleanupExpired_ZeroOrNegativeTtl_Noop()
    {
        _cache.Set("test", "en", "zh-CN", "你好");
        Assert.Equal(0, _cache.CleanupExpired(0));
        Assert.Equal(0, _cache.CleanupExpired(-1));
        Assert.NotNull(_cache.Get("test", "en", "zh-CN"));
    }

    [Fact]
    public void Clear_EmptiesCache()
    {
        _cache.Set("a", "en", "zh-CN", "甲");
        _cache.Set("b", "en", "zh-CN", "乙");
        _cache.Clear();

        Assert.Equal(0, _cache.Stats().Count);
        Assert.Null(_cache.Get("a", "en", "zh-CN"));
    }

    [Fact]
    public async Task ConcurrentAccess_IsSafe()
    {
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 20; j++)
            {
                string key = $"key{i}-{j}";
                _cache.Set(key, "en", "zh-CN", $"t{i}-{j}");
                _cache.Get(key, "en", "zh-CN");
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(8 * 20, _cache.Stats().Count);
    }

    public void Dispose()
    {
        _cache.Clear();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        File.Delete(_dbPath + "-wal");
        File.Delete(_dbPath + "-shm");
    }
}
