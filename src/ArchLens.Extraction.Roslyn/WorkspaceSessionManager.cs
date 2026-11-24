using System.Collections.Concurrent;
using ArchLens.Model;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Thread-safe stateful cache of extracted ArchitectureSnapshots.
/// Keeps solutions warm in memory for MCP agents and tracks baselines for before/after refactoring diffs.
/// </summary>
public sealed class WorkspaceSessionManager
{
    private readonly IReadOnlyList<ICodeGraphExtractor> _extractors;
    private readonly ConcurrentDictionary<string, ArchitectureSnapshot> _currentSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ArchitectureSnapshot> _baselineSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

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
        var key = NormalizeKey(targetPath, options);
        if (!forceRefresh && _currentSnapshots.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _currentSnapshots.TryGetValue(key, out cached))
            {
                return cached;
            }

            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(targetPath))
                ?? throw new NotSupportedException($"No registered extractor can handle target '{targetPath}'.");

            var snapshot = await extractor.ExtractAsync(targetPath, options, cancellationToken);
            _currentSnapshots[key] = snapshot;
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

    private static string NormalizeKey(string targetPath, ExtractionOptions? options)
    {
        var full = Path.GetFullPath(targetPath);
        var opt = options ?? new ExtractionOptions();
        return $"{full}|tests={opt.IncludeTestProjects}|gen={opt.IncludeGeneratedCode}";
    }
}
