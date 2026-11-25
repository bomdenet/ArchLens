using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Tracks source and project file changes across one or more workspace root directories and ancestor build files
/// using both FileSystemWatcher and mtime/length snapshots as a reliable fallback.
/// </summary>
internal sealed class FileChangeTracker : IDisposable
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "TestResults"
    };

    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".xaml", ".axaml", ".csproj", ".sln", ".slnx", ".props", ".targets"
    };

    private readonly IReadOnlyList<string> _rootDirectories;
    private readonly IReadOnlyList<string> _extraFiles;
    private readonly bool _watchBinaries;
    private readonly ConcurrentDictionary<string, byte> _watcherDirtyPaths;
    private readonly List<FileSystemWatcher> _watchers = [];
    private Dictionary<string, (long LastWriteUtcTicks, long Length)> _fileSnapshot;
    private int _overflowed;
    private int _disposed;

    public FileChangeTracker(string rootDirectory, bool watchBinaries = false)
        : this([rootDirectory], null, watchBinaries)
    {
    }

    public FileChangeTracker(IEnumerable<string> rootDirectories, IEnumerable<string>? extraFiles = null, bool watchBinaries = false, bool enableWatchers = true)
    {
        _watchBinaries = watchBinaries;
        _watcherDirtyPaths = new ConcurrentDictionary<string, byte>(PathComparer);
        _rootDirectories = DeduplicateRoots(rootDirectories);
        _extraFiles = (extraFiles ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToList();

        _fileSnapshot = ScanAllFiles();
        if (enableWatchers)
        {
            foreach (var root in _rootDirectories)
            {
                var watcher = TryCreateWatcher(root);
                if (watcher is not null)
                {
                    _watchers.Add(watcher);
                }
            }
        }
    }

    public (IReadOnlySet<string> ChangedFiles, bool ForceFullReload) ConsumeChangedFiles()
    {
        var overflow = Interlocked.Exchange(ref _overflowed, 0) != 0;
        var changed = new HashSet<string>(PathComparer);

        foreach (var path in _watcherDirtyPaths.Keys.ToList())
        {
            if (_watcherDirtyPaths.TryRemove(path, out _))
            {
                changed.Add(path);
            }
        }

        var currentScan = ScanAllFiles();

        foreach (var (path, stamp) in currentScan)
        {
            if (!_fileSnapshot.TryGetValue(path, out var prev) ||
                prev.LastWriteUtcTicks != stamp.LastWriteUtcTicks ||
                prev.Length != stamp.Length)
            {
                changed.Add(path);
            }
        }

        foreach (var oldPath in _fileSnapshot.Keys)
        {
            if (!currentScan.ContainsKey(oldPath))
            {
                changed.Add(oldPath);
            }
        }

        _fileSnapshot = currentScan;
        return (changed, overflow);
    }

    public void MarkDirty(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _watcherDirtyPaths[Path.GetFullPath(path)] = 0;
        }
    }

    public IReadOnlyList<string> RootDirectories => _rootDirectories;
    public IReadOnlyList<string> ExtraFiles => _extraFiles;

    public void MarkForceFullReload()
    {
        Interlocked.Exchange(ref _overflowed, 1);
    }

    /// <summary>
    /// Computes a deterministic SHA-256 fingerprint over the tracker's captured file snapshot
    /// (avoiding TOCTOU races where edits made during extraction get hashed onto an older snapshot).
    /// </summary>
    public string ComputeCapturedFingerprint() => HashFileMap(_fileSnapshot);

    /// <summary>
    /// Computes a deterministic SHA-256 fingerprint by scanning the given roots and extra files directly.
    /// </summary>
    public static string ComputeFingerprintForRoots(
        IEnumerable<string> rootDirectories,
        IEnumerable<string>? extraFiles = null,
        bool watchBinaries = false)
    {
        using var temp = new FileChangeTracker(rootDirectories, extraFiles, watchBinaries, enableWatchers: false);
        return temp.ComputeCapturedFingerprint();
    }

    private static string HashFileMap(Dictionary<string, (long LastWriteUtcTicks, long Length)> scan)
    {
        var sb = new StringBuilder(scan.Count * 64);
        foreach (var kvp in scan.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key).Append('|')
              .Append(kvp.Value.LastWriteUtcTicks).Append('|')
              .Append(kvp.Value.Length).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static List<string> DeduplicateRoots(IEnumerable<string> directories)
    {
        var normalized = directories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Where(Directory.Exists)
            .Distinct(PathComparer)
            .OrderBy(d => d.Length)
            .ToList();

        var minimal = new List<string>();
        foreach (var dir in normalized)
        {
            var covered = minimal.Any(parent =>
                dir.Equals(parent, PathComparison) ||
                dir.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison));
            if (!covered)
            {
                minimal.Add(dir);
            }
        }

        return minimal;
    }

    private FileSystemWatcher? TryCreateWatcher(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            return null;
        }

        try
        {
            var watcher = new FileSystemWatcher(rootDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };

            // Attach handlers BEFORE enabling events so startup edits are never missed
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnRenamedEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            return null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (IsTrackedFile(e.FullPath))
            {
                _watcherDirtyPaths[Path.GetFullPath(e.FullPath)] = 0;
            }
        }
        catch
        {
            Interlocked.Exchange(ref _overflowed, 1);
        }
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        try
        {
            if (IsTrackedFile(e.OldFullPath))
            {
                _watcherDirtyPaths[Path.GetFullPath(e.OldFullPath)] = 0;
            }

            if (IsTrackedFile(e.FullPath))
            {
                _watcherDirtyPaths[Path.GetFullPath(e.FullPath)] = 0;
            }
        }
        catch
        {
            Interlocked.Exchange(ref _overflowed, 1);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref _overflowed, 1);
    }

    private Dictionary<string, (long LastWriteUtcTicks, long Length)> ScanAllFiles()
    {
        var result = new Dictionary<string, (long, long)>(PathComparer);
        foreach (var root in _rootDirectories)
        {
            ScanDirectory(root, result);
        }

        foreach (var extra in _extraFiles)
        {
            if (File.Exists(extra))
            {
                try
                {
                    var info = new FileInfo(extra);
                    result[info.FullName] = (info.LastWriteTimeUtc.Ticks, info.Length);
                }
                catch
                {
                    // Ignore transient IO error
                }
            }
        }

        return result;
    }

    private void ScanDirectory(string rootDir, Dictionary<string, (long, long)> result)
    {
        if (!Directory.Exists(rootDir))
        {
            return;
        }

        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (!IsIgnoredDirectory(name))
                {
                    stack.Push(sub);
                }
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsRelevantFileName(file))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(file);
                    result[info.FullName] = (info.LastWriteTimeUtc.Ticks, info.Length);
                }
                catch
                {
                    // File may have been transiently deleted/locked during scan
                }
            }
        }
    }

    private bool IsTrackedFile(string fullPath)
    {
        if (!IsRelevantFileName(fullPath))
        {
            return false;
        }

        foreach (var root in _rootDirectories)
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }

            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (IsIgnoredDirectory(segments[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private bool IsIgnoredDirectory(string name)
    {
        if (_watchBinaries && name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IgnoredDirectories.Contains(name);
    }

    private bool IsRelevantFileName(string path)
    {
        var ext = Path.GetExtension(path);
        if (WatchedExtensions.Contains(ext))
        {
            return true;
        }

        if (_watchBinaries && (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        return fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnFileEvent;
                watcher.Created -= OnFileEvent;
                watcher.Deleted -= OnFileEvent;
                watcher.Renamed -= OnRenamedEvent;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
        _watchers.Clear();
    }
}
