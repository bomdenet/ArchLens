using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Calculates architectural metrics (Ca, Ce, Instability, Abstractness, Distance from Main Sequence, Complexity).
/// </summary>
public sealed class MetricsEngine
{
    private readonly IReadOnlyList<ICustomMetricCalculator> _customCalculators;

    public MetricsEngine(IEnumerable<ICustomMetricCalculator>? customCalculators = null)
    {
        _customCalculators = customCalculators?.ToList() ?? [];
    }

    public IReadOnlyDictionary<string, NodeMetrics> CalculateTypeMetrics(
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraCalculators = null)
    {
        var typeAdj = graph.BuildAdjacency(NodeKind.Type);
        var incomingCounts = BuildIncomingCounts(typeAdj);
        var customByNode = BuildCustomMetricsByNode(graph, extraCalculators);
        var result = new Dictionary<string, NodeMetrics>(StringComparer.Ordinal);

        foreach (var node in graph.GetNodes(NodeKind.Type))
        {
            var ce = typeAdj.TryGetValue(node.Id, out var outgoing) ? outgoing.Count : 0;
            var ca = incomingCounts.GetValueOrDefault(node.Id, 0);
            var instability = (ca + ce) == 0 ? 0.0 : Math.Round((double)ce / (ca + ce), 3);

            var maxMethodComplexity = ParseMetadataInt(node, "maxMethodComplexity", node.CyclomaticComplexity);
            var maxMethodCognitive = ParseMetadataInt(node, "maxMethodCognitiveComplexity", node.CognitiveComplexity);

            result[node.Id] = new NodeMetrics(
                NodeId: node.Id,
                Name: node.FullName,
                Kind: NodeKind.Type,
                AfferentCoupling: ca,
                EfferentCoupling: ce,
                Instability: instability,
                Abstractness: null,
                DistanceFromMainSequence: null,
                CyclomaticComplexity: node.CyclomaticComplexity,
                MaxMethodComplexity: maxMethodComplexity,
                LineCount: node.LineCount,
                ConstructorParameterCount: node.ConstructorParameterCount,
                Location: node.Location,
                CognitiveComplexity: node.CognitiveComplexity,
                MaxMethodCognitiveComplexity: maxMethodCognitive,
                Lcom4: node.Lcom4,
                CustomMetrics: customByNode.GetValueOrDefault(node.Id));
        }

        return result;
    }

    public IReadOnlyDictionary<string, NodeMetrics> CalculateMethodMetrics(
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraCalculators = null)
    {
        var methodAdj = graph.BuildAdjacency(NodeKind.Method);
        var incomingCounts = BuildIncomingCounts(methodAdj);
        var customByNode = BuildCustomMetricsByNode(graph, extraCalculators);
        var result = new Dictionary<string, NodeMetrics>(StringComparer.Ordinal);

        foreach (var node in graph.GetNodes(NodeKind.Method))
        {
            var ce = methodAdj.TryGetValue(node.Id, out var outgoing) ? outgoing.Count : 0;
            var ca = incomingCounts.GetValueOrDefault(node.Id, 0);
            var instability = (ca + ce) == 0 ? 0.0 : Math.Round((double)ce / (ca + ce), 3);

            result[node.Id] = new NodeMetrics(
                NodeId: node.Id,
                Name: node.FullName,
                Kind: NodeKind.Method,
                AfferentCoupling: ca,
                EfferentCoupling: ce,
                Instability: instability,
                Abstractness: null,
                DistanceFromMainSequence: null,
                CyclomaticComplexity: node.CyclomaticComplexity,
                MaxMethodComplexity: node.CyclomaticComplexity,
                LineCount: node.LineCount,
                ConstructorParameterCount: node.ConstructorParameterCount,
                Location: node.Location,
                CognitiveComplexity: node.CognitiveComplexity,
                MaxMethodCognitiveComplexity: node.CognitiveComplexity,
                Lcom4: 0,
                CustomMetrics: customByNode.GetValueOrDefault(node.Id));
        }

        return result;
    }

