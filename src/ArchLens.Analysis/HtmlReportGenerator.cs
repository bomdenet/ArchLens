using System.Globalization;
using System.Net;
using System.Text;
using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Generates a self-contained static HTML architecture report featuring:
/// - KPI summary cards
/// - Dependency Structure Matrix (DSM) with cycle highlighting
/// - Top hotspots table (Cyclomatic, Cognitive, LCOM4, Coupling, LOC)
/// - Inline SVG metrics trend comparison (when a baseline snapshot is provided)
/// - Rule violations, DI captive dependencies, and dead code candidates.
/// </summary>
public static class HtmlReportGenerator
{
    public static string Generate(
        ArchitectureSnapshot snapshot,
        RuleCheckReport? rulesReport = null,
        DiAnalysisReport? diReport = null,
        DeadCodeReport? deadCodeReport = null,
        ArchitectureSnapshot? baselineSnapshot = null,
        IReadOnlyList<IArchitectureRule>? rulesForDiff = null,
        NodeKind matrixScope = NodeKind.Namespace,
        int maxMatrixNodes = 35,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null,
        IEnumerable<IEntryPointDetector>? extraEntryPointDetectors = null)
    {
        var extraCalcs = extraMetricCalculators?.ToList();
        var extraDetectors = extraEntryPointDetectors?.ToList();
        var analyzer = new ArchitectureAnalyzerService();
        var overview = analyzer.GetOverview(snapshot);
        var hotspots = analyzer.GetHotspots(snapshot.Graph, sortBy: "cognitive", scope: NodeKind.Type, topN: 25, extraMetricCalculators: extraCalcs);
        var dsmScope = matrixScope is NodeKind.Project or NodeKind.Namespace or NodeKind.Type ? matrixScope : NodeKind.Namespace;
        var adj = snapshot.Graph.BuildAdjacency(dsmScope);
        var cycles = CycleDetector.FindCycles(snapshot.Graph, dsmScope);

        var matrixNodes = adj.Keys
            .OrderByDescending(k => adj[k].Count)
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(Math.Max(1, maxMatrixNodes))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var cycleEdgeSet = new HashSet<(string, string)>();
        foreach (var c in cycles)
        {
            var members = c.Nodes.ToHashSet(StringComparer.Ordinal);
            foreach (var from in matrixNodes.Where(members.Contains))
            {
                foreach (var to in adj[from].Where(members.Contains))
                {
                    cycleEdgeSet.Add((from, to));
                }
            }
        }

        SnapshotDiffResult? diff = baselineSnapshot is not null
            ? analyzer.DiffSnapshots(baselineSnapshot, snapshot, rulesForDiff, extraCalcs, extraDetectors)
            : null;

        var titleTarget = Path.GetFileName(snapshot.TargetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(titleTarget))
        {
            titleTarget = snapshot.TargetPath;
        }

        var sb = new StringBuilder(16384);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine("  <meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:;\" />");
        sb.AppendLine($"  <title>ArchLens Architecture Report — {Esc(titleTarget)}</title>");
        AppendStyles(sb);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<main class=\"container\">");

        sb.AppendLine("  <header>");
        sb.AppendLine($"    <h1>ArchLens Architecture Report</h1>");
        sb.AppendLine($"    <p class=\"meta\">Target: <code>{Esc(snapshot.TargetPath)}</code> &bull; Snapshot: <code>{Esc(snapshot.SnapshotId)}</code> &bull; Captured: {Esc(snapshot.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))}</p>");
        sb.AppendLine("  </header>");

        AppendKpiGrid(sb, overview, rulesReport, diReport, deadCodeReport);

        if (diff is not null && baselineSnapshot is not null)
        {
            AppendTrendSvgSection(sb, baselineSnapshot, snapshot, diff);
        }

        AppendDependencyMatrix(sb, dsmScope, matrixNodes, adj, cycleEdgeSet);
        AppendHotspotsTable(sb, hotspots);

