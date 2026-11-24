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

            var maxMethodComplexity = 0;
            if (node.Metadata is not null &&
                node.Metadata.TryGetValue("maxMethodComplexity", out var rawMax) &&
                int.TryParse(rawMax, out var parsedMax))
            {
                maxMethodComplexity = parsedMax;
            }
            else
            {
                maxMethodComplexity = node.CyclomaticComplexity;
            }

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
                Location: node.Location);
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
            var maxMethod = types.Max(t =>
                t.Metadata is not null && t.Metadata.TryGetValue("maxMethodComplexity", out var raw) && int.TryParse(raw, out var v)
                    ? v
                    : t.CyclomaticComplexity);
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
                ConstructorParameterCount: maxCtor);
        }

        return result;
    }

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
