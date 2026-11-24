using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Computes the transitive "blast radius" (dependents) of modifying a given type.
/// </summary>
public static class ImpactAnalyzer
{
    public static ImpactAnalysisResult Analyze(CodeGraph graph, CodeNode targetNode, int maxDepth = 4, int maxItems = 50)
    {
        // Build reverse adjacency for types
        var reverseAdj = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == DependencyKind.ProjectReference || string.Equals(edge.FromId, edge.ToId, StringComparison.Ordinal))
            {
                continue;
            }

            if (graph.FindNode(edge.FromId)?.Kind != NodeKind.Type ||
                graph.FindNode(edge.ToId)?.Kind != NodeKind.Type)
            {
                continue;
            }

            if (!reverseAdj.TryGetValue(edge.ToId, out var dependents))
            {
                dependents = new HashSet<string>(StringComparer.Ordinal);
                reverseAdj[edge.ToId] = dependents;
            }
            dependents.Add(edge.FromId);
        }

        var visited = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [targetNode.Id] = 0
        };
        var queue = new Queue<(string NodeId, int Depth)>();
        queue.Enqueue((targetNode.Id, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            if (!reverseAdj.TryGetValue(current, out var dependents))
            {
                continue;
            }

            foreach (var dep in dependents)
            {
                if (visited.TryAdd(dep, depth + 1))
                {
                    queue.Enqueue((dep, depth + 1));
                }
            }
        }

        visited.Remove(targetNode.Id);

        var allImpacted = visited
            .Select(kvp => (Node: graph.FindNode(kvp.Key), Distance: kvp.Value))
            .Where(x => x.Node is not null)
            .Select(x => new ImpactNode(x.Node!.Id, x.Node.FullName, x.Node.ProjectName, x.Distance, x.Node.Location))
            .OrderBy(n => n.Distance)
            .ThenBy(n => n.FullName, StringComparer.Ordinal)
            .ToList();

        var directCount = allImpacted.Count(n => n.Distance == 1);
        var affectedProjects = allImpacted
            .Select(n => n.ProjectName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return new ImpactAnalysisResult(
            TargetNodeId: targetNode.Id,
            TargetFullName: targetNode.FullName,
            DirectDependentsCount: directCount,
            TotalTransitiveDependentsCount: allImpacted.Count,
            AffectedProjects: affectedProjects,
            ImpactedNodes: allImpacted.Take(Math.Max(1, maxItems)).ToList());
    }
}
