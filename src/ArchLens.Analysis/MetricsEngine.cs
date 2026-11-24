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

    public IReadOnlyDictionary<string, NodeMetrics> CalculateTypeMetrics(CodeGraph graph)
    {
        var typeAdj = graph.BuildAdjacency(NodeKind.Type);
        var incomingCounts = BuildIncomingCounts(typeAdj);
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
                Lcom4: node.Lcom4);
        }

        return result;
    }

    public IReadOnlyDictionary<string, NodeMetrics> CalculateMethodMetrics(CodeGraph graph)
    {
        var methodAdj = graph.BuildAdjacency(NodeKind.Method);
        var incomingCounts = BuildIncomingCounts(methodAdj);
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
                Lcom4: 0);
        }

        return result;
    }

    public IReadOnlyDictionary<string, NodeMetrics> CalculateNamespaceMetrics(CodeGraph graph)
    {
        var nsAdj = graph.BuildAdjacency(NodeKind.Namespace);
        var incomingCounts = BuildIncomingCounts(nsAdj);
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

            result[ns] = new NodeMetrics(
                NodeId: $"ns:{ns}",
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
                Lcom4: maxLcom4);
        }

        return result;
    }

    private static int ParseMetadataInt(CodeNode node, string key, int fallback) =>
        node.Metadata is not null && node.Metadata.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : fallback;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> CalculateCustomMetrics(CodeGraph graph)
    {
        var output = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var calc in _customCalculators)
        {
            output[calc.MetricName] = calc.Calculate(graph);
        }
        return output;
    }

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
