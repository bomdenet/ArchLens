namespace ArchLens.Model;

/// <summary>
/// Immutable canonical representation of a C# codebase's architectural structure.
/// All analyzers, metric calculators, and rules operate on this model rather than raw Roslyn APIs.
/// </summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, CodeNode> _nodesById;
    private readonly Dictionary<string, List<DependencyEdge>> _outgoing;
    private readonly Dictionary<string, List<DependencyEdge>> _incoming;

    public CodeGraph(
        string rootPath,
        IEnumerable<CodeNode> nodes,
        IEnumerable<DependencyEdge> edges,
        IEnumerable<ProjectInfo>? projects = null,
        IEnumerable<ExtractionDiagnostic>? diagnostics = null,
        IEnumerable<DiRegistration>? diRegistrations = null,
        IEnumerable<AnalyzerDiagnostic>? analyzerDiagnostics = null,
        GraphLoadStatus? loadStatus = null,
        int excludedTestProjectCount = 0,
        bool methodsIncluded = false,
        int? analyzersLoadedCount = null,
        IEnumerable<string>? availableAnalyzerCategories = null)
    {
        RootPath = rootPath;
        _nodesById = new Dictionary<string, CodeNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            _nodesById[node.Id] = node;
        }

        Nodes = _nodesById.Values.ToList();
        Edges = edges.ToList();
        Projects = projects?.ToList() ?? [];
        Diagnostics = diagnostics?.ToList() ?? [];
        DiRegistrations = diRegistrations?.ToList() ?? [];
        AnalyzerDiagnostics = analyzerDiagnostics?.ToList() ?? [];
        LoadStatus = loadStatus;
        ExcludedTestProjectCount = excludedTestProjectCount;
        MethodsIncluded = methodsIncluded || Nodes.Any(n => n.Kind == NodeKind.Method);
        AnalyzersLoadedCount = analyzersLoadedCount;
        AvailableAnalyzerCategories = availableAnalyzerCategories?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _outgoing = new Dictionary<string, List<DependencyEdge>>(StringComparer.Ordinal);
        _incoming = new Dictionary<string, List<DependencyEdge>>(StringComparer.Ordinal);

        foreach (var edge in Edges)
        {
            if (!_outgoing.TryGetValue(edge.FromId, out var outList))
            {
                outList = [];
                _outgoing[edge.FromId] = outList;
            }
            outList.Add(edge);

            if (!_incoming.TryGetValue(edge.ToId, out var inList))
            {
                inList = [];
                _incoming[edge.ToId] = inList;
            }
            inList.Add(edge);
        }
    }

    public string RootPath { get; }
    public IReadOnlyList<CodeNode> Nodes { get; }
    public IReadOnlyList<DependencyEdge> Edges { get; }
    public IReadOnlyList<ProjectInfo> Projects { get; }
    public IReadOnlyList<ExtractionDiagnostic> Diagnostics { get; }
    public IReadOnlyList<DiRegistration> DiRegistrations { get; }
    public IReadOnlyList<AnalyzerDiagnostic> AnalyzerDiagnostics { get; }
    public GraphLoadStatus? LoadStatus { get; }
    public int ExcludedTestProjectCount { get; }
    public bool MethodsIncluded { get; }
    public int? AnalyzersLoadedCount { get; }
    public IReadOnlyList<string>? AvailableAnalyzerCategories { get; }
    public IReadOnlyDictionary<string, CodeNode> NodesById => _nodesById;

    public CodeNode? FindNode(string id) => _nodesById.GetValueOrDefault(id);

    public IEnumerable<CodeNode> GetNodes(NodeKind kind) => Nodes.Where(n => n.Kind == kind);

    public IReadOnlyList<DependencyEdge> GetOutgoing(string nodeId) =>
        _outgoing.TryGetValue(nodeId, out var list) ? list : [];

    public IReadOnlyList<DependencyEdge> GetIncoming(string nodeId) =>
        _incoming.TryGetValue(nodeId, out var list) ? list : [];

    /// <summary>
    /// Aggregates dependencies at the requested scope (Project, Namespace, or Type).
    /// Returns a map from source ID to distinct target IDs (excluding self-loops).
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildAdjacency(NodeKind scope)
    {
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (scope == NodeKind.Project)
        {
            foreach (var project in Projects)
            {
                adjacency.TryAdd(project.Name, new HashSet<string>(StringComparer.Ordinal));
                foreach (var reference in project.ProjectReferences)
                {
                    if (!string.Equals(project.Name, reference, StringComparison.Ordinal))
                    {
                        adjacency[project.Name].Add(reference);
                    }
                }
            }

            foreach (var edge in Edges)
            {
                if (_nodesById.TryGetValue(edge.FromId, out var from) &&
                    _nodesById.TryGetValue(edge.ToId, out var to) &&
                    !string.Equals(from.ProjectName, to.ProjectName, StringComparison.Ordinal))
                {
                    adjacency.TryAdd(from.ProjectName, new HashSet<string>(StringComparer.Ordinal));
                    adjacency.TryAdd(to.ProjectName, new HashSet<string>(StringComparer.Ordinal));
                    adjacency[from.ProjectName].Add(to.ProjectName);
                }
            }

            return adjacency;
        }

        if (scope == NodeKind.Namespace)
        {
            foreach (var type in GetNodes(NodeKind.Type))
            {
                if (!string.IsNullOrWhiteSpace(type.Namespace))
                {
                    adjacency.TryAdd(type.Namespace, new HashSet<string>(StringComparer.Ordinal));
                }
            }

            foreach (var edge in Edges)
            {
                if (_nodesById.TryGetValue(edge.FromId, out var from) &&
                    _nodesById.TryGetValue(edge.ToId, out var to) &&
                    !string.IsNullOrWhiteSpace(from.Namespace) &&
                    !string.IsNullOrWhiteSpace(to.Namespace) &&
                    !string.Equals(from.Namespace, to.Namespace, StringComparison.Ordinal))
                {
                    adjacency.TryAdd(from.Namespace, new HashSet<string>(StringComparer.Ordinal));
                    adjacency.TryAdd(to.Namespace, new HashSet<string>(StringComparer.Ordinal));
                    adjacency[from.Namespace].Add(to.Namespace);
                }
            }

            return adjacency;
        }

        // Type or Method level
        foreach (var node in GetNodes(scope))
        {
            adjacency.TryAdd(node.Id, new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var edge in Edges)
        {
            if (_nodesById.TryGetValue(edge.FromId, out var from) &&
                _nodesById.TryGetValue(edge.ToId, out var to) &&
                from.Kind == scope &&
                to.Kind == scope &&
                !string.Equals(from.Id, to.Id, StringComparison.Ordinal))
            {
                adjacency.TryAdd(from.Id, new HashSet<string>(StringComparer.Ordinal));
                adjacency.TryAdd(to.Id, new HashSet<string>(StringComparer.Ordinal));
                adjacency[from.Id].Add(to.Id);
            }
        }

        return adjacency;
    }
}

public sealed record ArchitectureSnapshot(
    string SnapshotId,
    string TargetPath,
    DateTimeOffset CapturedAt,
    CodeGraph Graph);
