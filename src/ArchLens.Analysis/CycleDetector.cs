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

                    cycles.Add(new CycleInfo(scope, component, internalEdges));
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
}
