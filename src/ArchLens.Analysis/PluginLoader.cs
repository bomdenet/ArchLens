using System.Reflection;
using System.Runtime.Loader;
using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Dynamically discovers and loads user plugin assemblies from .archlens/plugins/ using isolated,
/// collectible AssemblyLoadContexts while sharing ArchLens.Model interface identities with the host.
/// Supports IArchitectureRule, ICustomMetricCalculator, and IEntryPointDetector extensions.
/// </summary>
public static class PluginLoader
{
    public const string DefaultRelativePluginsDir = ".archlens/plugins";

    private static readonly HashSet<string> HostAssemblyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArchLens.Model",
        "ArchLens.Analysis",
        "ArchLens.Extraction.",
        "ArchLens.Rules.",
        "ArchLens.Query",
        "ArchLens.Mcp",
        "ArchLens.Cli"
    };

    public static string? DiscoverPluginsDirectory(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var full = Path.GetFullPath(targetPath);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, ".archlens", "plugins");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                break;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    public static LoadedPlugins LoadFromDirectory(string? pluginsDirectory, bool allowUnmanagedDlls = false)
    {
        if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
        {
            return LoadedPlugins.Empty;
        }

        var rules = new List<IArchitectureRule>();
        var metricCalculators = new List<ICustomMetricCalculator>();
        var entryPointDetectors = new List<IEntryPointDetector>();
        var loadedFiles = new List<string>();
        var diagnostics = new List<string>();
        var contexts = new List<PluginAssemblyLoadContext>();

        var dlls = Directory.GetFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dllPath in dlls)
        {
            var fileName = Path.GetFileNameWithoutExtension(dllPath);
            if (IsHostAssemblyName(fileName))
            {
                continue;
            }

            PluginAssemblyLoadContext? ctx = null;
            try
            {
                var fullDllPath = Path.GetFullPath(dllPath);
                ctx = new PluginAssemblyLoadContext(fullDllPath, allowUnmanagedDlls);
                var assembly = LoadAssemblyWithoutLocking(ctx, fullDllPath);

                Type[] exportedTypes;
                try
                {
                    exportedTypes = assembly.GetExportedTypes();
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    exportedTypes = rtle.Types.Where(t => t is not null).Cast<Type>().ToArray();
                    foreach (var loaderEx in rtle.LoaderExceptions.Where(e => e is not null))
                    {
                        diagnostics.Add($"Plugin '{Path.GetFileName(dllPath)}' partial type load warning: {loaderEx!.Message}");
                    }
                }

                // Stage per-assembly extensions before committing so an assembly-level failure never leaves
                // live instances pointing to an unloaded ALC.
                var asmRules = new List<IArchitectureRule>();
                var asmMetrics = new List<ICustomMetricCalculator>();
                var asmDetectors = new List<IEntryPointDetector>();

                foreach (var type in exportedTypes)
                {
                    if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                    {
                        continue;
                    }

                    var isRule = typeof(IArchitectureRule).IsAssignableFrom(type);
                    var isMetric = typeof(ICustomMetricCalculator).IsAssignableFrom(type);
                    var isDetector = typeof(IEntryPointDetector).IsAssignableFrom(type);
                    if (!isRule && !isMetric && !isDetector)
                    {
                        continue;
                    }

                    if (type.GetConstructor(Type.EmptyTypes) is null)
                    {
                        diagnostics.Add($"Plugin type '{type.FullName}' in '{Path.GetFileName(dllPath)}' skipped: requires a public parameterless constructor.");
                        continue;
                    }

                    try
                    {
                        var instance = Activator.CreateInstance(type);
                        if (instance is IArchitectureRule rule)
                        {
                            asmRules.Add(rule);
                        }
                        if (instance is ICustomMetricCalculator calc)
                        {
                            asmMetrics.Add(calc);
                        }
                        if (instance is IEntryPointDetector detector)
                        {
                            asmDetectors.Add(detector);
                        }
                    }
                    catch (Exception typeEx)
                    {
                        var inner = typeEx is TargetInvocationException tie && tie.InnerException is not null
                            ? tie.InnerException
                            : typeEx;
                        diagnostics.Add($"Plugin type '{type.FullName}' in '{Path.GetFileName(dllPath)}' failed to instantiate: {inner.Message}");
                    }
                }

                if (asmRules.Count > 0 || asmMetrics.Count > 0 || asmDetectors.Count > 0)
                {
                    rules.AddRange(asmRules);
                    metricCalculators.AddRange(asmMetrics);
                    entryPointDetectors.AddRange(asmDetectors);
                    contexts.Add(ctx);
                    loadedFiles.Add(Path.GetFileName(dllPath));
                }
                else
                {
                    ctx.Unload();
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Failed to load plugin '{Path.GetFileName(dllPath)}': {ex.Message}");
                try { ctx?.Unload(); } catch { }
            }
        }

        return new LoadedPlugins(rules, metricCalculators, entryPointDetectors, loadedFiles, diagnostics, contexts);
    }

    private static Assembly LoadAssemblyWithoutLocking(AssemblyLoadContext ctx, string fullDllPath)
    {
        var dllBytes = File.ReadAllBytes(fullDllPath);
        using var dllStream = new MemoryStream(dllBytes, writable: false);

        var pdbPath = Path.ChangeExtension(fullDllPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            var pdbBytes = File.ReadAllBytes(pdbPath);
            using var pdbStream = new MemoryStream(pdbBytes, writable: false);
            return ctx.LoadFromStream(dllStream, pdbStream);
        }

        return ctx.LoadFromStream(dllStream);
    }

    internal static bool IsHostAssemblyName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        HostAssemblyPrefixes.Any(prefix =>
            name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            (prefix.EndsWith('.') && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    internal sealed class PluginAssemblyLoadContext(string pluginPath, bool allowUnmanagedDlls)
        : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(Path.GetFullPath(pluginPath));

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share host ArchLens assemblies from the default ALC so plugin implementations
            // are assignable to the host's IArchitectureRule / ICustomMetricCalculator / IEntryPointDetector.
            if (IsHostAssemblyName(assemblyName.Name))
            {
                return null;
            }

            var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolvedPath is not null ? LoadAssemblyWithoutLocking(this, resolvedPath) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            if (!allowUnmanagedDlls)
            {
                return nint.Zero;
            }

            var resolvedPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return resolvedPath is not null ? LoadUnmanagedDllFromPath(resolvedPath) : nint.Zero;
        }
    }
}

