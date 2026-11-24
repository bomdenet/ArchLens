using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

MsBuildBootstrapper.EnsureRegistered();

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL for MCP over stdio: all logs must go to stderr so stdout carries only JSON-RPC frames.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton<ICodeGraphExtractor, RoslynCodeGraphExtractor>();
builder.Services.AddSingleton<ICodeGraphExtractor, MetadataCodeGraphExtractor>();
builder.Services.AddSingleton<IPersistentGraphCache>(_ => new SqliteGraphCache());
builder.Services.AddSingleton<WorkspaceSessionManager>();
builder.Services.AddSingleton<MetricsEngine>();
builder.Services.AddSingleton<ArchitectureAnalyzerService>();
builder.Services.AddSingleton<GraphSqlQueryEngine>();
builder.Services.AddSingleton<IRuleSetProvider, YamlRuleSetProvider>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ArchLens",
            Version = "0.5.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
