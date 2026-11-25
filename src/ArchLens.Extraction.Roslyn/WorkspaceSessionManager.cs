using System.Collections.Concurrent;
using ArchLens.Model;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Thread-safe stateful cache of extracted ArchitectureSnapshots with:
/// - Tier 1: In-memory cache with automatic mtime + FileSystemWatcher invalidation
/// - Tier 2: Incremental Roslyn Document updates via IIncrementalCodeGraphExtractor
/// - Tier 3: Optional persistent SQLite cache (IPersistentGraphCache) across MCP restarts
/// - Multi-backend extractor selection (Roslyn source extractor vs fast IL/Metadata extractor).
/// </summary>
public sealed class WorkspaceSessionManager : IDisposable
{
    private readonly IReadOnlyList<ICodeGraphExtractor> _extractors;
    private readonly IPersistentGraphCache? _persistentCache;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ArchitectureSnapshot> _baselineSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public WorkspaceSessionManager(
        IEnumerable<ICodeGraphExtractor> extractors,
        IPersistentGraphCache? persistentCache = null)
    {
        _extractors = extractors.ToList();
        _persistentCache = persistentCache;
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

            if (!forceRefresh && _entries.TryGetValue(key, out var existing))
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
                        PersistToCache(key, existing.Tracker, updated);
                        return updated;
                    }
                    catch
                    {
                        existing.Tracker.MarkDirty(changedFiles);
                        throw;
                    }
                }

                existing.Tracker.MarkForceFullReload();
            }

            var extractor = SelectExtractor(targetPath, options);
            var watchBinaries = extractor.Name.Equals("IL", StringComparison.OrdinalIgnoreCase);
            var initialWatchRoot = GetWatchRootDirectory(targetPath);
            var tracker = new FileChangeTracker(
                [initialWatchRoot],
                CsprojMetadataReader.FindAncestorDirectoryBuildProps(initialWatchRoot),
                watchBinaries);

            // Tier 3: Check persistent disk cache (SQLite) on cold start using stored watched roots
            if (!forceRefresh && _persistentCache is not null)
            {
                var hit = _persistentCache.TryGetWithWatchedRoots(
                    key,
                    (roots, extras) =>
                    {
                        var effectiveRoots = roots.Count > 0 ? roots : tracker.RootDirectories;
                        var effectiveExtras = extras.Count > 0 ? extras : tracker.ExtraFiles;
                        return FileChangeTracker.ComputeFingerprintForRoots(effectiveRoots, effectiveExtras, watchBinaries);
                    },
                    out var diskCached,
                    out var cachedRoots,
                    out var cachedExtras);

                if (!hit)
                {
                    hit = _persistentCache.TryGet(key, tracker.ComputeCapturedFingerprint(), out diskCached);
                }

                if (hit && diskCached is not null)
                {
                    if (cachedRoots.Count > 0 && NeedsExpandedTracker(initialWatchRoot, cachedRoots.ToList()))
                    {
                        tracker.Dispose();
                        tracker = new FileChangeTracker(cachedRoots, cachedExtras, watchBinaries);
                    }
                    else
                    {
                        tracker = ExpandTrackerIfNeeded(initialWatchRoot, diskCached, tracker, watchBinaries);
                    }
                    StoreEntry(key, diskCached, tracker, extractor);
                    return diskCached;
                }
            }

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

            tracker = ExpandTrackerIfNeeded(initialWatchRoot, snapshot, tracker, watchBinaries);
            StoreEntry(key, snapshot, tracker, extractor);
            PersistToCache(key, tracker, snapshot);
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

    private ICodeGraphExtractor SelectExtractor(string targetPath, ExtractionOptions? options)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path cannot be empty.", nameof(targetPath));
        }

        var fullPath = Path.GetFullPath(targetPath);
        var isFile = File.Exists(fullPath);
        var isDir = !isFile && Directory.Exists(fullPath);
        if (!isFile && !isDir)
        {
            throw new FileNotFoundException($"Target path does not exist: '{fullPath}'.", fullPath);
        }

        if (!string.IsNullOrWhiteSpace(options?.Backend) &&
            !options.Backend.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var requested = options.Backend.Trim();
            var matched = _extractors.FirstOrDefault(e =>
                e.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                (requested.Equals("metadata", StringComparison.OrdinalIgnoreCase) && e.Name.Equals("IL", StringComparison.OrdinalIgnoreCase)) ||
                (requested.Equals("cecil", StringComparison.OrdinalIgnoreCase) && e.Name.Equals("IL", StringComparison.OrdinalIgnoreCase)));

            if (matched is not null)
            {
                return matched;
            }

            throw new NotSupportedException($"Requested backend '{options.Backend}' is not registered. Available backends: {string.Join(", ", _extractors.Select(e => e.Name))}.");
        }

        var selected = _extractors.FirstOrDefault(e => e.CanHandle(targetPath));
        if (selected is not null)
        {
            return selected;
        }

        if (isDir)
        {
            throw new FileNotFoundException(
                $"No .slnx, .sln, .csproj, or .dll files found in directory '{fullPath}'.",
                fullPath);
        }

        throw new NotSupportedException(
            $"Unsupported file type '{Path.GetExtension(fullPath)}' for '{fullPath}'. Pass a .slnx, .sln, .csproj, .dll, .exe, or a directory containing one.");
    }

    private FileChangeTracker ExpandTrackerIfNeeded(
        string initialWatchRoot,
        ArchitectureSnapshot snapshot,
        FileChangeTracker tracker,
        bool watchBinaries)
    {
        var allWatchRoots = CollectWatchRoots(initialWatchRoot, snapshot);
        if (!NeedsExpandedTracker(initialWatchRoot, allWatchRoots))
        {
            return tracker;
        }

        var ancestorProps = allWatchRoots.SelectMany(CsprojMetadataReader.FindAncestorDirectoryBuildProps);
        tracker.Dispose();
        return new FileChangeTracker(allWatchRoots, ancestorProps, watchBinaries);
    }

    private void StoreEntry(string key, ArchitectureSnapshot snapshot, FileChangeTracker tracker, ICodeGraphExtractor extractor)
    {
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
    }

    private void PersistToCache(string key, FileChangeTracker tracker, ArchitectureSnapshot snapshot)
    {
        if (_persistentCache is null)
        {
            return;
        }

        // Never persist degraded Roslyn snapshots to disk cache so running 'dotnet restore' (which only modifies obj/)
        // immediately gets a clean reload on the next invocation instead of serving stale degraded data.
        if (snapshot.Graph.LoadStatus is { Degraded: true })
        {
            return;
        }

        // Hash the captured pre-extraction snapshot (not a post-extraction rescan) to avoid TOCTOU staleness
        var fp = tracker.ComputeCapturedFingerprint();
        _persistentCache.SetWithWatchedRoots(key, fp, snapshot, tracker.RootDirectories, tracker.ExtraFiles);
    }

    private static List<string> CollectWatchRoots(string initialWatchRoot, ArchitectureSnapshot snapshot)
    {
        var roots = new List<string> { initialWatchRoot };
        if (!string.IsNullOrWhiteSpace(snapshot.Graph.RootPath) && Directory.Exists(snapshot.Graph.RootPath))
        {
            roots.Add(snapshot.Graph.RootPath);
        }

        var visitedCsprojs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var proj in snapshot.Graph.Projects)
        {
            if (string.IsNullOrWhiteSpace(proj.FilePath))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(snapshot.Graph.RootPath, proj.FilePath));
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    roots.Add(dir);
                }
                if (fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath) && visitedCsprojs.Add(fullPath))
                {
                    queue.Enqueue(fullPath);
                }
            }
            catch
            {
                // Ignore malformed paths
            }
        }

        // Also walk transitive <ProjectReference> paths so opening a single LeafApp.csproj watches ../SiblingCore too!
        while (queue.Count > 0)
        {
            var csproj = queue.Dequeue();
            var csprojDir = Path.GetDirectoryName(csproj);
            if (string.IsNullOrEmpty(csprojDir)) continue;

            var meta = CsprojMetadataReader.Read(csproj);
            foreach (var relRef in meta.ProjectReferences)
            {
                try
                {
                    var refFull = Path.GetFullPath(Path.Combine(csprojDir, relRef.Replace('\\', Path.DirectorySeparatorChar)));
                    var refDir = Path.GetDirectoryName(refFull);
                    if (!string.IsNullOrWhiteSpace(refDir) && Directory.Exists(refDir))
                    {
                        roots.Add(refDir);
                    }
                    if (File.Exists(refFull) && visitedCsprojs.Add(refFull))
                    {
                        queue.Enqueue(refFull);
                    }
                }
                catch
                {
                    // Ignore malformed reference paths
                }
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
        var cats = opt.AnalyzerCategories is { Count: > 0 }
            ? string.Join(",", opt.AnalyzerCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            : string.Empty;
        var rawBackend = string.IsNullOrWhiteSpace(opt.Backend) ? "auto" : opt.Backend.Trim().ToLowerInvariant();
        var backend = rawBackend is "metadata" or "cecil" ? "il" : rawBackend;
        return $"{canonical}|backend={backend}|tests={opt.IncludeTestProjects}|gen={opt.IncludeGeneratedCode}|methods={opt.IncludeMethods}|tfm={opt.TargetFramework ?? string.Empty}|analyzers={opt.IncludeAnalyzerDiagnostics}|elevate={opt.ElevateAnalyzerSeverity}|cats={cats}";
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

        _locks.Clear();
    }

    private sealed class CacheEntry(ArchitectureSnapshot snapshot, FileChangeTracker tracker, ICodeGraphExtractor extractor)
    {
        public ArchitectureSnapshot Snapshot { get; set; } = snapshot;
        public FileChangeTracker Tracker { get; } = tracker;
        public ICodeGraphExtractor Extractor { get; } = extractor;
    }
}
