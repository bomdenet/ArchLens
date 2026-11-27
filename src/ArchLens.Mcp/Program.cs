using System.Reflection;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Mcp;
using ArchLens.Mcp.Feedback;
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

// Feedback delivery is opt-in (ARCHLENS_FEEDBACK_TOKEN / GITHUB_TOKEN / ARCHLENS_FEEDBACK_URL); without one the
// give_feedback tool reports "disabled" and writes nothing.
var feedbackOptions = FeedbackOptions.FromEnvironment();
builder.Services.AddSingleton(feedbackOptions);
builder.Services.AddSingleton<FeedbackOutbox>();
builder.Services.AddSingleton<IFeedbackSender>(_ =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    HttpFeedbackSender.ConfigureClient(http, serverVersion, feedbackOptions.Token);
    return new HttpFeedbackSender(feedbackOptions, http);
});
builder.Services.AddSingleton(serviceProvider => new FeedbackService(
    feedbackOptions,
    serviceProvider.GetRequiredService<IFeedbackSender>(),
    serviceProvider.GetRequiredService<FeedbackOutbox>()));
builder.Services.AddHostedService<FeedbackOutboxFlusher>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ArchLens",
            Version = serverVersion
        };
    })
    .WithRequestFilters(filters =>
    {
        var toolAllowedParams = BuildAllowedToolParameterMap(typeof(ArchLensTools), typeof(FeedbackTools));
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            if (result.Tools is { Count: > 0 })
            {
                foreach (var tool in result.Tools)
                {
                    if (tool.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        !tool.InputSchema.TryGetProperty("additionalProperties", out _))
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(tool.InputSchema.GetRawText())?.AsObject();
                        if (node is not null)
                        {
                            node["additionalProperties"] = false;
                            tool.InputSchema = System.Text.Json.JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
                        }
                    }
                }
            }
            return result;
        });
        filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? "tool";
            if (context.Params?.Arguments is { Count: > 0 } providedArgs &&
                toolAllowedParams.TryGetValue(toolName, out var allowedParams))
            {
                var unknown = providedArgs.Keys
                    .Where(k => !allowedParams.ContainsKey(k))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
                if (unknown.Count > 0)
                {
                    var supported = string.Join(", ", allowedParams.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    return McpResponseFormatter.Error(
                        "invalid_argument",
                        $"Unknown parameter(s) for '{toolName}': {string.Join(", ", unknown)}. Supported parameters: {supported}.",
                        hint: "Parameter names are case-sensitive camelCase. Inspect tools/list for exact names.");
                }

                // Enforce strict JSON value types matching tools/list schema (prevent stringified integers/booleans or null for non-nullable params)
                Dictionary<string, System.Text.Json.JsonElement>? normalizedIntegralArgs = null;
                foreach (var (paramName, jsonVal) in providedArgs)
                {
                    if (!allowedParams.TryGetValue(paramName, out var paramInfo)) continue;
                    var coreType = Nullable.GetUnderlyingType(paramInfo.ParamType) ?? paramInfo.ParamType;
                    var isNonNullableValueType = paramInfo.ParamType.IsValueType && Nullable.GetUnderlyingType(paramInfo.ParamType) is null;
                    if (jsonVal.ValueKind == System.Text.Json.JsonValueKind.Null)
                    {
                        if (paramInfo.IsRequired)
                        {
                            return McpResponseFormatter.Error(
                                "invalid_argument",
                                $"Parameter '{paramName}' on '{toolName}' is required and cannot be null.",
                                hint: "Pass a non-null value matching the parameter type in tools/list.");
                        }
                        if (isNonNullableValueType)
                        {
                            var expectedKind = coreType == typeof(int) ? "JSON integer" : coreType == typeof(bool) ? "JSON boolean" : coreType.Name;
                            return McpResponseFormatter.Error(
                                "invalid_argument",
                                $"Invalid type for parameter '{paramName}' on '{toolName}': expected {expectedKind} (cannot be null; omit the parameter to use its default).",
                                hint: $"Omit '{paramName}' or pass a valid {expectedKind}.");
                        }
                        continue;
                    }
                    if (coreType == typeof(int))
                    {
                        if (jsonVal.ValueKind != System.Text.Json.JsonValueKind.Number)
                        {
                            return McpResponseFormatter.Error(
                                "invalid_argument",
                                $"Invalid type for parameter '{paramName}' on '{toolName}': expected JSON integer, got {jsonVal.ValueKind.ToString().ToLowerInvariant()}.",
                                hint: "Pass a 32-bit JSON integer (not a quoted string) for integer parameters.");
                        }
                        if (!jsonVal.TryGetInt32(out _))
                        {
                            var rawText = jsonVal.GetRawText();
                            if (jsonVal.TryGetDecimal(out var decVal))
                            {
                                if (decVal != decimal.Truncate(decVal))
                                {
                                    return McpResponseFormatter.Error(
                                        "invalid_argument",
                                        $"Invalid type for parameter '{paramName}' on '{toolName}': expected JSON integer, got non-integer number '{rawText}'.",
                                        hint: "Pass a whole 32-bit JSON integer for integer parameters.");
                                }
                                if (decVal < int.MinValue || decVal > int.MaxValue)
                                {
                                    return McpResponseFormatter.Error(
                                        "invalid_argument",
                                        $"Value '{rawText}' for parameter '{paramName}' on '{toolName}' is out of 32-bit integer range ({int.MinValue}..{int.MaxValue}).",
                                        hint: "Pass a 32-bit signed integer within range.");
                                }
                                normalizedIntegralArgs ??= new Dictionary<string, System.Text.Json.JsonElement>(providedArgs);
                                normalizedIntegralArgs[paramName] = System.Text.Json.JsonSerializer.SerializeToElement((int)decVal);
                            }
                            else if (jsonVal.TryGetDouble(out var dblVal) && double.IsFinite(dblVal) && Math.Truncate(dblVal) == dblVal)
                            {
                                return McpResponseFormatter.Error(
                                    "invalid_argument",
                                    $"Value '{rawText}' for parameter '{paramName}' on '{toolName}' is out of 32-bit integer range ({int.MinValue}..{int.MaxValue}).",
                                    hint: "Pass a 32-bit signed integer within range.");
                            }
                            else
                            {
                                return McpResponseFormatter.Error(
                                    "invalid_argument",
                                    $"Invalid type for parameter '{paramName}' on '{toolName}': expected JSON integer, got non-integer number '{rawText}'.",
                                    hint: "Pass a 32-bit JSON integer for integer parameters.");
                            }
                        }
                    }
                    if (coreType == typeof(bool) &&
                        jsonVal.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                    {
                        return McpResponseFormatter.Error(
                            "invalid_argument",
                            $"Invalid type for parameter '{paramName}' on '{toolName}': expected JSON boolean, got {jsonVal.ValueKind.ToString().ToLowerInvariant()}.",
                            hint: "Pass a JSON boolean (true or false) for boolean parameters.");
                    }
                    if (coreType == typeof(string) && jsonVal.ValueKind != System.Text.Json.JsonValueKind.String)
                    {
                        return McpResponseFormatter.Error(
                            "invalid_argument",
                            $"Invalid type for parameter '{paramName}' on '{toolName}': expected JSON string, got {jsonVal.ValueKind.ToString().ToLowerInvariant()}.",
                            hint: "Pass a JSON string for string parameters.");
                    }
                }
                if (normalizedIntegralArgs is not null && context.Params is not null)
                {
                    context.Params.Arguments = normalizedIntegralArgs;
                }
            }

            try
            {
                return await next(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or FormatException)
            {
                return McpResponseFormatter.Error(
                    "invalid_argument",
                    $"Invalid arguments for '{toolName}': {McpResponseFormatter.CleanExceptionMessage(ex)}",
                    hint: "Check the required parameter names and JSON value types in tools/list and try again.");
            }
        });
        filters.AddGetPromptFilter(next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or FormatException)
            {
                var promptName = context.Params?.Name ?? "prompt";
                throw new ModelContextProtocol.McpProtocolException(
                    $"Invalid arguments for prompt '{promptName}': {McpResponseFormatter.CleanExceptionMessage(ex)}",
                    ModelContextProtocol.McpErrorCode.InvalidParams);
            }
        });
        filters.AddReadResourceFilter(next => async (context, cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(context.Params?.Uri))
            {
                throw new ModelContextProtocol.McpProtocolException(
                    "Missing required parameter 'uri' for resources/read.",
                    ModelContextProtocol.McpErrorCode.InvalidParams);
            }
            return await next(context, cancellationToken);
        });
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();

static Dictionary<string, Dictionary<string, (Type ParamType, bool IsRequired)>> BuildAllowedToolParameterMap(params Type[] toolTypes)
{
    var nullabilityCtx = new NullabilityInfoContext();
    var map = new Dictionary<string, Dictionary<string, (Type ParamType, bool IsRequired)>>(StringComparer.Ordinal);
    foreach (var type in toolTypes)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var attr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attr is null || string.IsNullOrWhiteSpace(attr.Name))
            {
                continue;
            }

            var paramMap = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken) && !string.IsNullOrWhiteSpace(p.Name))
                .ToDictionary(
                    p => p.Name!,
                    p =>
                    {
                        var isRequired = !p.HasDefaultValue &&
                            Nullable.GetUnderlyingType(p.ParameterType) is null &&
                            nullabilityCtx.Create(p).ReadState != NullabilityState.Nullable;
                        return (p.ParameterType, isRequired);
                    },
                    StringComparer.Ordinal);
            map[attr.Name] = paramMap;
        }
    }
    return map;
}
