using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoSorter.Models;

namespace VideoSorter.Services;

public sealed class CacheService : IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private readonly string _thumbnailBaseDir;

    public CacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(appData, "VideoSorter");
        Directory.CreateDirectory(cacheDir);

        _thumbnailBaseDir = Path.Combine(cacheDir, "thumbnails");
        Directory.CreateDirectory(_thumbnailBaseDir);

        var dbPath = Path.Combine(cacheDir, "cache.db");
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL";
        walCmd.ExecuteNonQuery();

        InitializeDatabase();
    }

    public string GetThumbnailDirectory(string cacheKey) =>
        Path.Combine(_thumbnailBaseDir, cacheKey);

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS video_cache (
                cache_key TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                extension TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                last_modified_utc TEXT NOT NULL,
                creation_time_utc TEXT NOT NULL DEFAULT '',
                duration_seconds REAL NOT NULL,
                format_name TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                bit_rate INTEGER NOT NULL,
                tracks_json TEXT NOT NULL,
                thumbnail_paths_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_cache_key ON video_cache(cache_key);
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing databases: add missing columns
        using var pragmaCmd = _connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA table_info(video_cache)";
        using var reader = pragmaCmd.ExecuteReader();
        var hasCreationTime = false;
        var hasMode3D = false;
        while (reader.Read())
        {
            var colName = reader.GetString(1);
            if (colName == "creation_time_utc") hasCreationTime = true;
            if (colName == "mode_3d") hasMode3D = true;
        }
        reader.Close();

        if (!hasCreationTime)
        {
            using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE video_cache ADD COLUMN creation_time_utc TEXT NOT NULL DEFAULT ''";
            alterCmd.ExecuteNonQuery();
        }

        if (!hasMode3D)
        {
            using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE video_cache ADD COLUMN mode_3d INTEGER NOT NULL DEFAULT 0";
            alterCmd.ExecuteNonQuery();
        }
    }

    public VideoFileInfo? Get(string cacheKey)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM video_cache WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", cacheKey);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var tracks = JsonSerializer.Deserialize<List<VideoTrackInfo>>(reader.GetString(reader.GetOrdinal("tracks_json"))) ?? [];
            var thumbnailPaths = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("thumbnail_paths_json"))) ?? [];

            // Verify thumbnails still exist
            thumbnailPaths = thumbnailPaths.Where(File.Exists).ToList();

            var creationStr = reader.GetString(reader.GetOrdinal("creation_time_utc"));
            var creationTimeUtc = !string.IsNullOrEmpty(creationStr)
                ? DateTime.Parse(creationStr).ToUniversalTime()
                : DateTime.MinValue;

            var mode3dOrdinal = reader.GetOrdinal("mode_3d");
            var mode3d = !reader.IsDBNull(mode3dOrdinal) ? reader.GetInt32(mode3dOrdinal) : 0;

            return new VideoFileInfo
            {
                CacheKey = cacheKey,
                FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                FileName = reader.GetString(reader.GetOrdinal("file_name")),
                Extension = reader.GetString(reader.GetOrdinal("extension")),
                FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
                LastModifiedUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_modified_utc"))).ToUniversalTime(),
                CreationTimeUtc = creationTimeUtc,
                Duration = TimeSpan.FromSeconds(reader.GetDouble(reader.GetOrdinal("duration_seconds"))),
                FormatName = reader.GetString(reader.GetOrdinal("format_name")),
                Width = reader.GetInt32(reader.GetOrdinal("width")),
                Height = reader.GetInt32(reader.GetOrdinal("height")),
                BitRate = reader.GetInt64(reader.GetOrdinal("bit_rate")),
                Tracks = tracks,
                ThumbnailPaths = thumbnailPaths,
                Mode3D = mode3d
            };
        }
    }

    public void Save(VideoFileInfo info)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO video_cache
                (cache_key, file_path, file_name, extension, file_size, last_modified_utc, creation_time_utc,
                 duration_seconds, format_name, width, height, bit_rate, tracks_json, thumbnail_paths_json, mode_3d)
                VALUES
                (@key, @path, @name, @ext, @size, @modified, @created,
                 @duration, @format, @width, @height, @bitrate, @tracks, @thumbs, @mode3d)
                """;
            cmd.Parameters.AddWithValue("@key", info.CacheKey);
            cmd.Parameters.AddWithValue("@path", info.FilePath);
            cmd.Parameters.AddWithValue("@name", info.FileName);
            cmd.Parameters.AddWithValue("@ext", info.Extension);
            cmd.Parameters.AddWithValue("@size", info.FileSize);
            cmd.Parameters.AddWithValue("@modified", info.LastModifiedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@created", info.CreationTimeUtc != DateTime.MinValue
                ? info.CreationTimeUtc.ToString("O") : "");
            cmd.Parameters.AddWithValue("@duration", info.Duration.TotalSeconds);
            cmd.Parameters.AddWithValue("@format", info.FormatName);
            cmd.Parameters.AddWithValue("@width", info.Width);
            cmd.Parameters.AddWithValue("@height", info.Height);
            cmd.Parameters.AddWithValue("@bitrate", info.BitRate);
            cmd.Parameters.AddWithValue("@tracks", JsonSerializer.Serialize(info.Tracks));
            cmd.Parameters.AddWithValue("@thumbs", JsonSerializer.Serialize(info.ThumbnailPaths));
            cmd.Parameters.AddWithValue("@mode3d", info.Mode3D);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateThumbnailPaths(string cacheKey, List<string> thumbnailPaths)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE video_cache SET thumbnail_paths_json = @thumbs WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", cacheKey);
            cmd.Parameters.AddWithValue("@thumbs", JsonSerializer.Serialize(thumbnailPaths));
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateMode3D(string cacheKey, int mode3d)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE video_cache SET mode_3d = @mode3d WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", cacheKey);
            cmd.Parameters.AddWithValue("@mode3d", mode3d);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateFilePath(string cacheKey, string newFilePath, string newFileName)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE video_cache SET file_path = @path, file_name = @name WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", cacheKey);
            cmd.Parameters.AddWithValue("@path", newFilePath);
            cmd.Parameters.AddWithValue("@name", newFileName);
            cmd.ExecuteNonQuery();
        }
    }

    public void Remove(string cacheKey)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM video_cache WHERE cache_key = @key";
            cmd.Parameters.AddWithValue("@key", cacheKey);
            cmd.ExecuteNonQuery();
        }

        // Clean up thumbnail directory (outside lock — no DB access)
        var thumbDir = GetThumbnailDirectory(cacheKey);
        if (Directory.Exists(thumbDir))
        {
            try { Directory.Delete(thumbDir, true); }
            catch { /* ignore cleanup failures */ }
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