        if (rulesReport is not null)
        {
            AppendViolationsSection(sb, rulesReport);
        }

        if (diReport is { CaptiveDependencyCount: > 0 })
        {
            AppendCaptiveDiSection(sb, diReport);
        }

        if (deadCodeReport is { UnreferencedCount: > 0 })
        {
            AppendDeadCodeSection(sb, deadCodeReport);
        }

        sb.AppendLine("</main>");
        AppendClientScript(sb);
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendKpiGrid(
        StringBuilder sb,
        SolutionOverviewReport overview,
        RuleCheckReport? rules,
        DiAnalysisReport? di,
        DeadCodeReport? dead)
    {
        sb.AppendLine("  <section class=\"kpi-grid\">");
        AppendKpiCard(sb, "Projects", overview.ProjectCount.ToString(), "neutral");
        AppendKpiCard(sb, "Namespaces", overview.NamespaceCount.ToString(), "neutral");
        AppendKpiCard(sb, "Types", overview.TypeCount.ToString(), "neutral");
        AppendKpiCard(sb, "Edges", overview.DependencyEdgeCount.ToString(), "neutral");
        var totalCycles = overview.ProjectCycleCount + overview.NamespaceCycleCount;
        AppendKpiCard(sb, "Cycles", totalCycles.ToString(), totalCycles > 0 ? "danger" : "ok");
        if (rules is not null)
        {
            AppendKpiCard(sb, "Rule Errors", rules.ErrorCount.ToString(), rules.ErrorCount > 0 ? "danger" : "ok");
            AppendKpiCard(sb, "Rule Warnings", rules.WarningCount.ToString(), rules.WarningCount > 0 ? "warn" : "ok");
        }
        if (di is not null)
        {
            AppendKpiCard(sb, "Captive DI", di.CaptiveDependencyCount.ToString(), di.CaptiveDependencyCount > 0 ? "danger" : "ok");
        }
        if (dead is not null)
        {
            AppendKpiCard(sb, "Dead Code", dead.UnreferencedCount.ToString(), dead.UnreferencedCount > 0 ? "warn" : "ok");
        }
        sb.AppendLine("  </section>");
    }

    private static void AppendKpiCard(StringBuilder sb, string label, string value, string tone) =>
        sb.AppendLine($"    <div class=\"kpi-card {tone}\"><div class=\"kpi-val\">{Esc(value)}</div><div class=\"kpi-label\">{Esc(label)}</div></div>");