public sealed class LoadedPlugins : IDisposable
{
    public static readonly LoadedPlugins Empty = new([], [], [], [], [], [], isSharedEmpty: true);

    private readonly List<IArchitectureRule> _rules;
    private readonly List<ICustomMetricCalculator> _metricCalculators;
    private readonly List<IEntryPointDetector> _entryPointDetectors;
    private readonly List<PluginLoader.PluginAssemblyLoadContext> _contexts;
    private readonly bool _isSharedEmpty;
    private int _disposed;

    internal LoadedPlugins(
        List<IArchitectureRule> rules,
        List<ICustomMetricCalculator> metricCalculators,
        List<IEntryPointDetector> entryPointDetectors,
        IReadOnlyList<string> loadedPluginFiles,
        IReadOnlyList<string> diagnostics,
        List<PluginLoader.PluginAssemblyLoadContext> contexts,
        bool isSharedEmpty = false)
    {
        _rules = rules;
        _metricCalculators = metricCalculators;
        _entryPointDetectors = entryPointDetectors;
        LoadedPluginFiles = loadedPluginFiles;
        Diagnostics = diagnostics;
        _contexts = contexts;
        _isSharedEmpty = isSharedEmpty;
    }

    public IReadOnlyList<IArchitectureRule> Rules => _rules;
    public IReadOnlyList<ICustomMetricCalculator> MetricCalculators => _metricCalculators;
    public IReadOnlyList<IEntryPointDetector> EntryPointDetectors => _entryPointDetectors;
    public IReadOnlyList<string> LoadedPluginFiles { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public bool HasAny => _rules.Count > 0 || _metricCalculators.Count > 0 || _entryPointDetectors.Count > 0;

    public void Dispose()
    {
        if (_isSharedEmpty)
        {
            return;
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var disposedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in _rules.Cast<object>().Concat(_metricCalculators).Concat(_entryPointDetectors))
        {
            if (item is IDisposable d && disposedObjects.Add(item))
            {
                try { d.Dispose(); } catch { }
            }
        }

        // Release strong references to plugin instances so collectible ALCs can be reclaimed by GC.
        _rules.Clear();
        _metricCalculators.Clear();
        _entryPointDetectors.Clear();

        foreach (var ctx in _contexts)
        {
            try { ctx.Unload(); } catch { }
        }
        _contexts.Clear();
    }
}
