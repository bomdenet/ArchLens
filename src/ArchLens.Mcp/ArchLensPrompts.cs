using System.ComponentModel;
using ArchLens.Analysis;
using ModelContextProtocol.Server;

namespace ArchLens.Mcp;

/// <summary>
/// Built-in standardized MCP Prompts for AI coding agents performing architectural audits,
/// layer refactoring plans, PR reviews, and dead-code cleanups with ArchLens.
/// </summary>
[McpServerPromptType]
public static class ArchLensPrompts
{
    [McpServerPrompt(Name = "audit-architecture")]
    [Description("Run a comprehensive, structured architectural audit of a C#/.NET codebase using ArchLens tools and produce a prioritized remediation plan.")]
    public static string AuditArchitecture(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Optional architectural preset or style to audit against (built-in presets: 'clean-architecture', 'modular-monolith', 'vertical-slice', 'ddd').")] string? style = null)
    {
        RequireNonEmpty(path, nameof(path));
        var safePath = EscapeArg(path);
        var isBinary = IsBinaryTarget(path);
        string? resolvedPreset = !string.IsNullOrWhiteSpace(style) &&
                                 ArchitecturePresets.TryResolvePresetId(style.Trim(), out var canonicalPreset)
            ? canonicalPreset
            : null;
        var hasBuiltInPreset = resolvedPreset is not null;

        var styleNote = string.IsNullOrWhiteSpace(style)
            ? "Auto-discover `.archlens.yaml` if present; otherwise evaluate structural health and cycles."
            : hasBuiltInPreset
                ? $"Evaluate against the built-in `{resolvedPreset}` preset (and any existing `.archlens.yaml`)."
                : $"Evaluate against the `{style.Trim()}` architectural style conceptually (auto-discovering `.archlens.yaml` if present; note `{style.Trim()}` is not a built-in `preset` ID — built-in presets are `clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`).";
        var presetArg = resolvedPreset is null ? "" : $", preset: \"{EscapeArg(resolvedPreset)}\"";

        var step2 = isBinary
            ? $"2. **Rule Compliance**: Call `check_rules(path: \"{safePath}\"{presetArg})` to identify architecture rule violations and dependency cycles (note: compiled `.dll`/`.exe` targets use the IL backend, so omit `includeAnalyzers` and `di_analysis`)."
            : $"2. **Rule Compliance**: Call `check_rules(path: \"{safePath}\"{presetArg}, includeAnalyzers: true)` to identify all architecture rule violations and Roslyn analyzer diagnostics (`Design`, `Maintainability`, `Performance`).";
        var step3 = isBinary
            ? "3. **Complexity & Coupling Hotspots**: Call `hotspots` twice — once with `sortBy: \"complexity\"` and once with `sortBy: \"coupling\"` — to locate complex types and high-coupling hubs (note: IL backend does not compute cognitive complexity)."
            : "3. **Complexity & Cohesion Hotspots**: Call `hotspots` twice — once with `sortBy: \"cognitive\"` and once with `sortBy: \"lcom4\"` — to locate hard-to-read methods and low-cohesion God classes (`LCOM4 >= 2`).";
        var step4 = isBinary
            ? "4. **Dead Code Audit**: Call `dead_code(path: \"" + safePath + "\", scope: \"type\")` to flag unreferenced types and unreachable dead islands."
            : $"4. **DI Container Safety**: Call `di_analysis(path: \"{safePath}\")` to detect captive dependencies (`Singleton -> Scoped`, direct or via `Transient`) and unnecessary single-implementation interfaces.\n5. **Dead Code Audit**: Call `dead_code(path: \"{safePath}\", scope: \"type\")` to flag unreferenced types and unreachable dead islands.";
        var finalStepNum = isBinary ? "5" : "6";

        return $"""
            Perform a thorough, evidence-based architectural audit of the C#/.NET target at `{path}`.
            {styleNote}

            Follow this exact workflow using ArchLens MCP tools:
            1. **Map & Health Check**: Call `overview(path: "{safePath}")` to inspect projects, references, namespace counts, cycle counts, and workspace load diagnostics.
            {step2}
            {step3}
            {step4}
            {finalStepNum}. **Deep Ad-Hoc Queries (if needed)**: Use `query` (see resource `archlens://schema/sql`) to inspect cross-project coupling concentration or unstable namespaces (`distance_from_main_sequence > 0.5`).

            Deliver a concise, actionable report organized into:
            - **Executive Summary** (health score, cycle count, error/warning totals)
            - **Critical Issues (Fix Now)** (cycles, layer violations, captive DI dependencies — with exact `file:line`)
            - **High-ROI Refactoring Targets** (top complex/low-cohesion types with suggested splits)
            - **Cleanup Opportunities** (dead code candidates & single-implementation abstractions)
            - **Prioritized Action Plan** (ordered steps with blast-radius notes).
            """;
    }

    [McpServerPrompt(Name = "plan-layer-refactoring")]
    [Description("Design a safe, incremental refactoring plan to break dependency cycles or decouple a target symbol/layer with zero regressions.")]
    public static string PlanLayerRefactoring(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Target symbol, namespace, or layer to refactor (e.g. 'MyApp.Domain.*' or 'OrderService').")] string target)
    {
        RequireNonEmpty(path, nameof(path));
        RequireNonEmpty(target, nameof(target));
        var safePath = EscapeArg(path);
        var safeTarget = EscapeArg(target);

        return $"""
            Create a safe, step-by-step refactoring plan for `{target}` in `{path}` without breaking architectural boundaries or introducing new cycles.

            Execute these ArchLens steps in order:
            1. **Establish Baseline**: Call `diff(path: "{safePath}", resetBaseline: true)` so every subsequent edit can be verified quantitatively.
            2. **Inspect Topology**: Call `dependency_graph(path: "{safePath}", scope: "namespace")` (and `scope: "type"`, `filter: "{safeTarget}"` if scoped) to identify all cycles and illegal layer crossings involving `{target}`.
            3. **Inspect Symbol & Blast Radius**: For the key offending types in `{target}`, call `inspect_symbol` and `impact(maxDepth: 4)` to map every direct and transitive dependent and affected project.
            4. **Design Concrete Cut Points**: Identify the minimum set of edges to invert (Dependency Inversion via interface extraction), relocate, or split (guided by `LCOM4` and `Afferent`/`Efferent` coupling).
            5. **Verification Criteria**: Specify the exact `diff` acceptance criteria (`cycleCountDelta < 0`, `violationCountDelta <= 0`, `newViolations` empty) to verify after each incremental step.

            Present the plan as ordered, atomic refactoring steps with exact file paths, interfaces/types to extract or move, and risk level per step.
            """;
    }

    [McpServerPrompt(Name = "review-pr-architecture")]
    [Description("Review architectural regressions introduced in the current branch/working tree against the saved .archlens-baseline.json or session baseline.")]
    public static string ReviewPrArchitecture(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Optional path to .archlens-baseline.json.")] string? baselineFile = null)
    {
        RequireNonEmpty(path, nameof(path));
        var safePath = EscapeArg(path);
        var isBinary = IsBinaryTarget(path);
        var baseArg = string.IsNullOrWhiteSpace(baselineFile) ? "" : $", baselineFile: \"{EscapeArg(baselineFile)}\"";

        var diStep = isBinary
            ? "2. For any symbol appearing in `newViolations` or `addedCycles`, call `inspect_symbol` to understand the exact dependency edge and suggest the minimal fix (note: `di_analysis` requires a C# source target and is skipped for compiled `.dll`/`.exe` binaries)."
            : $"2. Call `di_analysis(path: \"{safePath}\")` to inspect current captive DI dependencies (note: `di_analysis` reports the current snapshot; to verify whether a captive dependency is newly introduced by this PR, check `newViolations` for `no_captive_dependencies` / `CLEAN-005` or compare against the base branch).\n3. For any symbol appearing in `newViolations` or `addedCycles`, call `inspect_symbol` to understand the exact dependency edge (`file:line`) and suggest the minimal fix.";

        return $"""
            Review the architectural impact of the current branch/working-tree changes in `{path}`.

            1. Call `diff(path: "{safePath}"{baseArg})` to compare current cycles, type/edge deltas, and rule violations against the baseline.
               - **Important**: If `diff` returns `status: "baseline_set"`, no pre-change baseline existed yet; also call `check_rules(path: "{safePath}")` and report the verdict as **INCONCLUSIVE (no pre-change baseline)** (advising the caller to pass `baselineFile` from the base branch).
               - If `diff` returns `identicalSnapshot: true`: when `baselineSource` is `"session"`, no code edits occurred since the session baseline was captured (verify whether a pre-change `baselineFile` was intended); when `baselineSource` is an explicit `baselineFile`, the current code is architecturally identical to that saved baseline (0 code deltas; call `check_rules` if policy YAML changed).
            {diStep}

            Summarize as a concise PR review verdict: **APPROVE** (verified diff against a pre-change baseline with 0 new errors/cycles/captive regressions), **REQUEST CHANGES** (listing each regression with `file:line` and concrete fix), or **INCONCLUSIVE** (no pre-change baseline available, listing current rule/DI findings).
            """;
    }

    [McpServerPrompt(Name = "investigate-dead-code")]
    [Description("Identify and safely triage dead/unreferenced types and methods before deletion using entry-point reachability and blast-radius verification.")]
    public static string InvestigateDeadCode(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null)
    {
        RequireNonEmpty(path, nameof(path));
        var safePath = EscapeArg(path);
        var filterArg = string.IsNullOrWhiteSpace(filter) ? "" : $", filter: \"{EscapeArg(filter)}\"";

        return $"""
            Conduct a safe dead-code triage in `{path}`:
            1. Call `dead_code(path: "{safePath}", scope: "type"{filterArg})` and `dead_code(path: "{safePath}", scope: "method"{filterArg})`.
            2. Group candidates into:
               - **Safe to delete** (private/internal symbols with 0 references and no reflection/DI/serialization usage)
               - **Verify reflection/framework usage** (public APIs, DTOs serialized via JSON/ORM, dynamically activated types)
               - **Unreachable islands** (mutually referencing dead clusters that should be removed together).
            3. For each candidate proposed for removal, double-check with `impact` (passing `includeMethods: true` for method candidates) that `totalTransitiveDependentsCount == 0` (or all dependents are inside the same dead island).
            """;
    }

    private static void RequireNonEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Parameter '{paramName}' cannot be empty or whitespace.");
        }
    }

    private static bool IsBinaryTarget(string path)
    {
        var ext = Path.GetExtension(path.Trim());
        return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeArg(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
