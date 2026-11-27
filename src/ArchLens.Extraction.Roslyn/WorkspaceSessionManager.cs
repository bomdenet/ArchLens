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
    private readonly ConcurrentDictionary<string, byte> _explicitBaselineKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks = new(StringComparer.OrdinalIgnoreCase);
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

        var extractor = SelectExtractor(targetPath, options);
        var key = NormalizeKey(targetPath, options, extractor.Name);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (!forceRefresh && _entries.TryGetValue(key, out var existing))
            {
                var (changedFiles, forceFull) = existing.Tracker.ConsumeChangedFiles();
                if (!forceFull && changedFiles.Count == 0 && HasNewExternalLinkedCompilePaths(existing.Tracker))
                {
                    forceFull = true;
                }
                if (!forceFull && changedFiles.Count == 0)
                {
                    return existing.Snapshot;
                }

                if (!forceFull && existing.Extractor is IIncrementalCodeGraphExtractor incremental)
                {
                    var targetGate = _targetLocks.GetOrAdd(ResolveCanonicalTargetPath(targetPath), _ => new SemaphoreSlim(1, 1));
                    await targetGate.WaitAsync(cancellationToken);
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
                    finally
                    {
                        targetGate.Release();
                    }
                }

                existing.Tracker.MarkForceFullReload();
            }

            var watchBinaries = extractor.Name.Equals("IL", StringComparison.OrdinalIgnoreCase);
            var fullTargetPath = Path.GetFullPath(targetPath);
            var isSingleBinaryTarget = watchBinaries && IsBinaryFilePath(fullTargetPath);
            var initialWatchRoot = GetWatchRootDirectory(targetPath);
            var initialRoots = isSingleBinaryTarget
                ? Array.Empty<string>()
                : [initialWatchRoot];
            var initialExtras = isSingleBinaryTarget
                ? CollectSingleBinaryFiles(fullTargetPath)
                : CollectInitialExtraFiles(fullTargetPath, initialWatchRoot);
            var tracker = new FileChangeTracker(
                initialRoots,
                initialExtras,
                watchBinaries,
                enableWatchers: !isSingleBinaryTarget);

            // Tier 3: Check persistent disk cache (SQLite) on cold start using stored watched roots
            if (!forceRefresh && _persistentCache is not null)
            {
                var evaluatedWatchedRoots = false;
                var hit = _persistentCache.TryGetWithWatchedRoots(
                    key,
                    (roots, extras) =>
                    {
                        evaluatedWatchedRoots = true;
                        var hasStored = roots.Count > 0 || extras.Count > 0;
                        var mergedRoots = new List<string>(hasStored ? roots : tracker.RootDirectories);
                        var seenRoots = new HashSet<string>(mergedRoots, StringComparer.OrdinalIgnoreCase);
                        var mergedExtras = new HashSet<string>(hasStored ? extras : tracker.ExtraFiles, StringComparer.OrdinalIgnoreCase);
                        // Re-scan ancestor build/config files and linked <Compile Include> wildcards so newly created
                        // ancestor props/.editorconfig or external linked source directories invalidate warm cache entries.
                        if (!isSingleBinaryTarget)
                        {
                            var baseRoots = mergedRoots.ToList();
                            foreach (var r in baseRoots)
                            {
                                foreach (var f in CsprojMetadataReader.FindAncestorDirectoryBuildProps(r)) mergedExtras.Add(f);
                                foreach (var f in CsprojMetadataReader.FindAncestorEditorConfigs(r)) mergedExtras.Add(f);
                                if (Directory.Exists(r))
                                {
                                    try
                                    {
                                        foreach (var csproj in Directory.EnumerateFiles(r, "*.csproj", SearchOption.AllDirectories))
                                        {
                                            var (linkedFiles, linkedDirs) = CsprojMetadataReader.ReadLinkedCompilePaths(csproj);
                                            foreach (var lf in linkedFiles) mergedExtras.Add(lf);
                                            foreach (var ld in linkedDirs)
                                            {
                                                if (seenRoots.Add(ld)) mergedRoots.Add(ld);
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        return FileChangeTracker.ComputeFingerprintForRoots(mergedRoots, mergedExtras.ToList(), watchBinaries);
                    },
                    out var diskCached,
                    out var cachedRoots,
                    out var cachedExtras);

                if (!hit && !evaluatedWatchedRoots)
                {
                    hit = _persistentCache.TryGet(key, tracker.ComputeCapturedFingerprint(), out diskCached);
                }

                if (hit && diskCached is not null)
                {
                    if (!isSingleBinaryTarget &&
                        (cachedRoots.Count > 0 || cachedExtras.Count > 0) &&
                        NeedsExpandedTracker(tracker, cachedRoots.ToList(), cachedExtras.ToList()))
                    {
                        tracker.Dispose();
                        tracker = new FileChangeTracker(cachedRoots, cachedExtras, watchBinaries);
                    }
                    else if (!isSingleBinaryTarget)
                    {
                        tracker = ExpandTrackerIfNeeded(initialWatchRoot, diskCached, tracker, watchBinaries);
                    }
                    StoreEntry(key, diskCached, tracker, extractor);
                    return diskCached;
                }
            }

            ArchitectureSnapshot snapshot;
            // Serialize cold extractions per canonical target path so concurrent calls with different
            // ExtractionOptions do not open overlapping MSBuildWorkspace instances against the same .csproj/obj files.
            var extractGate = _targetLocks.GetOrAdd(ResolveCanonicalTargetPath(targetPath), _ => new SemaphoreSlim(1, 1));
            await extractGate.WaitAsync(cancellationToken);
            try
            {
                snapshot = await extractor.ExtractAsync(targetPath, options, cancellationToken);
            }
            catch
            {
                tracker.Dispose();
                throw;
            }
            finally
            {
                extractGate.Release();
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                tracker.Dispose();
                throw new ObjectDisposedException(nameof(WorkspaceSessionManager));
            }

            if (!isSingleBinaryTarget)
            {
                tracker = ExpandTrackerIfNeeded(initialWatchRoot, snapshot, tracker, watchBinaries);
            }
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
        var key = NormalizeKeyForLookup(targetPath, options);
        return _baselineSnapshots.GetValueOrDefault(key);
    }

    public bool HasExplicitBaseline(string targetPath, ExtractionOptions? options = null)
    {
        var key = NormalizeKeyForLookup(targetPath, options);
        return _explicitBaselineKeys.ContainsKey(key);
    }

    public void SetBaseline(string targetPath, ArchitectureSnapshot snapshot, ExtractionOptions? options = null)
    {
        var key = NormalizeKeyForLookup(targetPath, options);
        _baselineSnapshots[key] = snapshot;
        _explicitBaselineKeys[key] = 0;
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
                (requested.Equals("metadata", StringComparison.OrdinalIgnoreCase) && e.Name.Equals("IL", StringComparison.OrdinalIgnoreCase)));

            if (matched is not null)
            {
                return matched;
            }

            throw new ArgumentException($"Unknown backend '{options.Backend}'. Supported values: 'auto', 'roslyn', 'il'.");
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

    private static bool HasNewExternalLinkedCompilePaths(FileChangeTracker tracker)
    {
        if (tracker.RootDirectories.Count == 0)
        {
            return false;
        }

        var existingRoots = new HashSet<string>(tracker.RootDirectories, StringComparer.OrdinalIgnoreCase);
        var existingExtras = new HashSet<string>(tracker.ExtraFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var root in tracker.RootDirectories)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
                {
                    var (linkedFiles, linkedDirs) = CsprojMetadataReader.ReadLinkedCompilePaths(csproj);
                    foreach (var lf in linkedFiles)
                    {
                        if (!existingExtras.Contains(lf)) return true;
                    }
                    foreach (var ld in linkedDirs)
                    {
                        if (!existingRoots.Contains(ld)) return true;
                    }
                }
            }
            catch
            {
                // Ignore transient filesystem enumeration errors
            }
        }
        return false;
    }

    private FileChangeTracker ExpandTrackerIfNeeded(
        string initialWatchRoot,
        ArchitectureSnapshot snapshot,
        FileChangeTracker tracker,
        bool watchBinaries)
    {
        var (allWatchRoots, linkedExtraFiles) = CollectWatchRootsAndExtras(initialWatchRoot, snapshot);
        var allExtras = new List<string>(linkedExtraFiles);
        foreach (var root in allWatchRoots)
        {
            allExtras.AddRange(CsprojMetadataReader.FindAncestorDirectoryBuildProps(root));
            allExtras.AddRange(CsprojMetadataReader.FindAncestorEditorConfigs(root));
        }

        if (!NeedsExpandedTracker(tracker, allWatchRoots, allExtras))
        {
            return tracker;
        }

        tracker.Dispose();
        return new FileChangeTracker(allWatchRoots, allExtras, watchBinaries);
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

        // Never adopt a degraded snapshot as an implicit session baseline
        if (snapshot.Graph.LoadStatus is not { Degraded: true })
        {
            _baselineSnapshots.TryAdd(key, snapshot);
        }
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

    private static bool IsBinaryFilePath(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return false;
        }
        var ext = Path.GetExtension(fullPath);
        return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectSingleBinaryFiles(string fullBinaryPath)
    {
        var files = new List<string> { fullBinaryPath };
        var pdb = Path.ChangeExtension(fullBinaryPath, ".pdb");
        if (File.Exists(pdb))
        {
            files.Add(pdb);
        }

        if (fullBinaryPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var siblingDll = Path.ChangeExtension(fullBinaryPath, ".dll");
            if (File.Exists(siblingDll))
            {
                files.Add(siblingDll);
                var siblingPdb = Path.ChangeExtension(siblingDll, ".pdb");
                if (File.Exists(siblingPdb))
                {
                    files.Add(siblingPdb);
                }
            }
        }

        return files;
    }

    private static List<string> CollectInitialExtraFiles(string fullTargetPath, string initialWatchRoot)
    {
        var extras = new List<string>();
        extras.AddRange(CsprojMetadataReader.FindAncestorDirectoryBuildProps(initialWatchRoot));
        extras.AddRange(CsprojMetadataReader.FindAncestorEditorConfigs(initialWatchRoot));
        if (File.Exists(fullTargetPath) && fullTargetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var (linkedFiles, _) = CsprojMetadataReader.ReadLinkedCompilePaths(fullTargetPath);
            extras.AddRange(linkedFiles);
        }
        return extras;
    }

    private static (List<string> Roots, List<string> ExtraFiles) CollectWatchRootsAndExtras(string initialWatchRoot, ArchitectureSnapshot snapshot)
    {
        var roots = new List<string> { initialWatchRoot };
        var extraFiles = new List<string>();
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

        // Also walk transitive <ProjectReference> paths and linked <Compile Include=".." /> files
        while (queue.Count > 0)
        {
            var csproj = queue.Dequeue();
            var csprojDir = Path.GetDirectoryName(csproj);
            if (string.IsNullOrEmpty(csprojDir)) continue;

            var (linkedFiles, linkedDirs) = CsprojMetadataReader.ReadLinkedCompilePaths(csproj);
            extraFiles.AddRange(linkedFiles);
            roots.AddRange(linkedDirs);

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

        return (roots, extraFiles);
    }

    private static bool NeedsExpandedTracker(FileChangeTracker currentTracker, List<string> allRoots, List<string> allExtras)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var existingRoots = currentTracker.RootDirectories
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .ToList();

        foreach (var r in allRoots)
        {
            if (string.IsNullOrWhiteSpace(r) || !Directory.Exists(r)) continue;
            var norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r));
            var covered = existingRoots.Any(er =>
                norm.Equals(er, cmp) || norm.StartsWith(er + Path.DirectorySeparatorChar, cmp));
            if (!covered)
            {
                return true;
            }
        }

        var existingExtras = currentTracker.ExtraFiles
            .Select(Path.GetFullPath)
            .ToHashSet(comparer);
        foreach (var f in allExtras)
        {
            if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) continue;
            var full = Path.GetFullPath(f);
            if (!existingExtras.Contains(full))
            {
                return true;
            }
        }

        return false;
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

    private static string ResolveCanonicalTargetPath(string targetPath)
    {
        try
        {
            return RoslynCodeGraphExtractor.ResolveTargetFile(targetPath);
        }
        catch
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        }
    }

    private string NormalizeKeyForLookup(string targetPath, ExtractionOptions? options)
    {
        string? resolvedBackend = null;
        try
        {
            resolvedBackend = SelectExtractor(targetPath, options).Name;
        }
        catch
        {
            // Fall back to raw backend option if target path doesn't resolve yet
        }
        return NormalizeKey(targetPath, options, resolvedBackend);
    }

    private static string NormalizeKey(string targetPath, ExtractionOptions? options, string? resolvedBackendName = null)
    {
        var canonical = ResolveCanonicalTargetPath(targetPath);

        var opt = options ?? new ExtractionOptions();
        var cats = opt.AnalyzerCategories is { Count: > 0 }
            ? string.Join(",", opt.AnalyzerCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            : string.Empty;
        var rawBackend = !string.IsNullOrWhiteSpace(resolvedBackendName)
            ? resolvedBackendName.Trim().ToLowerInvariant()
            : (string.IsNullOrWhiteSpace(opt.Backend) ? "auto" : opt.Backend.Trim().ToLowerInvariant());
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
        _targetLocks.Clear();
    }

    private sealed class CacheEntry(ArchitectureSnapshot snapshot, FileChangeTracker tracker, ICodeGraphExtractor extractor)
    {
        public ArchitectureSnapshot Snapshot { get; set; } = snapshot;
        public FileChangeTracker Tracker { get; } = tracker;
        public ICodeGraphExtractor Extractor { get; } = extractor;
    }
}
