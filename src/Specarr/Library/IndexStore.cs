using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Specarr.Media;
using Specarr.Options;

namespace Specarr.Library;

/// <summary>A row of the cache without the large columns: enough to fill the tables at start.</summary>
public sealed record StoredFile(string Path, long Size, long ModifiedTicks, string? SummaryJson, int SummaryVersion, string? Error);

/// <summary>
/// What was read from each file (ffprobe JSON, dovi_tool summary, mediainfo
/// facts), saved in SQLite with the file's size and modification time. A file
/// is only read again when one of them changes, so a restart is instant.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;
    private readonly ILogger<IndexStore> _logger;

    public IndexStore(IOptions<SpecarrOptions> options, ILogger<IndexStore> logger)
    {
        _logger = logger;
        var path = options.Value.DatabasePath;
        _connection = Open(path) ?? OpenInMemory();
        Initialize();
    }

    /// <summary>Where the cache lives, or null when it is only in memory (the folder could not be written).</summary>
    public string? FilePath { get; private set; }

    public long FileSizeBytes
    {
        get
        {
            try
            {
                // The write-ahead log holds recent writes until SQLite folds them into the file.
                return FilePath == null
                    ? 0
                    : new[] { FilePath, FilePath + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    private SqliteConnection? Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            FilePath = System.IO.Path.GetFullPath(path);
            return connection;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not open the cache at {Path} ({Message}); keeping it in memory, so every file is read again after a restart. Mount a writable /config.",
                path, e.Message);
            return null;
        }
    }

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        Execute(_connection, """
            CREATE TABLE IF NOT EXISTS files (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                probed_utc INTEGER NOT NULL,
                ffprobe TEXT,
                hdr10plus TEXT,
                dovi TEXT,
                dovi_error TEXT,
                mediainfo TEXT,
                error TEXT,
                summary TEXT,
                summary_version INTEGER NOT NULL DEFAULT 0
            );
            """);

        // Columns added after v0.1.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(files)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("audio_bitrates"))
        {
            Execute(_connection, "ALTER TABLE files ADD COLUMN audio_bitrates TEXT");
        }
    }

    public List<StoredFile> LoadAll()
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, size, mtime, summary, summary_version, error FROM files";
            using var reader = command.ExecuteReader();
            var rows = new List<StoredFile>();
            while (reader.Read())
            {
                rows.Add(new StoredFile(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return rows;
        }
    }

    /// <summary>The row of one file without the large columns, when the cache has it for this exact size and date.</summary>
    public StoredFile? GetRow(string path, long size, long modifiedTicks)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, size, mtime, summary, summary_version, error FROM files WHERE path = $path AND size = $size AND mtime = $mtime";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$mtime", modifiedTicks);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new StoredFile(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5))
                : null;
        }
    }

    /// <summary>What was read from the file, when the cache has it for this exact size and date.</summary>
    public RawProbe? Get(string path, long size, long modifiedTicks)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT ffprobe, hdr10plus, dovi, dovi_error, mediainfo, audio_bitrates FROM files WHERE path = $path AND size = $size AND mtime = $mtime AND ffprobe IS NOT NULL";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$mtime", modifiedTicks);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new RawProbe
            {
                FfprobeJson = reader.GetString(0),
                Hdr10PlusStreams = ParseInts(reader.IsDBNull(1) ? null : reader.GetString(1)),
                DoviSummary = reader.IsDBNull(2) ? null : reader.GetString(2),
                DoviError = reader.IsDBNull(3) ? null : reader.GetString(3),
                MediaInfo = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<MediaInfoFacts>(reader.GetString(4)),
                MeasuredAudioBitRates = reader.IsDBNull(5)
                    ? new Dictionary<int, long>()
                    : JsonSerializer.Deserialize<Dictionary<int, long>>(reader.GetString(5)) ?? new Dictionary<int, long>()
            };
        }
    }

    /// <summary>Saves a successful read, or the error of a failed one (so it is not retried until the file changes).</summary>
    public void Save(string path, long size, long modifiedTicks, RawProbe? raw, string? error, FileSummary? summary)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO files (path, size, mtime, probed_utc, ffprobe, hdr10plus, dovi, dovi_error, mediainfo, audio_bitrates, error, summary, summary_version)
                VALUES ($path, $size, $mtime, $probed, $ffprobe, $hdr10plus, $dovi, $doviError, $mediainfo, $audioBitRates, $error, $summary, $version)
                ON CONFLICT(path) DO UPDATE SET
                    size = excluded.size, mtime = excluded.mtime, probed_utc = excluded.probed_utc,
                    ffprobe = excluded.ffprobe, hdr10plus = excluded.hdr10plus, dovi = excluded.dovi,
                    dovi_error = excluded.dovi_error, mediainfo = excluded.mediainfo, audio_bitrates = excluded.audio_bitrates, error = excluded.error,
                    summary = excluded.summary, summary_version = excluded.summary_version
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$mtime", modifiedTicks);
            command.Parameters.AddWithValue("$probed", DateTime.UtcNow.Ticks);
            command.Parameters.AddWithValue("$ffprobe", (object?)raw?.FfprobeJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$hdr10plus", raw == null ? DBNull.Value : string.Join(',', raw.Hdr10PlusStreams));
            command.Parameters.AddWithValue("$dovi", (object?)raw?.DoviSummary ?? DBNull.Value);
            command.Parameters.AddWithValue("$doviError", (object?)raw?.DoviError ?? DBNull.Value);
            command.Parameters.AddWithValue("$mediainfo", raw?.MediaInfo == null ? DBNull.Value : JsonSerializer.Serialize(raw.MediaInfo));
            command.Parameters.AddWithValue("$audioBitRates", raw == null || raw.MeasuredAudioBitRates.Count == 0
                ? DBNull.Value
                : JsonSerializer.Serialize(raw.MeasuredAudioBitRates));
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary", (object?)summary?.ToJson() ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", summary == null ? 0 : FileSummary.CurrentVersion);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Replaces only what mediainfo said, for a row that was read before it said more.</summary>
    public void UpdateMediaInfo(string path, MediaInfoFacts facts)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE files SET mediainfo = $mediainfo WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$mediainfo", JsonSerializer.Serialize(facts));
            command.ExecuteNonQuery();
        }
    }

    public void UpdateSummary(string path, FileSummary summary)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE files SET summary = $summary, summary_version = $version WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$summary", summary.ToJson());
            command.Parameters.AddWithValue("$version", FileSummary.CurrentVersion);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Forgets files that are gone from the library, or from a browsed folder.</summary>
    public void Delete(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM files WHERE path = $path";
            var parameter = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in paths)
            {
                parameter.Value = path;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Forgets failed reads, so they are tried again.</summary>
    public void ClearErrors()
    {
        lock (_lock)
        {
            Execute(_connection, "DELETE FROM files WHERE ffprobe IS NULL");
        }
    }

    /// <summary>Forgets everything, so every file is read again.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Execute(_connection, "DELETE FROM files");
            Execute(_connection, "VACUUM");
        }
    }

    private static IReadOnlyList<int> ParseInts(string? text)
    {
        return string.IsNullOrEmpty(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
