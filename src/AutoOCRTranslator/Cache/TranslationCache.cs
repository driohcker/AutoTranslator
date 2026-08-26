using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AutoOCRTranslator.Cache;

/// <summary>
/// 翻译结果持久化缓存（对应 Python 版 translation_cache）。
/// SQLite + WAL 模式，避免重复调用翻译 API。
/// 每个操作使用独立连接，支持多线程并发读写。
/// </summary>
public sealed class TranslationCache : IDisposable
{
    private readonly string _dbPath;
    private bool _disposed;

    /// <summary>当前活动方案 ID；Get/Set/Stats/ListEntries/Delete/Export/Import 都按此过滤。</summary>
    public string ActiveProfile { get; private set; } = "default";

    public TranslationCache(string dbPath)
    {
        _dbPath = dbPath;
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        InitDb();
    }

    /// <summary>释放连接池（文件本身保留，缓存持久化）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void InitDb()
    {
        using SqliteConnection conn = Open();
        // 方案元数据表
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS profiles (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP
                )
                """;
            cmd.ExecuteNonQuery();
        }
        // 保证 default 方案行始终存在
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO profiles (id, name) VALUES ('default', '默认方案')";
            cmd.ExecuteNonQuery();
        }

        bool tableExists = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='translations'";
            using var r = cmd.ExecuteReader();
            if (r.Read()) tableExists = true;
        }

        if (!tableExists)
        {
            // 全新库：直接建含 profile_id 的最新 schema
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE translations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_text TEXT NOT NULL,
                    source_lang TEXT NOT NULL,
                    target_lang TEXT NOT NULL,
                    translation TEXT NOT NULL,
                    profile_id TEXT NOT NULL DEFAULT 'default',
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    last_accessed TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    hit_count INTEGER DEFAULT 0,
                    UNIQUE(source_text, source_lang, target_lang, profile_id)
                )
                """;
            cmd.ExecuteNonQuery();
        }
        else
        {
            // 旧库迁移：检查是否已有 profile_id 列；无则重建表以把 UNIQUE 约束扩展到含 profile_id
            bool hasProfileId = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(translations)";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetString(1) == "profile_id") { hasProfileId = true; break; }
                }
            }
            if (!hasProfileId)
            {
                using var tx = conn.BeginTransaction();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        CREATE TABLE translations_new (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            source_text TEXT NOT NULL,
                            source_lang TEXT NOT NULL,
                            target_lang TEXT NOT NULL,
                            translation TEXT NOT NULL,
                            profile_id TEXT NOT NULL DEFAULT 'default',
                            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                            last_accessed TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                            hit_count INTEGER DEFAULT 0,
                            UNIQUE(source_text, source_lang, target_lang, profile_id)
                        )
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO translations_new (id, source_text, source_lang, target_lang, translation, profile_id, created_at, last_accessed, hit_count)
                        SELECT id, source_text, source_lang, target_lang, translation, 'default', created_at, last_accessed, hit_count
                        FROM translations
                        """;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DROP TABLE translations";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "ALTER TABLE translations_new RENAME TO translations";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                Log.Information("翻译缓存表已迁移：新增 profile_id 列");
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_translation_lookup
                ON translations(source_text, source_lang, target_lang, profile_id)
                """;
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- 方案管理 ----------

    /// <summary>列出所有方案；default 排在最前，其余按创建时间升序。</summary>
    public List<ProfileInfo> ListProfiles()
    {
        using SqliteConnection conn = Open();
        var list = new List<ProfileInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, COALESCE(created_at, '') FROM profiles ORDER BY (id = 'default') DESC, created_at ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ProfileInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        return list;
    }

    /// <summary>新建方案，返回新方案信息。</summary>
    public ProfileInfo AddProfile(string name)
    {
        string id = Guid.NewGuid().ToString("N");
        string trimmed = string.IsNullOrWhiteSpace(name) ? "未命名方案" : name.Trim();
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO profiles (id, name) VALUES ($id, $name)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", trimmed);
        cmd.ExecuteNonQuery();
        Log.Information("新建翻译缓存方案：{Id} / {Name}", id, trimmed);
        return new ProfileInfo(id, trimmed, "");
    }

    /// <summary>重命名方案；返回是否成功。</summary>
    public bool RenameProfile(string id, string newName)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(newName)) return false;
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE profiles SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", newName.Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>删除方案及其全部条目；禁止删除 default 与当前活动方案。返回是否成功。</summary>
    public bool DeleteProfile(string id)
    {
        if (string.IsNullOrEmpty(id) || id == "default" || id == ActiveProfile) return false;
        using SqliteConnection conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM translations WHERE profile_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        int rows;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM profiles WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            rows = cmd.ExecuteNonQuery();
        }
        tx.Commit();
        if (rows > 0) Log.Information("删除翻译缓存方案：{Id}", id);
        return rows > 0;
    }

    /// <summary>切换当前活动方案；id 不存在则返回 false。</summary>
    public bool SetActive(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return false;
        ActiveProfile = id;
        return true;
    }

    /// <summary>查询缓存，未命中返回 null；命中时累加 hit_count 并刷新访问时间。</summary>
    public string? Get(string text, string sourceLang, string targetLang)
    {
        using SqliteConnection conn = Open();
        long id;
        string translation;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, translation FROM translations
                WHERE source_text = $text AND source_lang = $sl AND target_lang = $tl AND profile_id = $p
                """;
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$sl", sourceLang);
            cmd.Parameters.AddWithValue("$tl", targetLang);
            cmd.Parameters.AddWithValue("$p", ActiveProfile);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            id = reader.GetInt64(0);
            translation = reader.GetString(1);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE translations
                SET hit_count = hit_count + 1, last_accessed = CURRENT_TIMESTAMP
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        return translation;
    }

    /// <summary>写入缓存；记录已存在时更新译文并累加命中次数。</summary>
    public void Set(string text, string sourceLang, string targetLang, string translation)
    {
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO translations (source_text, source_lang, target_lang, translation, profile_id)
            VALUES ($text, $sl, $tl, $translation, $p)
            ON CONFLICT(source_text, source_lang, target_lang, profile_id)
            DO UPDATE SET
                translation = excluded.translation,
                last_accessed = CURRENT_TIMESTAMP,
                hit_count = hit_count + 1
            """;
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$sl", sourceLang);
        cmd.Parameters.AddWithValue("$tl", targetLang);
        cmd.Parameters.AddWithValue("$translation", translation);
        cmd.Parameters.AddWithValue("$p", ActiveProfile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>清空当前活动方案的全部缓存条目。</summary>
    public void Clear()
    {
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations WHERE profile_id = $p";
        cmd.Parameters.AddWithValue("$p", ActiveProfile);
        cmd.ExecuteNonQuery();
        Log.Information("翻译缓存已清空（方案 {Profile}）", ActiveProfile);
    }

    /// <summary>清理过期缓存，返回清理的记录数；ttlDays &lt;= 0 表示永不过期。</summary>
    public int CleanupExpired(int ttlDays)
    {
        if (ttlDays <= 0) return 0;

        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations WHERE last_accessed < datetime('now', $ttl) AND profile_id = $p";
        cmd.Parameters.AddWithValue("$ttl", $"-{ttlDays} days");
        cmd.Parameters.AddWithValue("$p", ActiveProfile);
        int deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            Log.Information("清理了 {Deleted} 条过期翻译缓存", deleted);
        }
        return deleted;
    }

    /// <summary>缓存统计（当前活动方案）：(记录总数, 总命中次数)。</summary>
    public (int Count, int TotalHits) Stats()
    {
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(hit_count), 0) FROM translations WHERE profile_id = $p";
        cmd.Parameters.AddWithValue("$p", ActiveProfile);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>分页列举缓存条目，可按语言方向与关键字过滤；返回 (条目列表, 总数)。</summary>
    public (List<CacheEntry> Entries, int Total) ListEntries(
        string? sourceLang = null, string? targetLang = null, string? search = null,
        int offset = 0, int limit = 100)
    {
        using SqliteConnection conn = Open();
        var where = new StringBuilder(" WHERE profile_id = $p");
        if (!string.IsNullOrEmpty(sourceLang)) where.Append(" AND source_lang = $sl");
        if (!string.IsNullOrEmpty(targetLang)) where.Append(" AND target_lang = $tl");
        if (!string.IsNullOrEmpty(search)) where.Append(" AND (source_text LIKE $q OR translation LIKE $q)");
        string whereClause = where.ToString();

        int total;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM translations {whereClause}";
            cmd.Parameters.AddWithValue("$p", ActiveProfile);
            if (!string.IsNullOrEmpty(sourceLang)) cmd.Parameters.AddWithValue("$sl", sourceLang);
            if (!string.IsNullOrEmpty(targetLang)) cmd.Parameters.AddWithValue("$tl", targetLang);
            if (!string.IsNullOrEmpty(search)) cmd.Parameters.AddWithValue("$q", $"%{search}%");
            total = Convert.ToInt32(cmd.ExecuteScalar());
        }

        var entries = new List<CacheEntry>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT id, source_text, source_lang, target_lang, translation, hit_count, last_accessed
                FROM translations {whereClause}
                ORDER BY last_accessed DESC
                LIMIT $lim OFFSET $off
                """;
            cmd.Parameters.AddWithValue("$p", ActiveProfile);
            if (!string.IsNullOrEmpty(sourceLang)) cmd.Parameters.AddWithValue("$sl", sourceLang);
            if (!string.IsNullOrEmpty(targetLang)) cmd.Parameters.AddWithValue("$tl", targetLang);
            if (!string.IsNullOrEmpty(search)) cmd.Parameters.AddWithValue("$q", $"%{search}%");
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.Parameters.AddWithValue("$off", offset);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new CacheEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6)));
            }
        }
        return (entries, total);
    }

    /// <summary>按 id 删除单条（仅当前活动方案）。</summary>
    public int DeleteById(long id)
    {
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations WHERE id = $id AND profile_id = $p";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", ActiveProfile);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>按 id 批量删除（仅当前活动方案）。</summary>
    public int DeleteByIds(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return 0;
        using SqliteConnection conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM translations WHERE profile_id = $p AND id IN ({string.Join(",", list.Select((_, i) => "$i" + i))})";
        cmd.Parameters.AddWithValue("$p", ActiveProfile);
        for (int i = 0; i < list.Count; i++) cmd.Parameters.AddWithValue("$i" + i, list[i]);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>导出为 JSON 文件（可按语言方向筛选）；返回导出条数。</summary>
    public int ExportToJson(string path, string? sourceLang = null, string? targetLang = null)
    {
        var (entries, _) = ListEntries(sourceLang, targetLang, null, 0, int.MaxValue);
        var data = entries.Select(e => new ExportItem
        {
            Source = e.SourceText,
            SourceLang = e.SourceLang,
            TargetLang = e.TargetLang,
            Translation = e.Translation
        });
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        Log.Information("导出翻译缓存到 {Path}，共 {Count} 条", path, entries.Count);
        return entries.Count;
    }

    /// <summary>从 JSON 导入；overwrite=true 覆盖已有相同键，否则跳过。返回导入条数。</summary>
    public int ImportFromJson(string path, bool overwrite = false)
    {
        string json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<List<ExportItem>>(json);
        if (data is null || data.Count == 0) return 0;
        using SqliteConnection conn = Open();
        using var transaction = conn.BeginTransaction();
        int imported = 0;
        foreach (var item in data)
        {
            if (string.IsNullOrEmpty(item.Source) || string.IsNullOrEmpty(item.Translation)) continue;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = overwrite
                ? """
                  INSERT INTO translations (source_text, source_lang, target_lang, translation, profile_id)
                  VALUES ($text, $sl, $tl, $tr, $p)
                  ON CONFLICT(source_text, source_lang, target_lang, profile_id)
                  DO UPDATE SET translation = excluded.translation, last_accessed = CURRENT_TIMESTAMP
                  """
                : """
                  INSERT OR IGNORE INTO translations (source_text, source_lang, target_lang, translation, profile_id)
                  VALUES ($text, $sl, $tl, $tr, $p)
                  """;
            cmd.Parameters.AddWithValue("$text", item.Source);
            cmd.Parameters.AddWithValue("$sl", string.IsNullOrEmpty(item.SourceLang) ? "ja" : item.SourceLang);
            cmd.Parameters.AddWithValue("$tl", string.IsNullOrEmpty(item.TargetLang) ? "zh-CN" : item.TargetLang);
            cmd.Parameters.AddWithValue("$tr", item.Translation);
            cmd.Parameters.AddWithValue("$p", ActiveProfile);
            imported += cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        Log.Information("导入翻译缓存 {Path}，共 {Count} 条（覆盖={Overwrite}）", path, imported, overwrite);
        return imported;
    }
}

/// <summary>缓存条目。</summary>
public sealed record CacheEntry(long Id, string SourceText, string SourceLang, string TargetLang, string Translation, long HitCount, string LastAccessed);

/// <summary>导出/导入 JSON 的条目格式。</summary>
public sealed class ExportItem
{
    public string Source { get; set; } = "";
    public string SourceLang { get; set; } = "";
    public string TargetLang { get; set; } = "";
    public string Translation { get; set; } = "";
}

/// <summary>方案元数据。</summary>
public sealed record ProfileInfo(string Id, string Name, string CreatedAt);
