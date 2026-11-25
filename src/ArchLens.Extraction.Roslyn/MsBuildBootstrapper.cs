using System.Globalization;
using Microsoft.Build.Locator;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Ensures MSBuildLocator is registered exactly once per process before any Roslyn MSBuildWorkspace types are JIT-loaded,
/// and configures invariant UI culture so MSBuild/Roslyn diagnostics stay deterministic across locales.
/// </summary>
public static class MsBuildBootstrapper
{
    private static readonly object SyncLock = new();
    private static bool _initialized;

    public static void EnsureRegistered()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncLock)
        {
            if (_initialized)
            {
                return;
            }

            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            _initialized = true;
        }
    }
}