    public IReadOnlyDictionary<string, NodeMetrics> CalculateNamespaceMetrics(
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraCalculators = null)
    {
        var nsAdj = graph.BuildAdjacency(NodeKind.Namespace);
        var incomingCounts = BuildIncomingCounts(nsAdj);
        var customByNode = BuildCustomMetricsByNode(graph, extraCalculators);
        var typesByNs = graph.GetNodes(NodeKind.Type)
            .Where(t => !string.IsNullOrWhiteSpace(t.Namespace))
            .GroupBy(t => t.Namespace!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var result = new Dictionary<string, NodeMetrics>(StringComparer.Ordinal);

        foreach (var (ns, types) in typesByNs)
        {
            var ce = nsAdj.TryGetValue(ns, out var outgoing) ? outgoing.Count : 0;
            var ca = incomingCounts.GetValueOrDefault(ns, 0);
            var instability = (ca + ce) == 0 ? 0.0 : Math.Round((double)ce / (ca + ce), 3);

            var abstractCount = types.Count(t => t.IsAbstract || t.TypeKind == TypeKind.Interface);
            var abstractness = types.Count == 0 ? 0.0 : Math.Round((double)abstractCount / types.Count, 3);
            var distance = Math.Round(Math.Abs(abstractness + instability - 1.0), 3);

            var totalComplexity = types.Sum(t => t.CyclomaticComplexity);
            var maxMethod = types.Max(t => ParseMetadataInt(t, "maxMethodComplexity", t.CyclomaticComplexity));
            var totalCognitive = types.Sum(t => t.CognitiveComplexity);
            var maxMethodCognitive = types.Max(t => ParseMetadataInt(t, "maxMethodCognitiveComplexity", t.CognitiveComplexity));
            var maxLcom4 = types.Max(t => t.Lcom4);
            var totalLines = types.Sum(t => t.LineCount);
            var maxCtor = types.Max(t => t.ConstructorParameterCount);

            var nsNodeId = $"ns:{ns}";
            var customMetrics = MergeNamespaceCustomMetrics(customByNode, nsNodeId, ns);

            result[ns] = new NodeMetrics(
                NodeId: nsNodeId,
                Name: ns,
                Kind: NodeKind.Namespace,
                AfferentCoupling: ca,
                EfferentCoupling: ce,
                Instability: instability,
                Abstractness: abstractness,
                DistanceFromMainSequence: distance,
                CyclomaticComplexity: totalComplexity,
                MaxMethodComplexity: maxMethod,
                LineCount: totalLines,
                ConstructorParameterCount: maxCtor,
                Location: null,
                CognitiveComplexity: totalCognitive,
                MaxMethodCognitiveComplexity: maxMethodCognitive,
                Lcom4: maxLcom4,
                CustomMetrics: customMetrics);
        }

        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> CalculateCustomMetrics(
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraCalculators = null)
    {
        var allCalcs = extraCalculators is null
            ? _customCalculators
            : [.. _customCalculators, .. extraCalculators];

        var output = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var calc in allCalcs)
        {
            try
            {
                var values = calc.Calculate(graph);
                if (values is not null)
                {
                    output[calc.MetricName] = values;
                }
            }
            catch
            {
                // Isolate faulty custom metric calculators so remaining built-in & plugin metrics succeed.
            }
        }
        return output;
    }

    private Dictionary<string, Dictionary<string, double>> BuildCustomMetricsByNode(
        CodeGraph graph,
        IEnumerable<ICustomMetricCalculator>? extraCalculators)
    {
        var allCustom = CalculateCustomMetrics(graph, extraCalculators);
        var byNode = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (metricName, values) in allCustom)
        {
            foreach (var (nodeId, val) in values)
            {
                if (double.IsNaN(val))
                {
                    continue;
                }

                if (!byNode.TryGetValue(nodeId, out var map))
                {
                    map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    byNode[nodeId] = map;
                }
                map[metricName] = val;
            }
        }
        return byNode;
    }

    private static IReadOnlyDictionary<string, double>? MergeNamespaceCustomMetrics(
        Dictionary<string, Dictionary<string, double>> customByNode,
        string nsNodeId,
        string nsName)
    {
        customByNode.TryGetValue(nsNodeId, out var byId);
        customByNode.TryGetValue(nsName, out var byName);
        if (byId is null && byName is null) return null;
        if (byId is not null && byName is null) return byId;
        if (byId is null && byName is not null) return byName;

        var merged = new Dictionary<string, double>(byName!, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in byId!)
        {
            merged[k] = v;
        }
        return merged;
    }

    private static int ParseMetadataInt(CodeNode node, string key, int fallback) =>
        node.Metadata is not null && node.Metadata.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : fallback;

    private static Dictionary<string, int> BuildIncomingCounts(Dictionary<string, HashSet<string>> adjacency)
    {
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, targets) in adjacency)
        {
            foreach (var target in targets)
            {
                incoming[target] = incoming.GetValueOrDefault(target, 0) + 1;
            }
        }
        return incoming;
    }
}