    private static void AppendTrendSvgSection(
        StringBuilder sb,
        ArchitectureSnapshot baseline,
        ArchitectureSnapshot current,
        SnapshotDiffResult diff)
    {
        var baseTypes = baseline.Graph.GetNodes(NodeKind.Type).Count();
        var currTypes = current.Graph.GetNodes(NodeKind.Type).Count();
        var baseEdges = baseline.Graph.Edges.Count;
        var currEdges = current.Graph.Edges.Count;
        var baseAvgCog = baseline.Graph.GetNodes(NodeKind.Type).Select(t => t.CognitiveComplexity).DefaultIfEmpty(0).Average();
        var currAvgCog = current.Graph.GetNodes(NodeKind.Type).Select(t => t.CognitiveComplexity).DefaultIfEmpty(0).Average();

        var bars = new (string Label, double BaseVal, double CurrVal)[]
        {
            ("Types", baseTypes, currTypes),
            ("Edges", baseEdges, currEdges),
            ("Avg Cognitive", Math.Round(baseAvgCog, 1), Math.Round(currAvgCog, 1))
        };

        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine($"    <h2>Metrics Trend vs Baseline (<code>{Esc(baseline.SnapshotId)}</code> &rarr; <code>{Esc(current.SnapshotId)}</code>)</h2>");
        sb.AppendLine("    <div class=\"trend-wrap\">");
        sb.AppendLine("      <svg viewBox=\"0 0 640 180\" width=\"640\" height=\"180\" role=\"img\" aria-label=\"Metrics trend comparison\">");

        for (var i = 0; i < bars.Length; i++)
        {
            var pairMax = Math.Max(1.0, Math.Max(bars[i].BaseVal, bars[i].CurrVal));
            var x = 40 + i * 200;
            var h1 = Math.Max(2, (int)Math.Round(bars[i].BaseVal / pairMax * 110));
            var h2 = Math.Max(2, (int)Math.Round(bars[i].CurrVal / pairMax * 110));
            var baseLabel = bars[i].BaseVal.ToString("0.#", CultureInfo.InvariantCulture);
            var currLabel = bars[i].CurrVal.ToString("0.#", CultureInfo.InvariantCulture);
            sb.AppendLine($"        <rect x=\"{x}\" y=\"{140 - h1}\" width=\"36\" height=\"{h1}\" fill=\"#94a3b8\" rx=\"3\" />");
            sb.AppendLine($"        <rect x=\"{x + 42}\" y=\"{140 - h2}\" width=\"36\" height=\"{h2}\" fill=\"#2563eb\" rx=\"3\" />");
            sb.AppendLine($"        <text x=\"{x + 39}\" y=\"162\" text-anchor=\"middle\" font-size=\"12\" fill=\"#334155\">{Esc(bars[i].Label)}</text>");
            sb.AppendLine($"        <text x=\"{x + 18}\" y=\"{134 - h1}\" text-anchor=\"middle\" font-size=\"10\" fill=\"#475569\">{baseLabel}</text>");
            sb.AppendLine($"        <text x=\"{x + 60}\" y=\"{134 - h2}\" text-anchor=\"middle\" font-size=\"10\" fill=\"#1d4ed8\">{currLabel}</text>");
        }

        sb.AppendLine("      </svg>");
        sb.AppendLine($"      <p class=\"meta\">Types: <strong>{diff.TypeCountDelta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}</strong> &bull; Edges: <strong>{diff.EdgeCountDelta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}</strong> &bull; Cycles: <strong>{diff.CycleCountDelta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}</strong> &bull; Violations: <strong>{diff.ViolationCountDelta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}</strong></p>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendDependencyMatrix(
        StringBuilder sb,
        NodeKind scope,
        List<string> matrixNodes,
        Dictionary<string, HashSet<string>> adj,
        HashSet<(string, string)> cycleEdges)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine($"    <h2>Dependency Structure Matrix ({Esc(scope.ToString())} — {matrixNodes.Count} nodes)</h2>");
        sb.AppendLine("    <p class=\"meta\">Rows depend on Columns. Red cells indicate cyclic dependencies.</p>");
        sb.AppendLine("    <div class=\"table-scroll\"><table class=\"dsm\">");
        sb.Append("      <thead><tr><th>From \\ To</th>");
        for (var col = 0; col < matrixNodes.Count; col++)
        {
            sb.Append($"<th title=\"{Esc(matrixNodes[col])}\">{col + 1}</th>");
        }
        sb.AppendLine("</tr></thead>");
        sb.AppendLine("      <tbody>");

        for (var row = 0; row < matrixNodes.Count; row++)
        {
            var from = matrixNodes[row];
            var targets = adj.GetValueOrDefault(from);
            sb.Append($"        <tr><th class=\"row-hdr\" title=\"{Esc(from)}\">{row + 1}. {Esc(from)}</th>");
            for (var col = 0; col < matrixNodes.Count; col++)
            {
                var to = matrixNodes[col];
                if (row == col)
                {
                    sb.Append("<td class=\"diag\">&middot;</td>");
                }
                else if (targets is not null && targets.Contains(to))
                {
                    var isCycle = cycleEdges.Contains((from, to));
                    var cls = isCycle ? "dep cycle" : "dep";
                    sb.Append($"<td class=\"{cls}\" title=\"{Esc(from)} -> {Esc(to)}{(isCycle ? " (CYCLE)" : "")}\">&#9679;</td>");
                }
                else
                {
                    sb.Append("<td></td>");
                }
            }
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("      </tbody></table></div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendHotspotsTable(StringBuilder sb, HotspotsReport hotspots)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine("    <h2>Top Architectural Hotspots</h2>");
        sb.AppendLine("    <input id=\"hotspotFilter\" type=\"search\" placeholder=\"Filter types...\" class=\"search\" />");
        sb.AppendLine("    <div class=\"table-scroll\"><table id=\"hotspotsTable\" class=\"data\">");
        sb.AppendLine("      <thead><tr><th>Type</th><th>Max CC</th><th>Max Cognitive</th><th>LCOM4</th><th>Ce</th><th>Ca</th><th>Instability</th><th>LOC</th><th>Location</th></tr></thead>");
        sb.AppendLine("      <tbody>");
        foreach (var item in hotspots.Items)
        {
            var inst = item.Instability.ToString("0.00", CultureInfo.InvariantCulture);
            sb.AppendLine($"        <tr><td><code>{Esc(item.Name)}</code></td><td>{item.MaxMethodComplexity}</td><td>{item.MaxMethodCognitiveComplexity}</td><td>{item.Lcom4}</td><td>{item.EfferentCoupling}</td><td>{item.AfferentCoupling}</td><td>{inst}</td><td>{item.LineCount}</td><td><code>{Esc(item.Location?.ToShortString() ?? "")}</code></td></tr>");
        }
        sb.AppendLine("      </tbody></table></div>");
        sb.AppendLine("  </section>");
    }

    private static void AppendViolationsSection(StringBuilder sb, RuleCheckReport report)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine($"    <h2>Architecture Rule Check ({report.RulesEvaluated} rules: {report.ErrorCount} errors, {report.WarningCount} warnings)</h2>");
        if (report.Violations.Count == 0)
        {
            sb.AppendLine("    <p class=\"ok-text\">All architecture rules passed with 0 violations.</p>");
        }
        else
        {
            sb.AppendLine("    <div class=\"table-scroll\"><table class=\"data\"><thead><tr><th>Severity</th><th>Rule</th><th>Message</th><th>Location</th></tr></thead><tbody>");
            foreach (var v in report.Violations)
            {
                var badge = v.Severity.ToString().ToLowerInvariant();
                sb.AppendLine($"      <tr><td><span class=\"badge {badge}\">{Esc(v.Severity.ToString())}</span></td><td><code>{Esc(v.RuleId)}</code></td><td>{Esc(v.Message)}</td><td><code>{Esc(v.Location?.ToShortString() ?? "")}</code></td></tr>");
            }
            sb.AppendLine("    </tbody></table></div>");
            if (report.Truncated)
            {
                sb.AppendLine($"    <p class=\"meta\">Showing {report.Violations.Count} of {report.TotalViolations} total violations.</p>");
            }
        }
        sb.AppendLine("  </section>");
    }

    private static void AppendCaptiveDiSection(StringBuilder sb, DiAnalysisReport di)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine($"    <h2>DI Captive Dependencies ({di.CaptiveDependencyCount})</h2>");
        sb.AppendLine("    <ul>");
        foreach (var issue in di.CaptiveDependencies)
        {
            sb.AppendLine($"      <li><code>{Esc(issue.DependencyPath)}</code> <span class=\"meta\">{Esc(issue.Location?.ToShortString() ?? "")}</span></li>");
        }
        sb.AppendLine("    </ul>");
        sb.AppendLine("  </section>");
    }

