using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Model;
using Microsoft.Data.Sqlite;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Persistent SQLite-backed cache of ArchitectureSnapshots using GZip-compressed JSON payloads.
/// Persists watched root directories alongside each entry so multi-root solutions (sibling ProjectReferences)
/// validate fingerprints accurately across MCP restarts.
/// </summary>
public sealed class SqliteGraphCache : IPersistentGraphCache, IDisposable
{
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _connectionString;
    private readonly object _syncLock = new();
    private bool _available;
    private int _disposed;

    public SqliteGraphCache(string? databasePath = null)
    {
        DatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? GetDefaultDatabasePath()
            : Path.GetFullPath(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 5
        }.ToString();

        _available = TryInitializeSchema();
    }

    public string DatabasePath { get; }
    public bool IsAvailable => _available && Volatile.Read(ref _disposed) == 0;

    public static string GetDefaultDatabasePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }
        return Path.Combine(baseDir, "ArchLens", "graph-cache.db");
    }

    public bool TryGet(string cacheKey, string contentFingerprint, out ArchitectureSnapshot? snapshot)
    {
        snapshot = null;
        if (!IsAvailable || string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(contentFingerprint))
        {
            return false;
        }

        lock (_syncLock)
        {
            if (!IsAvailable) return false;
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT payload FROM snapshot_cache
                    WHERE cache_key = $key AND schema_version = $ver AND fingerprint = $fp
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$key", cacheKey);
                cmd.Parameters.AddWithValue("$ver", CurrentSchemaVersion);
                cmd.Parameters.AddWithValue("$fp", contentFingerprint);

                var raw = cmd.ExecuteScalar();
                return raw is byte[] compressed && TryDeserializeSnapshot(compressed, out snapshot);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryGetWithWatchedRoots(
        string cacheKey,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, string> fingerprintEvaluator,
        out ArchitectureSnapshot? snapshot,
        out IReadOnlyList<string> watchedRoots,
        out IReadOnlyList<string> extraFiles)
    {
        snapshot = null;
        watchedRoots = [];
        extraFiles = [];

        if (!IsAvailable || string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        lock (_syncLock)
        {
            if (!IsAvailable) return false;
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT fingerprint, watched_roots, extra_files, payload FROM snapshot_cache
                    WHERE cache_key = $key AND schema_version = $ver
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$key", cacheKey);
                cmd.Parameters.AddWithValue("$ver", CurrentSchemaVersion);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return false;
                }

                var storedFingerprint = reader.GetString(0);
                var rootsJson = reader.GetString(1);
                var extrasJson = reader.GetString(2);
                var roots = JsonSerializer.Deserialize<List<string>>(rootsJson) ?? [];
                var extras = JsonSerializer.Deserialize<List<string>>(extrasJson) ?? [];

                var currentFingerprint = fingerprintEvaluator(roots, extras);
                if (!string.Equals(storedFingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    return false;
                }

                if (reader["payload"] is not byte[] compressed || !TryDeserializeSnapshot(compressed, out snapshot))
                {
                    return false;
                }

                watchedRoots = roots;
                extraFiles = extras;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Set(string cacheKey, string contentFingerprint, ArchitectureSnapshot snapshot) =>
        SetWithWatchedRoots(cacheKey, contentFingerprint, snapshot, [], []);

    public void SetWithWatchedRoots(
        string cacheKey,
        string contentFingerprint,
        ArchitectureSnapshot snapshot,
        IReadOnlyList<string> watchedRoots,
        IReadOnlyList<string> extraFiles)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(contentFingerprint))
        {
            return;
        }

        lock (_syncLock)
        {
            if (!IsAvailable) return;
            try
            {
                var dto = new CachedSnapshotDto(
                    SnapshotId: snapshot.SnapshotId,
                    TargetPath: snapshot.TargetPath,
                    CapturedAt: snapshot.CapturedAt,
                    RootPath: snapshot.Graph.RootPath,
                    Nodes: snapshot.Graph.Nodes.ToList(),
                    Edges: snapshot.Graph.Edges.ToList(),
                    Projects: snapshot.Graph.Projects.ToList(),
                    Diagnostics: snapshot.Graph.Diagnostics.ToList(),
                    DiRegistrations: snapshot.Graph.DiRegistrations.ToList(),
                    AnalyzerDiagnostics: snapshot.Graph.AnalyzerDiagnostics.ToList());

                var compressed = CompressUtf8(JsonSerializer.Serialize(dto, JsonOptions));
                var rootsJson = JsonSerializer.Serialize(watchedRoots);
                var extrasJson = JsonSerializer.Serialize(extraFiles);

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO snapshot_cache (cache_key, schema_version, fingerprint, watched_roots, extra_files, captured_at, payload)
                    VALUES ($key, $ver, $fp, $roots, $extras, $at, $payload)
                    ON CONFLICT(cache_key) DO UPDATE SET
                        schema_version = excluded.schema_version,
                        fingerprint = excluded.fingerprint,
                        watched_roots = excluded.watched_roots,
                        extra_files = excluded.extra_files,
                        captured_at = excluded.captured_at,
                        payload = excluded.payload;
                    """;
                cmd.Parameters.AddWithValue("$key", cacheKey);
                cmd.Parameters.AddWithValue("$ver", CurrentSchemaVersion);
                cmd.Parameters.AddWithValue("$fp", contentFingerprint);
                cmd.Parameters.AddWithValue("$roots", rootsJson);
                cmd.Parameters.AddWithValue("$extras", extrasJson);
                cmd.Parameters.AddWithValue("$at", snapshot.CapturedAt.ToString("O"));
                cmd.Parameters.Add("$payload", SqliteType.Blob).Value = compressed;
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort persistence: never fail analysis due to cache write failure
            }
        }
    }

    public void Invalidate(string cacheKey)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        lock (_syncLock)
        {
            if (!IsAvailable) return;
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM snapshot_cache WHERE cache_key = $key;";
                cmd.Parameters.AddWithValue("$key", cacheKey);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Ignore
            }
        }
    }

    private bool TryInitializeSchema()
    {
        lock (_syncLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(DatabasePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE IF NOT EXISTS snapshot_cache (
                        cache_key TEXT PRIMARY KEY,
                        schema_version INTEGER NOT NULL,
                        fingerprint TEXT NOT NULL,
                        watched_roots TEXT NOT NULL DEFAULT '[]',
                        extra_files TEXT NOT NULL DEFAULT '[]',
                        captured_at TEXT NOT NULL,
                        payload BLOB NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();

                // Migrate v1 table if columns are missing
                EnsureColumnExists(conn, "watched_roots", "TEXT NOT NULL DEFAULT '[]'");
                EnsureColumnExists(conn, "extra_files", "TEXT NOT NULL DEFAULT '[]'");
                return true;
            }
            catch
            {
                // Degrade gracefully to in-memory-only operation on read-only/corrupt environments
                return false;
            }
        }
    }

    private static void EnsureColumnExists(SqliteConnection conn, string columnName, string columnDef)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(snapshot_cache);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE snapshot_cache ADD COLUMN {columnName} {columnDef};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static bool TryDeserializeSnapshot(byte[] compressed, out ArchitectureSnapshot? snapshot)
    {
        snapshot = null;
        if (compressed.Length == 0)
        {
            return false;
        }

        var json = DecompressUtf8(compressed);
        var dto = JsonSerializer.Deserialize<CachedSnapshotDto>(json, JsonOptions);
        if (dto is null || string.IsNullOrWhiteSpace(dto.SnapshotId) || string.IsNullOrWhiteSpace(dto.TargetPath))
        {
            return false;
        }

        var graph = new CodeGraph(
            rootPath: dto.RootPath ?? string.Empty,
            nodes: dto.Nodes ?? [],
            edges: dto.Edges ?? [],
            projects: dto.Projects ?? [],
            diagnostics: dto.Diagnostics ?? [],
            diRegistrations: dto.DiRegistrations ?? [],
            analyzerDiagnostics: dto.AnalyzerDiagnostics ?? []);

        snapshot = new ArchitectureSnapshot(dto.SnapshotId, dto.TargetPath, dto.CapturedAt, graph);
        return true;
    }

    private static byte[] CompressUtf8(string text)
    {
        var rawBytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(rawBytes, 0, rawBytes.Length);
        }
        return output.ToArray();
    }

    private static string DecompressUtf8(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_syncLock)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                SqliteConnection.ClearPool(conn);
            }
            catch
            {
                // Ignore
            }
        }
    }

    private sealed record CachedSnapshotDto(
        string SnapshotId,
        string TargetPath,
        DateTimeOffset CapturedAt,
        string? RootPath,
        List<CodeNode>? Nodes,
        List<DependencyEdge>? Edges,
        List<ProjectInfo>? Projects,
        List<ExtractionDiagnostic>? Diagnostics,
        List<DiRegistration>? DiRegistrations,
        List<AnalyzerDiagnostic>? AnalyzerDiagnostics);
}
