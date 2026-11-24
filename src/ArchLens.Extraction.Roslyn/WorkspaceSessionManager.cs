using System.Collections.Concurrent;
using ArchLens.Model;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Thread-safe stateful cache of extracted ArchitectureSnapshots with automatic mtime + FileSystemWatcher
/// cache invalidation and selective per-project incremental re-extraction.
/// </summary>
public sealed class WorkspaceSessionManager : IDisposable
{
    private readonly IReadOnlyList<ICodeGraphExtractor> _extractors;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ArchitectureSnapshot> _baselineSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public WorkspaceSessionManager(IEnumerable<ICodeGraphExtractor> extractors)
    {
        _extractors = extractors.ToList();
    }

    public async Task<ArchitectureSnapshot> GetOrLoadAsync(
        string targetPath,
        bool forceRefresh = false,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = NormalizeKey(targetPath, options);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            CacheEntry? existing = null;
            if (!forceRefresh && _entries.TryGetValue(key, out existing))
            {
                var (changedFiles, forceFull) = existing.Tracker.ConsumeChangedFiles();
                if (!forceFull && changedFiles.Count == 0)
                {
                    return existing.Snapshot;
                }

                if (!forceFull && existing.Extractor is IIncrementalCodeGraphExtractor incremental)
                {
                    try
                    {
                        var updated = await incremental.ExtractIncrementalAsync(
                            targetPath, existing.Snapshot, changedFiles, options, cancellationToken);
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                        existing.Snapshot = updated;
                        return updated;
                    }
                    catch
                    {
                        existing.Tracker.MarkDirty(changedFiles);
                        throw;
                    }
                }

                // Mark existing tracker for full reload in case the full reload below fails or is cancelled
                existing.Tracker.MarkForceFullReload();
            }

            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(targetPath))
                ?? throw new NotSupportedException($"No registered extractor can handle target '{targetPath}'.");

            var initialWatchRoot = GetWatchRootDirectory(targetPath);
            var tracker = new FileChangeTracker([initialWatchRoot], CsprojMetadataReader.FindAncestorDirectoryBuildProps(initialWatchRoot));
            ArchitectureSnapshot snapshot;
            try
            {
                snapshot = await extractor.ExtractAsync(targetPath, options, cancellationToken);
            }
            catch
            {
                tracker.Dispose();
                throw;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                tracker.Dispose();
                throw new ObjectDisposedException(nameof(WorkspaceSessionManager));
            }

            // Expand watcher coverage to include all loaded project directories (e.g. external ../ ProjectReferences)
            var allWatchRoots = CollectWatchRoots(initialWatchRoot, snapshot);
            var ancestorProps = allWatchRoots.SelectMany(CsprojMetadataReader.FindAncestorDirectoryBuildProps);
            if (NeedsExpandedTracker(initialWatchRoot, allWatchRoots))
            {
                tracker.Dispose();
                tracker = new FileChangeTracker(allWatchRoots, ancestorProps);
            }

            var newEntry = new CacheEntry(snapshot, tracker, extractor);
            if (_entries.TryGetValue(key, out var previous))
            {
                previous.Tracker.Dispose();
            }

            _entries[key] = newEntry;
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_entries.TryRemove(key, out var removed))
                {
                    removed.Tracker.Dispose();
                }
                throw new ObjectDisposedException(nameof(WorkspaceSessionManager));
            }
            _baselineSnapshots.TryAdd(key, snapshot);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public ArchitectureSnapshot? GetBaseline(string targetPath, ExtractionOptions? options = null)
    {
        var key = NormalizeKey(targetPath, options);
        return _baselineSnapshots.GetValueOrDefault(key);
    }

    public void SetBaseline(string targetPath, ArchitectureSnapshot snapshot, ExtractionOptions? options = null)
    {
        var key = NormalizeKey(targetPath, options);
        _baselineSnapshots[key] = snapshot;
    }

    private static List<string> CollectWatchRoots(string initialWatchRoot, ArchitectureSnapshot snapshot)
    {
        var roots = new List<string> { initialWatchRoot };
        if (!string.IsNullOrWhiteSpace(snapshot.Graph.RootPath) && Directory.Exists(snapshot.Graph.RootPath))
        {
            roots.Add(snapshot.Graph.RootPath);
        }

        foreach (var proj in snapshot.Graph.Projects)
        {
            if (string.IsNullOrWhiteSpace(proj.FilePath))
            {
                continue;
            }

            try
            {
                var fullCsproj = Path.GetFullPath(Path.Combine(snapshot.Graph.RootPath, proj.FilePath));
                var dir = Path.GetDirectoryName(fullCsproj);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    roots.Add(dir);
                }
            }
            catch
            {
                // Ignore malformed paths
            }
        }

        return roots;
    }

    private static bool NeedsExpandedTracker(string initialRoot, List<string> allRoots)
    {
        var normInitial = Path.TrimEndingDirectorySeparator(Path.GetFullPath(initialRoot));
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return allRoots.Any(r =>
        {
            var norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r));
            return !norm.Equals(normInitial, cmp) &&
                   !norm.StartsWith(normInitial + Path.DirectorySeparatorChar, cmp);
        });
    }

    private static string GetWatchRootDirectory(string targetPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (Directory.Exists(full))
        {
            return full;
        }

        return Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
    }

    private static string NormalizeKey(string targetPath, ExtractionOptions? options)
    {
        string canonical;
        try
        {
            canonical = RoslynCodeGraphExtractor.ResolveTargetFile(targetPath);
        }
        catch
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        }

        var opt = options ?? new ExtractionOptions();
        return $"{canonical}|tests={opt.IncludeTestProjects}|gen={opt.IncludeGeneratedCode}|methods={opt.IncludeMethods}|tfm={opt.TargetFramework ?? string.Empty}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var kvp in _entries.ToArray())
        {
            if (_entries.TryRemove(kvp.Key, out var entry))
            {
                entry.Tracker.Dispose();
            }
        }

        // Do not call SemaphoreSlim.Dispose() on _locks while in-flight callers may still hold/release them;
        // SemaphoreSlim without AvailableWaitHandle holds no unmanaged resources and is safely reclaimed by GC.
        _locks.Clear();
    }

    private sealed class CacheEntry(ArchitectureSnapshot snapshot, FileChangeTracker tracker, ICodeGraphExtractor extractor)
    {
        public ArchitectureSnapshot Snapshot { get; set; } = snapshot;
        public FileChangeTracker Tracker { get; } = tracker;
        public ICodeGraphExtractor Extractor { get; } = extractor;
    }
}
