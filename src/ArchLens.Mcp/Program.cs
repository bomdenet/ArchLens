using System.Reflection;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Mcp;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

MsBuildBootstrapper.EnsureRegistered();

var allowPlugins = args.Any(a => a.Equals("--allow-plugins", StringComparison.OrdinalIgnoreCase)) ||
                   string.Equals(Environment.GetEnvironmentVariable("ARCHLENS_ALLOW_PLUGINS"), "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Environment.GetEnvironmentVariable("ARCHLENS_ALLOW_PLUGINS"), "true", StringComparison.OrdinalIgnoreCase);

string? explicitPluginsDir = Environment.GetEnvironmentVariable("ARCHLENS_PLUGINS_DIR");
var hostArgs = new List<string>(args.Length);
for (var i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--allow-plugins", StringComparison.OrdinalIgnoreCase))
    {
        allowPlugins = true;
        continue;
    }

    if (args[i].Equals("--plugins", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        explicitPluginsDir = args[++i];
        allowPlugins = true;
        continue;
    }

    hostArgs.Add(args[i]);
}

var builder = Host.CreateApplicationBuilder(hostArgs.ToArray());

// CRITICAL for MCP over stdio: all logs must go to stderr so stdout carries only JSON-RPC frames.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(new PluginHostOptions(allowPlugins, explicitPluginsDir));
builder.Services.AddSingleton<ICodeGraphExtractor, RoslynCodeGraphExtractor>();
builder.Services.AddSingleton<ICodeGraphExtractor, MetadataCodeGraphExtractor>();
builder.Services.AddSingleton<IPersistentGraphCache>(_ => new SqliteGraphCache());
builder.Services.AddSingleton<WorkspaceSessionManager>();
builder.Services.AddSingleton<MetricsEngine>();
builder.Services.AddSingleton<ArchitectureAnalyzerService>();
builder.Services.AddSingleton<GraphSqlQueryEngine>();
builder.Services.AddSingleton<IRuleSetProvider, YamlRuleSetProvider>();

var serverVersion = typeof(ArchLensTools).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
    ?? typeof(ArchLensTools).Assembly.GetName().Version?.ToString(3)
    ?? "0.6.0";

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ArchLens",
            Version = serverVersion
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
