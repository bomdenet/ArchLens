using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Finds strongly connected components (dependency cycles) using Tarjan's algorithm in O(V + E).
/// </summary>
public static class CycleDetector
{
    public static IReadOnlyList<CycleInfo> FindCycles(CodeGraph graph, NodeKind scope)
    {
        var adjacency = graph.BuildAdjacency(scope);
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new List<CycleInfo>();

        void StrongConnect(string v)
        {
            indices[v] = index;
            lowLink[v] = index;
            index++;
            stack.Push(v);
            onStack.Add(v);

            if (adjacency.TryGetValue(v, out var neighbors))
            {
                foreach (var w in neighbors)
                {
                    if (!indices.ContainsKey(w))
                    {
                        StrongConnect(w);
                        lowLink[v] = Math.Min(lowLink[v], lowLink[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowLink[v] = Math.Min(lowLink[v], indices[w]);
                    }
                }
            }

            if (lowLink[v] == indices[v])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                } while (!string.Equals(w, v, StringComparison.Ordinal));

                if (component.Count > 1)
                {
                    component.Sort(StringComparer.Ordinal);
                    var memberSet = component.ToHashSet(StringComparer.Ordinal);
                    var internalEdges = component.Sum(n =>
                        adjacency.TryGetValue(n, out var adj)
                            ? adj.Count(t => memberSet.Contains(t))
                            : 0);

                    var cyclePath = FindShortestDirectedCyclePath(component, memberSet, adjacency);
                    var sampleEdges = FindRepresentativeCycleEdges(graph, scope, cyclePath);

                    cycles.Add(new CycleInfo(scope, component, internalEdges, cyclePath, sampleEdges));
                }
            }
        }

        foreach (var node in adjacency.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!indices.ContainsKey(node))
            {
                StrongConnect(node);
            }
        }

        return cycles
            .OrderByDescending(c => c.Nodes.Count)
            .ThenBy(c => c.Nodes[0], StringComparer.Ordinal)
            .ToList();
    }

    private static List<string>? FindShortestDirectedCyclePath(
        List<string> sortedComponent,
        HashSet<string> memberSet,
        Dictionary<string, HashSet<string>> adjacency)
    {
        var start = sortedComponent[0];
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        if (!adjacency.TryGetValue(start, out var startNeighbors))
        {
            return null;
        }

        foreach (var next in startNeighbors.Where(memberSet.Contains).OrderBy(x => x, StringComparer.Ordinal))
        {
            parent[next] = start;
            queue.Enqueue(next);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors.Where(memberSet.Contains).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (string.Equals(next, start, StringComparison.Ordinal))
                {
                    var path = new List<string> { start };
                    var cursor = current;
                    while (!string.Equals(cursor, start, StringComparison.Ordinal))
                    {
                        path.Add(cursor);
                        cursor = parent[cursor];
                    }
                    path.Add(start);
                    path.Reverse();
                    return path;
                }

                if (!parent.ContainsKey(next))
                {
                    parent[next] = current;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static List<CycleEdgeSample>? FindRepresentativeCycleEdges(
        CodeGraph graph,
        NodeKind scope,
        List<string>? cyclePath)
    {
        if (cyclePath is null || cyclePath.Count < 2)
        {
            return null;
        }

        var samples = new List<CycleEdgeSample>();
        for (var i = 0; i < cyclePath.Count - 1; i++)
        {
            var fromBucket = cyclePath[i];
            var toBucket = cyclePath[i + 1];

            DependencyEdge? best = null;
            foreach (var edge in graph.Edges)
            {
                var fromNode = graph.FindNode(edge.FromId);
                var toNode = graph.FindNode(edge.ToId);
                if (fromNode is null || toNode is null)
                {
                    continue;
                }

                var matches = scope switch
                {
                    NodeKind.Project =>
                        (edge.Kind == DependencyKind.ProjectReference && fromNode.ProjectName == fromBucket && toNode.ProjectName == toBucket) ||
                        (fromNode.Kind == NodeKind.Type && toNode.Kind == NodeKind.Type && fromNode.ProjectName == fromBucket && toNode.ProjectName == toBucket),
                    NodeKind.Namespace =>
                        fromNode.Kind == NodeKind.Type && toNode.Kind == NodeKind.Type &&
                        fromNode.Namespace == fromBucket && toNode.Namespace == toBucket,
                    _ =>
                        fromNode.Id == fromBucket && toNode.Id == toBucket
                };

                if (!matches)
                {
                    continue;
                }

                // Prefer a concrete type-level edge with source location over a bare ProjectReference edge
                if (best is null ||
                    (best.Kind == DependencyKind.ProjectReference && edge.Kind != DependencyKind.ProjectReference) ||
                    (best.Location is null && edge.Location is not null))
                {
                    best = edge;
                    if (best.Kind != DependencyKind.ProjectReference && best.Location is not null)
                    {
                        break;
                    }
                }
            }

            if (best is not null)
            {
                samples.Add(new CycleEdgeSample(best.FromId, best.ToId, best.Kind, best.Location ?? graph.FindNode(best.FromId)?.Location));
            }
        }

        return samples.Count > 0 ? samples : null;
    }

    internal static bool IsMigrationNamespaceCycle(IReadOnlyList<string> nodes) =>
        nodes.Any(n => n.EndsWith(".Migrations", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains(".Migrations.", StringComparison.OrdinalIgnoreCase));

    // Matches cycles rooted at a parent namespace where every other node is a descendant (A <-> A.*).
    // Sibling cycles (A.Sub1 <-> A.Sub2) and cross-root cycles (A <-> B) are not matched.
    internal static bool IsParentChildNamespaceCycle(IReadOnlyList<string> nodes)
    {
        if (nodes.Count < 2) return false;
        var root = nodes[0];
        for (var i = 1; i < nodes.Count; i++)
        {
            if (nodes[i].Length < root.Length)
            {
                root = nodes[i];
            }
        }

        var prefix = root + ".";
        for (var i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i], root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!nodes[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static string? GetDefaultNamespaceSuppressionReason(CycleInfo cycle)
    {
        if (cycle.Scope != NodeKind.Namespace) return null;
        if (IsMigrationNamespaceCycle(cycle.Nodes)) return "migrations";
        if (IsParentChildNamespaceCycle(cycle.Nodes)) return "parent_child";
        return null;
    }
}
