using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Model;
using ModelContextProtocol.Protocol;

namespace ArchLens.Mcp;

/// <summary>
/// Formats ArchLens analysis reports into compact, token-efficient JSON payloads and structured MCP error results.
/// Keeps field projection, pagination, deduplication, and error classification cleanly separated from MCP tool wiring.
/// </summary>
internal static class McpResponseFormatter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static CallToolResult Ok<T>(T payload) =>
        new()
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }]
        };

    public static CallToolResult OkRaw(string text) =>
        new()
        {
            Content = [new TextContentBlock { Text = text }]
        };

    public static CallToolResult Error(string code, string message, string? path = null, string? hint = null) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        error = code,
                        message,
                        path,
                        hint
                    }, JsonOptions)
                }
            ]
        };

    public static CallToolResult ErrorWithCandidates(
        string code,
        string message,
        IReadOnlyList<string> candidates,
        string? path = null,
        string? hint = null) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        error = code,
                        message,
                        candidates,
                        path,
                        hint
                    }, JsonOptions)
                }
            ]
        };

    public static CallToolResult FromException(Exception ex, string? path = null)
    {
        var cleanMessage = CleanExceptionMessage(ex);
        var (code, hint) = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => (
                "path_not_found",
                "Pass an existing .slnx, .sln, .csproj, .dll, .exe file or a directory containing one."),
            NotSupportedException when cleanMessage.Contains("No rule provider", StringComparison.OrdinalIgnoreCase) => (
                "invalid_argument",
                "Pass a .yaml or .yml file to rulesPath, or omit rulesPath to auto-discover .archlens.yaml or use a preset."),
            NotSupportedException when cleanMessage.Contains("C# source target", StringComparison.OrdinalIgnoreCase) => (
                "unsupported_target",
                "Pass a .slnx, .sln, .csproj, or C# source directory (this tool requires Roslyn source extraction and cannot run on compiled .dll/.exe binaries)."),
            NotSupportedException when cleanMessage.Contains("Roslyn source extraction", StringComparison.OrdinalIgnoreCase) => (
                "unsupported_target",
                "Pass backend 'roslyn' or 'auto' with a C# source target (.slnx, .sln, .csproj, or source directory)."),
            NotSupportedException => (
                "unsupported_target",
                "Pass a .slnx, .sln, .csproj, .dll, .exe file (or directory), or use backend 'auto', 'roslyn', or 'il'."),
            ArgumentException when cleanMessage.Contains("Parameter 'path' is required", StringComparison.OrdinalIgnoreCase) => (
                "invalid_argument",
                "Pass 'path' to query graph tables, or omit 'path' only when using includeSchema: true or querying 'schema_info'."),
            ArgumentException when cleanMessage.Contains("Parameter 'sql' is required", StringComparison.OrdinalIgnoreCase) ||
                                   cleanMessage.Contains("Provide 'path' and 'sql'", StringComparison.OrdinalIgnoreCase) => (
                "invalid_argument",
                "Provide 'path' and a read-only 'sql' SELECT/WITH query, or set includeSchema: true to inspect tables and columns."),
            ArgumentException when cleanMessage.Contains("SQL", StringComparison.OrdinalIgnoreCase) => (
                "invalid_argument",
                "Check SQL syntax (must start with SELECT, WITH, or EXPLAIN) or pass includeSchema: true to inspect tables and columns."),
            ArgumentException => (
                "invalid_argument",
                "Check the tool parameter values and try again."),
            TimeoutException => (
                "timeout",
                "Simplify the query or reduce the requested scope/limit."),
            InvalidOperationException when cleanMessage.Contains("No built .NET assemblies", StringComparison.OrdinalIgnoreCase) => (
                "assemblies_not_built",
                "Run 'dotnet build' on the target solution/project first, or pass backend: 'roslyn'."),
            InvalidDataException when cleanMessage.StartsWith("Rules file not found:", StringComparison.OrdinalIgnoreCase) => (
                "rules_file_not_found",
                "Pass an existing .archlens.yaml/.yml file to rulesPath, or omit rulesPath to auto-discover or use a preset."),
            InvalidDataException when cleanMessage.StartsWith("Invalid baseline", StringComparison.OrdinalIgnoreCase) => (
                "invalid_operation",
                "Pass a valid .archlens-baseline.json file generated by 'diff(saveBaseline: true)' or 'archlens baseline'."),
            InvalidDataException when cleanMessage.Contains("YAML", StringComparison.OrdinalIgnoreCase) || cleanMessage.Contains("rule definition", StringComparison.OrdinalIgnoreCase) => (
                "invalid_operation",
                "Check archlens://rules/reference for valid YAML rule types and required fields."),
            InvalidOperationException when cleanMessage.Contains("circular project reference", StringComparison.OrdinalIgnoreCase) => (
                "invalid_operation",
                "Remove one of the circular <ProjectReference> entries between the listed projects and retry."),
            InvalidOperationException when cleanMessage.Contains("SQLite", StringComparison.OrdinalIgnoreCase) || cleanMessage.Contains("SQL", StringComparison.OrdinalIgnoreCase) => (
                "invalid_operation",
                "Pass includeSchema: true or query 'SELECT * FROM schema_info' to inspect available tables and columns."),
            System.Xml.XmlException => (
                "invalid_operation",
                "Fix the XML syntax error in the target .slnx/.csproj or MSBuild props/targets file and retry."),
            InvalidOperationException or InvalidDataException => (
                "invalid_operation",
                "Inspect the error message and input files, then retry."),
            _ => (
                "internal_error",
                "Check server logs or retry with refresh: true.")
        };

        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        error = code,
                        message = cleanMessage,
                        exceptionType = code == "internal_error" ? ex.GetType().Name : null,
                        path,
                        hint
                    }, JsonOptions)
                }
            ]
        };
    }

    public static string CleanExceptionMessage(Exception ex)
    {
        var msg = ex.Message;

        // Strip .NET ArgumentException "(Parameter 'foo')" suffix
        if (ex is ArgumentException argEx && !string.IsNullOrEmpty(argEx.ParamName))
        {
            var suffix = $" (Parameter '{argEx.ParamName}')";
            if (msg.EndsWith(suffix, StringComparison.Ordinal))
            {
                msg = msg[..^suffix.Length];
            }
        }

        // Simplify MSBuildWorkspace circular ProjectReference errors that expose internal ProjectId GUIDs
        if (msg.Contains("will cause a circular reference", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("ProjectId", StringComparison.Ordinal))
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                msg, @"\(ProjectId,\s*#[0-9a-fA-F\-]+\s*-\s*([^)]+)\)");
            if (matches.Count >= 2)
            {
                var p1 = Path.GetFileName(matches[0].Groups[1].Value.Trim());
                var p2 = Path.GetFileName(matches[1].Groups[1].Value.Trim());
                return $"Circular project reference detected between '{p1}' and '{p2}'. MSBuild cannot load solutions with circular <ProjectReference> chains.";
            }
        }

        return msg;
    }

    public static object FormatOverview(
        SolutionOverviewReport report,
        bool summaryOnly,
        bool includePackages,
        bool includeFullDiagnostics)
    {
        var projects = summaryOnly
            ? null
            : report.Projects.Select(p => new
            {
                name = p.Name,
                filePath = p.FilePath,
                targetFramework = p.TargetFramework,
                projectReferences = p.ProjectReferences.Count > 0 ? p.ProjectReferences : null,
                packageReferences = includePackages && p.PackageReferences.Count > 0 ? p.PackageReferences : null,
                packageCount = !includePackages && p.PackageReferences.Count > 0 ? (int?)p.PackageReferences.Count : null,
                typeCount = p.TypeCount,
                documentCount = p.DocumentCount
            }).ToList();

        var summaryPackages = summaryOnly && includePackages
            ? report.Projects
                .SelectMany(p => p.PackageReferences)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;

        var diagnostics = (summaryOnly && !includeFullDiagnostics) || report.Diagnostics.Count == 0
            ? null
            : report.Diagnostics
                .Select(d => new
                {
                    severity = d.Severity,
                    source = d.Source,
                    message = includeFullDiagnostics ? d.Message : SummarizeDiagnosticMessage(d.Message)
                })
                .Distinct()
                .ToList();

        return new
        {
            snapshotId = report.SnapshotId,
            targetPath = report.TargetPath,
            capturedAt = report.CapturedAt,
            loadStatus = report.LoadStatus,
            projectCount = report.ProjectCount,
            namespaceCount = report.NamespaceCount,
            typeCount = report.TypeCount,
            methodCount = report.MethodCount,
            methodsIncluded = report.MethodsIncluded,
            dependencyEdgeCount = report.DependencyEdgeCount,
            projectCycleCount = report.ProjectCycleCount,
            namespaceCycleCount = report.NamespaceCycleCount,
            namespaceCycleCountRaw = report.NamespaceCycleCountRaw,
            suppressedNamespaceCycles = report.SuppressedNamespaceCycles is { Count: > 0 } ? report.SuppressedNamespaceCycles : null,
            projects,
            packages = summaryPackages,
            topNamespaces = summaryOnly ? null : report.TopNamespaces,
            returnedNamespaceCount = summaryOnly ? (int?)null : report.TopNamespaces.Count,
            topNamespacesTruncated = summaryOnly ? (bool?)null : report.TopNamespacesTruncated,
            diagnosticCount = report.Diagnostics.Count > 0 ? (int?)report.Diagnostics.Count : null,
            diagnostics
        };
    }

    public static object FormatDependencyGraph(
        DependencyGraphReport report,
        string format,
        bool includeCycles,
        GraphLoadStatus? loadStatus,
        int? requestedMaxEdges = null)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "compact" : format.Trim().ToLowerInvariant();
        var nodeIndex = new Dictionary<string, int>(report.Nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < report.Nodes.Count; i++)
        {
            nodeIndex[report.Nodes[i]] = i;
        }

        object? edgesPayload = normalizedFormat switch
        {
            "names" => null,
            "full" or "objects" => report.Edges,
            _ => report.Edges
                .Where(e => nodeIndex.ContainsKey(e.From) && nodeIndex.ContainsKey(e.To))
                .Select(e => new[] { nodeIndex[e.From], nodeIndex[e.To] })
                .ToList()
        };

        var edgeFormat = normalizedFormat switch
        {
            "names" => null,
            "full" or "objects" => "objects",
            _ => "nodeIndexPairs"
        };

        var totalCycles = report.TotalCycleCount ?? report.Cycles.Count;
        var cyclesTruncated = includeCycles && totalCycles > report.Cycles.Count;

        var isNamesOnly = normalizedFormat == "names";
        var effectiveTruncated = isNamesOnly
            ? (report.Truncated && !(report.EdgesTruncated == true && report.ReturnedNodeCount == report.TotalNodesInScope)) || cyclesTruncated
            : report.Truncated || cyclesTruncated;
        var suppressedByZeroBudget = requestedMaxEdges == 0;
        var noteParts = new List<string>();
        if (!isNamesOnly && !suppressedByZeroBudget && report.Edges.Count == 0 && (report.TotalEdgeCount ?? 0) > 0)
        {
            noteParts.Add($"0 edges connect nodes strictly within the returned {report.ReturnedNodeCount}-node window (totalEdgeCount={report.TotalEdgeCount} in full scope); increase maxNodes or narrow 'filter' to see edges.");
        }
        if (includeCycles && report.Cycles.Count > 0 && report.ReturnedNodeCount < report.TotalNodesInScope &&
            report.Cycles.Any(c => c.Nodes.Any(n => !nodeIndex.ContainsKey(n))))
        {
            noteParts.Add($"Some returned cycles include nodes outside the truncated {report.ReturnedNodeCount}-node window (totalNodesInScope={report.TotalNodesInScope}); increase maxNodes to list all cycle members in 'nodes'.");
        }
        if (!string.IsNullOrWhiteSpace(report.Note))
        {
            noteParts.Add(report.Note);
        }
        var combinedNote = noteParts.Count > 0 ? string.Join(" ", noteParts) : null;
        return new
        {
            scope = report.Scope,
            format = normalizedFormat is "names" or "full" or "objects" ? normalizedFormat : "compact",
            edgeFormat,
            totalNodesInScope = report.TotalNodesInScope,
            totalNodesBeforeFilter = report.TotalNodesBeforeFilter,
            returnedNodeCount = report.ReturnedNodeCount,
            totalEdgeCount = isNamesOnly ? (int?)null : (report.TotalEdgeCount ?? report.Edges.Count),
            returnedEdgeCount = isNamesOnly ? (int?)null : (report.ReturnedEdgeCount ?? report.Edges.Count),
            edgesTruncated = !isNamesOnly && report.EdgesTruncated == true ? (bool?)true : null,
            totalCycleCount = totalCycles,
            cyclesTruncated = cyclesTruncated ? (bool?)true : null,
            suppressedCycles = report.SuppressedCycles is { Count: > 0 } ? report.SuppressedCycles : null,
            truncated = effectiveTruncated,
            note = combinedNote,
            loadStatus = FormatCompactLoadStatus(loadStatus),
            cycles = includeCycles && report.Cycles.Count > 0 ? report.Cycles : null,
            nodes = report.Nodes,
            edges = edgesPayload
        };
    }

    public static object FormatHotspots(
        CodeGraph graph,
        HotspotsReport report,
        int offset,
        int topN,
        string? fields,
        bool summaryOnly)
    {
        var allItems = report.Items;
        var safeOffset = Math.Max(0, offset);
        var paged = summaryOnly ? [] : allItems.Skip(safeOffset).Take(topN).ToList();
        var truncated = !summaryOnly && (safeOffset + paged.Count < allItems.Count);
        var parsedFields = ParseFields(fields);
        var includeAll = parsedFields is { Count: > 0 } && parsedFields.Contains("all");

        var itemsPayload = summaryOnly
            ? null
            : paged.Select(m => ProjectHotspotItem(m, report.SortBy, parsedFields, includeAll)).ToList();

        return new
        {
            scope = report.Scope,
            sortBy = report.SortBy,
            totalEvaluated = report.TotalEvaluated,
            matchedCount = allItems.Count,
            returned = summaryOnly ? (int?)null : paged.Count,
            offset = safeOffset > 0 ? (int?)safeOffset : null,
            nextOffset = truncated ? (int?)(safeOffset + paged.Count) : null,
            truncated,
            note = report.Note,
            loadStatus = FormatCompactLoadStatus(graph.LoadStatus),
            items = itemsPayload
        };
    }

    public static object FormatDiAnalysis(
        DiAnalysisReport report,
        GraphLoadStatus? loadStatus,
        bool includeRegistrations,
        bool summaryOnly,
        int offset,
        int limit,
        string? lifetime = null,
        string? project = null,
        string? serviceType = null,
        bool onlyCaptive = false,
        bool onlyUnregistered = false,
        CodeGraph? graph = null)
    {
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 200);

        bool MatchesProject(string? typeId)
        {
            if (string.IsNullOrWhiteSpace(project) || graph is null) return true;
            var proj = typeId is not null ? graph.FindNode(typeId)?.ProjectName : null;
            return proj is not null && (proj.Equals(project, StringComparison.OrdinalIgnoreCase) || GlobMatcher.IsMatch(proj, project));
        }

        bool MatchesService(string? typeId)
        {
            if (string.IsNullOrWhiteSpace(serviceType)) return true;
            return typeId is not null &&
                   (typeId.Contains(serviceType, StringComparison.OrdinalIgnoreCase) ||
                    GlobMatcher.IsMatch(typeId, serviceType));
        }

        ServiceLifetimeKind? parsedLifetimeFilter = null;
        if (!string.IsNullOrWhiteSpace(lifetime) && Enum.TryParse<ServiceLifetimeKind>(lifetime.Trim(), ignoreCase: true, out var parsedLifetime))
        {
            parsedLifetimeFilter = parsedLifetime;
        }

        var filteredRegs = report.Registrations.AsEnumerable();
        if (parsedLifetimeFilter.HasValue)
        {
            filteredRegs = filteredRegs.Where(r => r.Lifetime == parsedLifetimeFilter.Value);
        }
        if (!string.IsNullOrWhiteSpace(project))
        {
            filteredRegs = filteredRegs.Where(r =>
                (r.ProjectName is not null && (r.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase) || GlobMatcher.IsMatch(r.ProjectName, project))) ||
                (graph is not null && (MatchesProject(r.ServiceTypeId) || MatchesProject(r.ImplementationTypeId))));
        }
        if (!string.IsNullOrWhiteSpace(serviceType))
        {
            filteredRegs = filteredRegs.Where(r =>
                MatchesService(r.ServiceTypeId) ||
                MatchesService(r.ImplementationTypeId));
        }
        var matchedRegs = filteredRegs.ToList();

        var lifetimeCounts = new
        {
            singleton = matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Singleton),
            scoped = matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Scoped),
            transient = matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Transient)
        };

        var topProjects = matchedRegs
            .Where(r => !string.IsNullOrWhiteSpace(r.ProjectName))
            .GroupBy(r => r.ProjectName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(g => new { project = g.Key, count = g.Count() })
            .ToList();

        var matchedCaptives = report.CaptiveDependencies
            .Where(c => string.IsNullOrWhiteSpace(project) ||
                        MatchesProject(c.ConsumerTypeId) ||
                        MatchesProject(c.ResolvedImplementationTypeId))
            .Where(c => string.IsNullOrWhiteSpace(serviceType) ||
                        MatchesService(c.ConsumerTypeId) ||
                        MatchesService(c.DependencyTypeId) ||
                        MatchesService(c.ResolvedImplementationTypeId))
            .Where(c => !parsedLifetimeFilter.HasValue ||
                        c.ConsumerLifetime == parsedLifetimeFilter.Value ||
                        c.DependencyLifetime == parsedLifetimeFilter.Value ||
                        (parsedLifetimeFilter.Value == ServiceLifetimeKind.Transient &&
                         c.DependencyPath.Contains("(Transient)", StringComparison.Ordinal)))
            .ToList();

        var captive = summaryOnly
            ? null
            : matchedCaptives.Select(c => new
            {
                consumerTypeId = c.ConsumerTypeId,
                consumerLifetime = c.ConsumerLifetime,
                dependencyTypeId = c.DependencyTypeId,
                dependencyLifetime = c.DependencyLifetime,
                resolvedImplementationTypeId = c.ResolvedImplementationTypeId,
                dependencyPath = c.DependencyPath,
                location = c.Location?.ToShortString()
            }).ToList();

        var filteredSingles = report.SingleImplementationInterfaces.AsEnumerable();
        if (onlyUnregistered)
        {
            filteredSingles = filteredSingles.Where(s => !s.IsDiRegistered);
        }
        if (!string.IsNullOrWhiteSpace(project))
        {
            filteredSingles = filteredSingles.Where(s =>
                MatchesProject(s.InterfaceTypeId) ||
                MatchesProject(s.ImplementationTypeId));
        }
        if (!string.IsNullOrWhiteSpace(serviceType))
        {
            filteredSingles = filteredSingles.Where(s =>
                s.InterfaceTypeId.Contains(serviceType, StringComparison.OrdinalIgnoreCase) ||
                s.ImplementationTypeId.Contains(serviceType, StringComparison.OrdinalIgnoreCase) ||
                GlobMatcher.IsMatch(s.InterfaceTypeId, serviceType) ||
                GlobMatcher.IsMatch(s.ImplementationTypeId, serviceType));
        }
        var matchedSingles = filteredSingles.ToList();

        var singleImpls = summaryOnly || onlyCaptive
            ? null
            : matchedSingles
                .Skip(safeOffset)
                .Take(safeLimit)
                .Select(s => new
                {
                    interfaceTypeId = s.InterfaceTypeId,
                    implementationTypeId = s.ImplementationTypeId,
                    isDiRegistered = s.IsDiRegistered,
                    consumerCount = s.ConsumerCount,
                    location = s.Location?.ToShortString()
                })
                .ToList();

        var singleTruncated = singleImpls is not null && (safeOffset + singleImpls.Count < matchedSingles.Count);

        var registrations = !summaryOnly && !onlyCaptive && includeRegistrations
            ? matchedRegs
                .Skip(safeOffset)
                .Take(safeLimit)
                .Select(r => new
                {
                    serviceTypeId = r.ServiceTypeId,
                    implementationTypeId = r.ImplementationTypeId,
                    lifetime = r.Lifetime,
                    serviceKey = r.ServiceKey,
                    registeredBy = r.RegisteredByTypeId,
                    project = r.ProjectName,
                    location = r.Location?.ToShortString()
                })
                .ToList()
            : null;

        var regsTruncated = registrations is not null && (safeOffset + registrations.Count < matchedRegs.Count);
        var hasFilters = !string.IsNullOrWhiteSpace(lifetime) || !string.IsNullOrWhiteSpace(project) || !string.IsNullOrWhiteSpace(serviceType);

        return new
        {
            registrationCount = report.RegistrationCount,
            matchedRegistrationCount = hasFilters ? (int?)matchedRegs.Count : null,
            lifetimeCounts,
            topProjects = !summaryOnly && topProjects.Count > 0 ? topProjects : null,
            captiveDependencyCount = report.CaptiveDependencyCount,
            matchedCaptiveDependencyCount = hasFilters ? (int?)matchedCaptives.Count : null,
            singleImplementationCount = report.SingleImplementationCount,
            matchedSingleImplementationCount = (onlyUnregistered || !string.IsNullOrWhiteSpace(serviceType) || !string.IsNullOrWhiteSpace(project)) ? (int?)matchedSingles.Count : null,
            offset = safeOffset > 0 ? (int?)safeOffset : null,
            nextOffset = (singleTruncated || regsTruncated) ? (int?)(safeOffset + safeLimit) : null,
            returnedSingleImplementationCount = singleImpls?.Count,
            singleImplementationsTruncated = singleTruncated ? (bool?)true : null,
            registrationsIncluded = includeRegistrations && !summaryOnly && !onlyCaptive,
            returnedRegistrationCount = registrations?.Count,
            registrationsTruncated = regsTruncated ? (bool?)true : null,
            truncated = singleTruncated || regsTruncated,
            loadStatus = FormatCompactLoadStatus(loadStatus),
            captiveDependencies = captive,
            singleImplementationInterfaces = singleImpls,
            registrations
        };
    }

    public static object FormatDeadCode(
        DeadCodeReport report,
        GraphLoadStatus? loadStatus,
        int offset,
        int limit,
        bool summaryOnly)
    {
        var safeOffset = Math.Max(0, offset);
        var paged = summaryOnly ? [] : report.Candidates.Skip(safeOffset).Take(limit).ToList();
        var truncated = !summaryOnly && (safeOffset + paged.Count < report.UnreferencedCount);

        var candidates = summaryOnly
            ? null
            : paged.Select(c => new
            {
                name = c.FullName,
                nodeId = string.Equals(c.NodeId, c.FullName, StringComparison.Ordinal) ? null : c.NodeId,
                project = c.ProjectName,
                accessibility = c.Accessibility,
                reason = CompactDeadCodeReason(c.Reason),
                location = c.Location?.ToShortString()
            }).ToList();

        return new
        {
            scope = report.Scope,
            profile = report.Profile,
            totalEvaluated = report.TotalEvaluated,
            entryPointCount = report.EntryPointCount,
            unreferencedCount = report.UnreferencedCount,
            acknowledgedCount = report.AcknowledgedCount,
            returnedCount = summaryOnly ? (int?)null : paged.Count,
            offset = safeOffset > 0 ? (int?)safeOffset : null,
            nextOffset = truncated ? (int?)(safeOffset + paged.Count) : null,
            truncated,
            activeDetectors = report.ActiveDetectors,
            referencesFromTestsExcluded = report.ReferencesFromTestsExcluded,
            excludedTestProjectCount = report.ExcludedTestProjectCount,
            note = report.Note,
            loadStatus = FormatCompactLoadStatus(loadStatus),
            acknowledged = report.Acknowledged,
            candidates
        };
    }

    public static object FormatCheckRules(
        string rulesSource,
        IReadOnlyList<string> loadedPlugins,
        IReadOnlyList<string> pluginDiagnostics,
        RuleCheckReport report,
        GraphLoadStatus? loadStatus,
        int offset,
        int maxViolations,
        bool summaryOnly,
        string? snapshotId = null,
        DateTimeOffset? capturedAt = null,
        string? note = null)
    {
        var safeOffset = Math.Max(0, offset);
        var paged = summaryOnly ? [] : report.Violations.Skip(safeOffset).Take(maxViolations).ToList();
        var truncated = !summaryOnly && (safeOffset + paged.Count < report.TotalViolations);

        var ruleNames = paged.Count > 0
            ? paged
                .GroupBy(v => v.RuleId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().RuleName, StringComparer.Ordinal)
            : null;

        var evaluatedRules = report.EvaluatedRules is { Count: > 0 }
            ? report.EvaluatedRules.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                severity = r.Severity,
                violations = r.ViolationCount
            }).ToList()
            : null;

        var violations = summaryOnly
            ? null
            : paged.Select(v => new
            {
                ruleId = v.RuleId,
                severity = v.Severity,
                message = v.Message,
                nodeId = v.NodeId,
                targetNodeId = v.TargetNodeId,
                location = v.Location?.ToShortString(),
                approximate = v.Details?.TryGetValue("approximate", out var approx) == true && approx == "true" ? (bool?)true : null
            }).ToList();

        return new
        {
            snapshotId,
            capturedAt,
            rulesSource,
            loadedPlugins = loadedPlugins.Count > 0 ? loadedPlugins : null,
            pluginDiagnostics = pluginDiagnostics.Count > 0 ? pluginDiagnostics : null,
            loadStatus = ShouldIncludeLoadStatus(loadStatus) ? loadStatus : null,
            skippedDegradedProjects = report.SkippedDegradedProjects is { Count: > 0 } ? report.SkippedDegradedProjects : null,
            suppressedCycles = report.SuppressedCycles is { Count: > 0 } ? report.SuppressedCycles : null,
            acknowledgedDeadCode = report.AcknowledgedDeadCode is { Count: > 0 } ? report.AcknowledgedDeadCode : null,
            vacuousConfiguration = report.VacuousConfiguration ? (bool?)true : null,
            configurationError = report.ConfigurationError ? (bool?)true : null,
            note = note ?? report.Note,
            rulesEvaluated = report.RulesEvaluated,
            evaluatedRules,
            totalViolations = report.TotalViolations,
            errorCount = report.ErrorCount,
            warningCount = report.WarningCount,
            infoCount = report.InfoCount,
            returnedCount = summaryOnly ? (int?)null : paged.Count,
            offset = safeOffset > 0 ? (int?)safeOffset : null,
            nextOffset = truncated ? (int?)(safeOffset + paged.Count) : null,
            truncated,
            ruleNames,
            violations
        };
    }

    public static object FormatDiff(
        string rulesSource,
        string baselineSource,
        bool identicalSnapshot,
        IReadOnlyList<string> pluginDiagnostics,
        SnapshotDiffResult diff,
        GraphLoadStatus? loadStatus = null,
        string? scopeWarning = null,
        int offset = 0,
        int maxViolations = 50,
        bool summaryOnly = false)
    {
        static object ProjectViolation(RuleViolation v) => new
        {
            ruleId = v.RuleId,
            ruleName = string.Equals(v.RuleName, v.RuleId, StringComparison.Ordinal) ? null : v.RuleName,
            severity = v.Severity,
            message = v.Message,
            nodeId = v.NodeId,
            targetNodeId = v.TargetNodeId,
            location = v.Location?.ToShortString()
        };

        var safeOffset = Math.Max(0, offset);
        var safeMax = Math.Clamp(maxViolations, 1, 200);
        var newPaged = summaryOnly ? null : diff.NewViolations.Skip(safeOffset).Take(safeMax).Select(ProjectViolation).ToList();
        var resolvedPaged = summaryOnly ? null : diff.ResolvedViolations.Skip(safeOffset).Take(safeMax).Select(ProjectViolation).ToList();
        var newTrunc = newPaged is not null && (safeOffset + newPaged.Count < diff.NewViolations.Count);
        var resolvedTrunc = resolvedPaged is not null && (safeOffset + resolvedPaged.Count < diff.ResolvedViolations.Count);
        var anyTrunc = newTrunc || resolvedTrunc;

        var notes = new List<string>();
        if (identicalSnapshot)
        {
            var isIl = string.Equals(loadStatus?.Backend, "il", StringComparison.OrdinalIgnoreCase);
            notes.Add(isIl
                ? "Baseline and current point to the same compiled-assembly snapshot (no rebuilt binary changes detected since baseline; unbuilt C# source edits require rebuilding or targeting the .slnx/.csproj source path)."
                : "Baseline and current point to the same code snapshot (no C# source changes detected since baseline). Edit files and re-run diff, or pass baselineFile to compare against an earlier saved baseline.");
            if (diff.CurrentViolationCount is > 0)
            {
                notes.Add($"Active rules report {diff.CurrentViolationCount} pre-existing violation(s) on this snapshot (diff evaluates the active ruleset on both baseline and current graphs to measure code deltas; call check_rules to inspect all active violations after policy-only YAML edits).");
            }
        }
        if (!string.IsNullOrWhiteSpace(scopeWarning))
        {
            notes.Add(scopeWarning);
        }
        if (!string.IsNullOrWhiteSpace(diff.Note))
        {
            notes.Add(diff.Note);
        }

        return new
        {
            rulesSource,
            baselineSource,
            identicalSnapshot = identicalSnapshot ? (bool?)true : null,
            vacuousConfiguration = diff.VacuousConfiguration ? (bool?)true : null,
            configurationError = diff.ConfigurationError ? (bool?)true : null,
            loadStatus = FormatCompactLoadStatus(loadStatus),
            note = notes.Count > 0 ? string.Join(" ", notes) : null,
            pluginDiagnostics = pluginDiagnostics.Count > 0 ? pluginDiagnostics : null,
            diff = new
            {
                baselineSnapshotId = diff.BaselineSnapshotId,
                currentSnapshotId = diff.CurrentSnapshotId,
                typeCountDelta = diff.TypeCountDelta,
                edgeCountDelta = diff.EdgeCountDelta,
                cycleCountDelta = diff.CycleCountDelta,
                violationCountDelta = diff.ViolationCountDelta,
                baselineViolationCount = diff.BaselineViolationCount,
                currentViolationCount = diff.CurrentViolationCount,
                totalAddedCycles = diff.AddedCycles.Count,
                totalResolvedCycles = diff.ResolvedCycles.Count,
                totalNewViolations = diff.NewViolations.Count,
                totalResolvedViolations = diff.ResolvedViolations.Count,
                offset = safeOffset > 0 ? (int?)safeOffset : null,
                nextOffset = anyTrunc ? (int?)(safeOffset + safeMax) : null,
                truncated = anyTrunc ? (bool?)true : null,
                addedCycles = summaryOnly ? null : diff.AddedCycles,
                resolvedCycles = summaryOnly ? null : diff.ResolvedCycles,
                newViolations = newPaged,
                resolvedViolations = resolvedPaged
            }
        };
    }

    public static object FormatAnalyzerDiagnostics(
        AnalyzerDiagnosticsReport report,
        GraphLoadStatus? loadStatus,
        int offset,
        int maxItems,
        bool summaryOnly,
        string? snapshotId = null,
        DateTimeOffset? capturedAt = null)
    {
        var safeOffset = Math.Max(0, offset);
        var paged = summaryOnly ? [] : report.Diagnostics.Skip(safeOffset).Take(maxItems).ToList();
        var truncated = !summaryOnly && (safeOffset + paged.Count < report.TotalDiagnostics);

        var ruleTitles = paged.Count > 0
            ? paged
                .GroupBy(d => d.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal)
            : null;

        var diagnostics = summaryOnly
            ? null
            : paged.Select(d => new
            {
                id = d.Id,
                category = d.Category,
                severity = d.Severity,
                message = d.Message,
                project = d.ProjectName,
                location = d.Location?.ToShortString()
            }).ToList();

        return new
        {
            snapshotId,
            capturedAt,
            analyzersLoaded = report.AnalyzersLoaded,
            totalDiagnostics = report.TotalDiagnostics,
            errorCount = report.ErrorCount,
            warningCount = report.WarningCount,
            infoCount = report.InfoCount,
            hiddenBySeverityCount = report.HiddenBySeverityCount,
            returnedCount = summaryOnly ? (int?)null : paged.Count,
            offset = safeOffset > 0 ? (int?)safeOffset : null,
            nextOffset = truncated ? (int?)(safeOffset + paged.Count) : null,
            truncated,
            categories = report.Categories,
            note = report.Note,
            loadStatus = FormatCompactLoadStatus(loadStatus),
            ruleTitles,
            diagnostics
        };
    }

    public static object FormatSymbolInspection(
        SymbolInspectionReport report,
        GraphLoadStatus? loadStatus,
        string? fields,
        int maxEdges,
        int offset,
        int? incomingOffset = null)
    {
        var node = report.Node;
        var m = report.Metrics;
        var safeOutOffset = Math.Max(0, offset);
        var safeInOffset = Math.Max(0, incomingOffset ?? 0);
        var safeMaxEdges = Math.Clamp(maxEdges, 1, 200);

        var outPaged = report.OutgoingDependencies.Skip(safeOutOffset).Take(safeMaxEdges)
            .Select(FormatEdgeDto).ToList();
        var inPaged = report.IncomingDependents.Skip(safeInOffset).Take(safeMaxEdges)
            .Select(FormatEdgeDto).ToList();
        var outTrunc = safeOutOffset + outPaged.Count < report.OutgoingDependencies.Count;
        var inTrunc = safeInOffset + inPaged.Count < report.IncomingDependents.Count;

        var isSynthetic = node.Metadata?.TryGetValue("isSynthetic", out var syn) == true &&
                          syn.Equals("true", StringComparison.OrdinalIgnoreCase);

        var parsedFields = ParseFields(fields);
        var full = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = node.Id,
            ["name"] = node.Name,
            ["kind"] = node.Kind,
            ["typeKind"] = node.TypeKind,
            ["project"] = node.ProjectName,
            ["namespace"] = node.Namespace,
            ["accessibility"] = node.Accessibility,
            ["isAbstract"] = node.IsAbstract ? true : null,
            ["isStatic"] = node.IsStatic ? true : null,
            ["isSynthetic"] = isSynthetic ? true : null,
            ["location"] = node.Location?.ToShortString(),
            ["lineCount"] = node.LineCount,
            ["memberCount"] = node.MemberCount > 0 ? node.MemberCount : null,
            ["constructorParameterCount"] = node.ConstructorParameterCount > 0 ? node.ConstructorParameterCount : null,
            ["metrics"] = m is null ? null : new
            {
                afferentCoupling = m.AfferentCoupling,
                efferentCoupling = m.EfferentCoupling,
                instability = m.Instability,
                maxMethodComplexity = m.MaxMethodComplexity,
                totalCyclomaticComplexity = m.CyclomaticComplexity,
                maxMethodCognitive = m.MaxMethodCognitiveComplexity,
                totalCognitiveComplexity = m.CognitiveComplexity,
                lcom4 = node.Kind == NodeKind.Method ? (int?)null : m.Lcom4,
                customMetrics = m.CustomMetrics
            },
            ["outgoingCount"] = report.OutgoingDependencies.Count,
            ["incomingCount"] = report.IncomingDependents.Count,
            ["offset"] = safeOutOffset > 0 ? safeOutOffset : null,
            ["incomingOffset"] = safeInOffset > 0 ? safeInOffset : null,
            ["outgoingDependencies"] = outPaged,
            ["incomingDependents"] = inPaged,
            ["outgoingTruncated"] = outTrunc ? true : null,
            ["incomingTruncated"] = inTrunc ? true : null,
            ["nextOffset"] = outTrunc ? safeOutOffset + safeMaxEdges : null,
            ["incomingNextOffset"] = inTrunc ? safeInOffset + safeMaxEdges : null,
            ["truncated"] = (outTrunc || inTrunc) ? true : null,
            ["loadStatus"] = FormatCompactLoadStatus(loadStatus)
        };

        if (parsedFields is null || parsedFields.Contains("all"))
        {
            return StripNulls(full);
        }

        var explicitValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["isAbstract"] = node.IsAbstract,
            ["isStatic"] = node.IsStatic,
            ["isSynthetic"] = isSynthetic,
            ["memberCount"] = node.MemberCount,
            ["constructorParameterCount"] = node.ConstructorParameterCount,
            ["offset"] = safeOutOffset,
            ["incomingOffset"] = safeInOffset,
            ["outgoingTruncated"] = outTrunc,
            ["incomingTruncated"] = inTrunc,
            ["truncated"] = outTrunc || inTrunc
        };
        var projected = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = node.Id
        };
        foreach (var kvp in full)
        {
            if (!parsedFields.Contains(kvp.Key))
            {
                continue;
            }
            if (kvp.Value is not null)
            {
                projected[kvp.Key] = kvp.Value;
            }
            else if (explicitValues.TryGetValue(kvp.Key, out var explicitVal))
            {
                projected[kvp.Key] = explicitVal;
            }
        }

        // Preserve pagination metadata whenever dependency arrays are requested so agents never mistake a page for the full list
        var wantsOut = parsedFields.Contains("outgoingDependencies");
        var wantsIn = parsedFields.Contains("incomingDependents");
        if (wantsOut)
        {
            projected["outgoingCount"] = report.OutgoingDependencies.Count;
            if (safeOutOffset > 0) projected["offset"] = safeOutOffset;
            if (outTrunc)
            {
                projected["outgoingTruncated"] = true;
                projected["nextOffset"] = safeOutOffset + safeMaxEdges;
            }
        }
        if (wantsIn)
        {
            projected["incomingCount"] = report.IncomingDependents.Count;
            if (safeInOffset > 0) projected["incomingOffset"] = safeInOffset;
            if (inTrunc)
            {
                projected["incomingTruncated"] = true;
                projected["incomingNextOffset"] = safeInOffset + safeMaxEdges;
            }
        }
        if ((wantsOut && outTrunc) || (wantsIn && inTrunc))
        {
            projected["truncated"] = true;
        }

        return projected;
    }

    public static object FormatImpact(
        ImpactAnalysisResult result,
        GraphLoadStatus? loadStatus,
        int offset,
        int maxItems,
        bool summaryOnly,
        string? fields,
        bool autoSummarized = false)
    {
        var safeOffset = Math.Max(0, offset);
        var paged = summaryOnly ? [] : result.ImpactedNodes.Skip(safeOffset).Take(maxItems).ToList();
        var truncated = !summaryOnly && (safeOffset + paged.Count < result.TotalTransitiveDependentsCount);
        var parsedFields = ParseFields(fields);

        var nodesPayload = summaryOnly
            ? null
            : paged.Select(n => ProjectImpactNode(n, parsedFields)).ToList();

        var note = result.Note;
        if (autoSummarized)
        {
            const string hint = "Auto-summarized because totalTransitiveDependentsCount > 20; pass summaryOnly: false (or an explicit maxItems) to retrieve impactedNodes.";
            note = string.IsNullOrWhiteSpace(note) ? hint : $"{note} {hint}";
        }

        return new
        {
            targetNodeId = result.TargetNodeId,
            targetFullName = string.Equals(result.TargetNodeId, result.TargetFullName, StringComparison.Ordinal) ? null : result.TargetFullName,
            directDependentsCount = result.DirectDependentsCount,
            totalTransitiveDependentsCount = result.TotalTransitiveDependentsCount,
            returnedCount = summaryOnly ? (int?)null : paged.Count,
            offset = safeOffset > 0 ? (int?)safeOffset : null,
            nextOffset = truncated ? (int?)(safeOffset + paged.Count) : null,
            autoSummarized = autoSummarized ? (bool?)true : null,
            truncated,
            affectedProjects = result.AffectedProjects,
            excludedRegistrationCount = result.ExcludedRegistrationCount,
            excludedTestCount = result.ExcludedTestCount,
            note,
            loadStatus = FormatCompactLoadStatus(loadStatus),
            impactedNodes = nodesPayload
        };
    }

    private static object FormatEdgeDto(SymbolDependencyDto d) => new
    {
        target = d.TargetId,
        kinds = d.Kinds,
        weight = d.Weight,
        location = d.Location?.ToShortString()
    };

    private static Dictionary<string, object?> ProjectHotspotItem(
        NodeMetrics m,
        string sortBy,
        HashSet<string>? requestedFields,
        bool includeAll)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = m.Name
        };

        if (includeAll)
        {
            AddAllHotspotFields(d, m);
            return d;
        }

        if (requestedFields is { Count: > 0 })
        {
            AddRequestedHotspotFields(d, m, requestedFields);
            return d;
        }

        // Default compact projection tailored to the requested sortBy metric
        AddDefaultSortMetrics(d, m, sortBy);
        d["lines"] = m.LineCount;
        if (m.Location is not null)
        {
            d["location"] = m.Location.ToShortString();
        }
        return d;
    }

    private static void AddDefaultSortMetrics(Dictionary<string, object?> d, NodeMetrics m, string sortBy)
    {
        switch (sortBy.ToLowerInvariant())
        {
            case "god_class" or "godclass":
                d["godClassScore"] = ArchitectureAnalyzerService.ComputeGodClassScore(m);
                d["cyclomaticComplexity"] = m.CyclomaticComplexity;
                d["maxMethodComplexity"] = m.MaxMethodComplexity;
                d["efferentCoupling"] = m.EfferentCoupling;
                break;
            case "cognitive" or "cognitive_complexity":
                d["maxMethodCognitive"] = m.MaxMethodCognitiveComplexity;
                d["cognitive"] = m.CognitiveComplexity;
                break;
            case "total_cognitive":
                d["cognitive"] = m.CognitiveComplexity;
                d["maxMethodCognitive"] = m.MaxMethodCognitiveComplexity;
                break;
            case "lcom4" or "cohesion":
                d["lcom4"] = m.Lcom4;
                d["efferentCoupling"] = m.EfferentCoupling;
                break;
            case "coupling" or "ce" or "efferent":
                d["efferentCoupling"] = m.EfferentCoupling;
                d["afferentCoupling"] = m.AfferentCoupling;
                d["instability"] = m.Instability;
                break;
            case "afferent" or "ca":
                d["afferentCoupling"] = m.AfferentCoupling;
                d["efferentCoupling"] = m.EfferentCoupling;
                break;
            case "instability":
                d["instability"] = m.Instability;
                d["efferentCoupling"] = m.EfferentCoupling;
                d["afferentCoupling"] = m.AfferentCoupling;
                break;
            case "distance":
                d["distanceFromMainSequence"] = m.DistanceFromMainSequence;
                d["abstractness"] = m.Abstractness;
                d["instability"] = m.Instability;
                break;
            case "lines" or "loc" or "size":
                d["maxMethodComplexity"] = m.MaxMethodComplexity;
                break;
            case "ctor" or "constructor_params":
                d["constructorParameterCount"] = m.ConstructorParameterCount;
                d["efferentCoupling"] = m.EfferentCoupling;
                break;
            case "total_complexity" or "total_cyclomatic":
                d["cyclomaticComplexity"] = m.CyclomaticComplexity;
                d["maxMethodComplexity"] = m.MaxMethodComplexity;
                break;
            case "complexity" or "cyclomatic" or "cyclomatic_complexity":
                d["maxMethodComplexity"] = m.MaxMethodComplexity;
                d["cyclomaticComplexity"] = m.CyclomaticComplexity;
                break;
            default:
                if (m.CustomMetrics is not null && m.CustomMetrics.TryGetValue(sortBy, out var customVal))
                {
                    d[sortBy] = customVal;
                }
                else
                {
                    d["maxMethodComplexity"] = m.MaxMethodComplexity;
                    d["cyclomaticComplexity"] = m.CyclomaticComplexity;
                }
                break;
        }
    }

    private static void AddAllHotspotFields(Dictionary<string, object?> d, NodeMetrics m)
    {
        if (!string.Equals(m.NodeId, m.Name, StringComparison.Ordinal)) d["nodeId"] = m.NodeId;
        d["afferentCoupling"] = m.AfferentCoupling;
        d["efferentCoupling"] = m.EfferentCoupling;
        d["instability"] = m.Instability;
        if (m.Abstractness.HasValue) d["abstractness"] = m.Abstractness.Value;
        if (m.DistanceFromMainSequence.HasValue) d["distanceFromMainSequence"] = m.DistanceFromMainSequence.Value;
        d["cyclomaticComplexity"] = m.CyclomaticComplexity;
        d["maxMethodComplexity"] = m.MaxMethodComplexity;
        d["cognitiveComplexity"] = m.CognitiveComplexity;
        d["maxMethodCognitiveComplexity"] = m.MaxMethodCognitiveComplexity;
        d["lcom4"] = m.Lcom4;
        d["lineCount"] = m.LineCount;
        d["constructorParameterCount"] = m.ConstructorParameterCount;
        d["godClassScore"] = ArchitectureAnalyzerService.ComputeGodClassScore(m);
        if (m.Location is not null) d["location"] = m.Location.ToShortString();
        if (m.CustomMetrics is { Count: > 0 }) d["customMetrics"] = m.CustomMetrics;
    }

    private static readonly HashSet<string> ValidHotspotFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "name", "nodeid", "ca", "afferent", "afferentcoupling", "ce", "efferent", "efferentcoupling",
        "instability", "abstractness", "distance", "distancefrommainsequence",
        "cc", "complexity", "cyclomaticcomplexity", "maxcc", "maxmethodcomplexity",
        "cognitive", "cognitivecomplexity", "maxcognitive", "maxmethodcognitive", "maxmethodcognitivecomplexity",
        "lcom4", "lines", "loc", "linecount", "ctor", "constructorparams", "constructorparametercount",
        "location", "custommetrics", "godclassscore"
    };

    private static readonly HashSet<string> ValidInspectSymbolFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "id", "name", "kind", "typeKind", "project", "namespace", "accessibility",
        "isAbstract", "isStatic", "isSynthetic", "location", "lineCount", "memberCount",
        "constructorParameterCount", "metrics", "outgoingCount", "incomingCount",
        "offset", "incomingOffset", "outgoingDependencies", "incomingDependents",
        "outgoingTruncated", "incomingTruncated", "nextOffset", "incomingNextOffset",
        "truncated", "loadStatus"
    };

    private static readonly HashSet<string> ValidImpactFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "nodeId", "name", "project", "projectName", "distance", "category", "location"
    };

    public static void ValidateHotspotFields(string? fields)
    {
        var parsed = ParseFields(fields);
        if (parsed is null) return;
        var invalid = parsed.Where(f => !ValidHotspotFields.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown hotspots fields: {string.Join(", ", invalid)}. Supported fields: all, name, nodeId, afferentCoupling (ca), efferentCoupling (ce), instability, abstractness, distanceFromMainSequence (distance), cyclomaticComplexity (cc), maxMethodComplexity (maxCc), cognitiveComplexity (cognitive), maxMethodCognitiveComplexity (maxMethodCognitive), lcom4, lineCount (lines), constructorParameterCount (ctor), godClassScore, location, customMetrics.");
        }
    }

    public static void ValidateInspectSymbolFields(string? fields)
    {
        var parsed = ParseFields(fields);
        if (parsed is null) return;
        var invalid = parsed.Where(f => !ValidInspectSymbolFields.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown inspect_symbol fields: {string.Join(", ", invalid)}. Supported fields: all, id, name, kind, typeKind, project, namespace, accessibility, isAbstract, isStatic, isSynthetic, location, lineCount, memberCount, constructorParameterCount, metrics, outgoingCount, incomingCount, outgoingDependencies, incomingDependents, truncated, loadStatus.");
        }
    }

    public static void ValidateImpactFields(string? fields)
    {
        var parsed = ParseFields(fields);
        if (parsed is null) return;
        var invalid = parsed.Where(f => !ValidImpactFields.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown impact fields: {string.Join(", ", invalid)}. Supported fields: all, nodeId, name, project, distance, category, location.");
        }
    }

    private static void AddRequestedHotspotFields(Dictionary<string, object?> d, NodeMetrics m, HashSet<string> fields)
    {
        foreach (var field in fields)
        {
            switch (field.ToLowerInvariant())
            {
                case "nodeid": d["nodeId"] = m.NodeId; break;
                case "ca" or "afferent" or "afferentcoupling": d["afferentCoupling"] = m.AfferentCoupling; break;
                case "ce" or "efferent" or "efferentcoupling": d["efferentCoupling"] = m.EfferentCoupling; break;
                case "instability": d["instability"] = m.Instability; break;
                case "abstractness": if (m.Abstractness.HasValue) d["abstractness"] = m.Abstractness.Value; break;
                case "distance" or "distancefrommainsequence": if (m.DistanceFromMainSequence.HasValue) d["distanceFromMainSequence"] = m.DistanceFromMainSequence.Value; break;
                case "cc" or "complexity" or "cyclomaticcomplexity": d["cyclomaticComplexity"] = m.CyclomaticComplexity; break;
                case "maxcc" or "maxmethodcomplexity": d["maxMethodComplexity"] = m.MaxMethodComplexity; break;
                case "cognitive" or "cognitivecomplexity": d["cognitiveComplexity"] = m.CognitiveComplexity; break;
                case "maxcognitive" or "maxmethodcognitive" or "maxmethodcognitivecomplexity": d["maxMethodCognitiveComplexity"] = m.MaxMethodCognitiveComplexity; break;
                case "lcom4": d["lcom4"] = m.Lcom4; break;
                case "lines" or "loc" or "linecount": d["lineCount"] = m.LineCount; break;
                case "ctor" or "constructorparams" or "constructorparametercount": d["constructorParameterCount"] = m.ConstructorParameterCount; break;
                case "godclassscore": d["godClassScore"] = ArchitectureAnalyzerService.ComputeGodClassScore(m); break;
                case "location": if (m.Location is not null) d["location"] = m.Location.ToShortString(); break;
                case "custommetrics": if (m.CustomMetrics is { Count: > 0 }) d["customMetrics"] = m.CustomMetrics; break;
            }
        }
    }

    private static object ProjectImpactNode(ImpactNode n, HashSet<string>? fields)
    {
        if (fields is null)
        {
            return new
            {
                name = n.FullName,
                project = n.ProjectName,
                distance = n.Distance,
                category = string.Equals(n.Category, "consumer", StringComparison.Ordinal) ? null : n.Category,
                location = n.Location?.ToShortString()
            };
        }

        if (fields.Contains("all"))
        {
            return new
            {
                nodeId = n.NodeId,
                name = n.FullName,
                project = n.ProjectName,
                distance = n.Distance,
                category = string.Equals(n.Category, "consumer", StringComparison.Ordinal) ? null : n.Category,
                location = n.Location?.ToShortString()
            };
        }

        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = n.FullName };
        if (fields.Contains("nodeId")) d["nodeId"] = n.NodeId;
        if (fields.Contains("project") || fields.Contains("projectName")) d["project"] = n.ProjectName;
        if (fields.Contains("distance")) d["distance"] = n.Distance;
        if (fields.Contains("category")) d["category"] = n.Category;
        if (fields.Contains("location") && n.Location is not null) d["location"] = n.Location.ToShortString();
        return d;
    }

    private static string CompactDeadCodeReason(string reason)
    {
        if (reason.StartsWith("Unreferenced", StringComparison.OrdinalIgnoreCase))
        {
            return "unreferenced";
        }

        var idx = reason.IndexOf("by ", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = reason[(idx + 3)..];
            var space = rest.IndexOf(' ');
            if (space > 0 && int.TryParse(rest[..space], out var count))
            {
                return $"unreachable_island({count})";
            }
        }

        return reason;
    }

    private static string SummarizeDiagnosticMessage(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 220 ? singleLine : singleLine[..217] + "...";
    }

    internal static bool ShouldIncludeLoadStatus(GraphLoadStatus? status) =>
        status is not null && (status.Degraded || status.Approximate);

    internal static object? FormatCompactLoadStatus(GraphLoadStatus? status)
    {
        if (!ShouldIncludeLoadStatus(status))
        {
            return null;
        }

        var s = status!;
        if (s.Degraded)
        {
            return new
            {
                backend = s.Backend,
                projectsExpected = s.ProjectsExpected,
                projectsLoaded = s.ProjectsLoaded,
                projectsDegraded = s.ProjectsDegraded,
                degraded = true,
                approximate = s.Approximate ? (bool?)true : null,
                degradedProjects = s.DegradedProjects,
                note = $"Degraded workspace ({s.ProjectsLoaded}/{s.ProjectsExpected} projects loaded); call overview for full compile diagnostics."
            };
        }

        return new
        {
            backend = s.Backend,
            projectsExpected = s.ProjectsExpected,
            projectsLoaded = s.ProjectsLoaded,
            degraded = false,
            approximate = true,
            note = s.Note
        };
    }

    private static HashSet<string>? ParseFields(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> StripNulls(Dictionary<string, object?> source) =>
        source.Where(kvp => kvp.Value is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
}