    private static void AppendDeadCodeSection(StringBuilder sb, DeadCodeReport dead)
    {
        sb.AppendLine("  <section class=\"card\">");
        sb.AppendLine($"    <h2>Dead Code Candidates ({dead.UnreferencedCount})</h2>");
        sb.AppendLine("    <div class=\"table-scroll\"><table class=\"data\"><thead><tr><th>Symbol</th><th>Accessibility</th><th>Reason</th><th>Location</th></tr></thead><tbody>");
        foreach (var c in dead.Candidates.Take(100))
        {
            sb.AppendLine($"      <tr><td><code>{Esc(c.FullName)}</code></td><td>{Esc(c.Accessibility.ToString())}</td><td>{Esc(c.Reason)}</td><td><code>{Esc(c.Location?.ToShortString() ?? "")}</code></td></tr>");
        }
        sb.AppendLine("    </tbody></table></div>");
        if (dead.Candidates.Count > 100 || dead.Truncated)
        {
            sb.AppendLine($"    <p class=\"meta\">Showing up to 100 of {dead.UnreferencedCount} dead code candidates.</p>");
        }
        sb.AppendLine("  </section>");
    }

    private static void AppendStyles(StringBuilder sb) => sb.AppendLine("""
          <style>
            :root { --bg: #f8fafc; --card: #ffffff; --text: #0f172a; --muted: #475569; --border: #e2e8f0; }
            body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 24px; }
            .container { max-width: 1280px; margin: 0 auto; }
            h1 { margin: 0 0 6px; font-size: 1.75rem; }
            h2 { margin: 0 0 12px; font-size: 1.2rem; }
            .meta { color: var(--muted); font-size: 0.9rem; margin: 0 0 16px; }
            .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(130px, 1fr)); gap: 12px; margin-bottom: 20px; }
            .kpi-card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 14px; text-align: center; }
            .kpi-card.danger { border-color: #ef4444; background: #fef2f2; }
            .kpi-card.warn { border-color: #f59e0b; background: #fffbeb; }
            .kpi-card.ok { border-color: #22c55e; background: #f0fdf4; }
            .kpi-val { font-size: 1.6rem; font-weight: 700; }
            .kpi-label { font-size: 0.8rem; color: var(--muted); text-transform: uppercase; letter-spacing: .04em; }
            .card { background: var(--card); border: 1px solid var(--border); border-radius: 10px; padding: 18px; margin-bottom: 20px; }
            .table-scroll { overflow-x: auto; }
            table { border-collapse: collapse; width: 100%; font-size: 0.88rem; }
            th, td { border: 1px solid var(--border); padding: 6px 8px; text-align: left; }
            th { background: #f1f5f9; }
            .dsm th, .dsm td { text-align: center; min-width: 24px; padding: 4px; }
            .dsm th.row-hdr { text-align: left; white-space: nowrap; max-width: 300px; overflow: hidden; text-overflow: ellipsis; }
            .dsm td.diag { background: #f1f5f9; color: #94a3b8; }
            .dsm td.dep { color: #2563eb; font-weight: bold; }
            .dsm td.dep.cycle { background: #fee2e2; color: #dc2626; }
            .search { padding: 6px 10px; border: 1px solid var(--border); border-radius: 6px; margin-bottom: 10px; width: 280px; }
            .badge { padding: 2px 8px; border-radius: 999px; font-size: 0.78rem; font-weight: 600; }
            .badge.error { background: #fee2e2; color: #b91c1c; }
            .badge.warning { background: #fef3c7; color: #b45309; }
            .badge.info { background: #e0f2fe; color: #0369a1; }
            .ok-text { color: #15803d; font-weight: 600; }
            code { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.85em; }
          </style>
        """);

    private static void AppendClientScript(StringBuilder sb) => sb.AppendLine("""
        <script>
          const input = document.getElementById('hotspotFilter');
          if (input) {
            input.addEventListener('input', () => {
              const q = input.value.toLowerCase();
              for (const row of document.querySelectorAll('#hotspotsTable tbody tr')) {
                row.style.display = row.textContent.toLowerCase().includes(q) ? '' : 'none';
              }
            });
          }
        </script>
        """);

    private static string Esc(string text) => WebUtility.HtmlEncode(text);
}
