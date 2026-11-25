# ArchLens MCP Tools Specification

Tools follow a **coarse-grained + token-efficient** design: a focused set of 12 tools without redundant `archlens_` prefixes (MCP clients namespace tools by server name), compact JSON omitting `null` fields, relative `file:line` locations, and built-in pagination/size caps.

> **Plugin Security:** Repository-local plugins in `.archlens/plugins/*.dll` are disabled by default and loaded only when explicitly opted in at host startup (`archlens-mcp --allow-plugins` or `ARCHLENS_ALLOW_PLUGINS=1`).

---

## Tool Catalog

| Tool | Purpose | Key Parameters |
| :--- | :--- | :--- |
| `overview` | Single-call solution map: projects, references, top namespaces, cycle counts, MSBuild load status | `path`, `refresh?`, `includeTests?`, `includeMethods?`, `summaryOnly?`, `targetFramework?`, `backend?` |
| `dependency_graph` | Dependency graph and detected cycles (including method-level call graph) | `path`, `scope` (`project\|namespace\|type\|method`), `filter?`, `maxNodes?`, `maxEdges?`, `format?`, `refresh?`, `targetFramework?`, `backend?` |
| `hotspots` | Top-N hotspots by selected metric (including composite `god_class`, Cognitive Complexity, LCOM4, and custom plugin metrics) | `path`, `sortBy` (`complexity\|god_class\|cognitive\|lcom4\|coupling\|afferent\|instability\|distance\|lines\|ctor\|<custom>`), `scope` (`type\|namespace\|method`), `filter?`, `topN?`, `fields?`, `refresh?`, `targetFramework?`, `backend?` |
| `check_rules` | Evaluate `.archlens.yaml`, built-in presets, and enabled plugins (optionally with Roslyn analyzer diagnostics); returns `snapshotId`/`capturedAt`, reports `suppressedCycles`, and skips degraded projects by default | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `maxViolations?`, `includeDegraded?`, `refresh?`, `includeTests?`, `includeMethods?`, `includeAnalyzers?`, `analyzerCategories?`, `targetFramework?`, `backend?` |
| `query` | Execute read-only SQL (`SELECT`/`WITH`) over an in-memory SQLite projection (`nodes`, `edges`, `metrics`, `projects`, `di_registrations`, `analyzer_diagnostics`, `custom_metrics`, `schema_info`) with `sqlite3_set_authorizer`, column hints, and `includeSchema: true` | `path?`, `sql?`, `includeSchema?`, `maxRows?`, `includeMethods?`, `includeTests?`, `includeAnalyzers?`, `refresh?`, `targetFramework?`, `backend?` |
| `export_diagram` | Export the dependency graph to Mermaid, Graphviz DOT, or Structurizr DSL with cycle highlighting | `path`, `format` (`mermaid\|dot\|structurizr`), `scope`, `filter?`, `maxNodes?`, `refresh?`, `targetFramework?`, `backend?` |
| `dead_code` | Detect potentially dead types/methods via reachability BFS with `auto\|app\|lib\|strict` profiles, Blazor/XAML/ASP.NET/DI detectors, test-support exclusion, and `.archlens.yaml` `acknowledge:` support | `path`, `scope` (`type\|method`), `profile?`, `treatPublicApiAsEntryPoint?`, `filter?`, `maxItems?`, `includeTests?`, `refresh?`, `targetFramework?`, `backend?` |
| `di_analysis` | Analyze `IServiceCollection` registrations: compact lifetime/project summary, filters, captive dependencies, and single-implementation interfaces (`includeRegistrations` defaults to `false`) | `path`, `includeRegistrations?`, `lifetime?`, `project?`, `serviceType?`, `onlyCaptive?`, `onlyUnregistered?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `analyzers` | Run Roslyn Analyzers (`NetAnalyzers`/`Roslynator`) filtered by category and severity; defaults to `minSeverity="warning"`, reports `analyzersLoaded`/`hiddenBySeverityCount`/`note`, and skips generated/Migrations files | `path`, `categories?`, `minSeverity?`, `elevateToWarning?`, `filter?`, `maxItems?`, `includeDegraded?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `inspect_symbol` | Inspect a single type or method: metrics (CC, Cognitive, LCOM4, coupling, custom metrics), outgoing dependencies, incoming dependents, and source location | `path`, `symbol`, `fields?`, `maxEdges?`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `impact` | Transitive blast radius for a type or method (excludes DI registrations and tests by default). Auto-summarizes when fan-in > 20 unless `summaryOnly: false` or `maxItems` is explicitly passed | `path`, `symbol`, `maxDepth?`, `maxItems?`, `summaryOnly?`, `excludeRegistrations?`, `includeTests?`, `strict?`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `diff` | Incrementally reload modified files and compare against the session baseline or `.archlens-baseline.json` on disk | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `resetBaseline?`, `baselineFile?`, `saveBaseline?`, `includeTests?`, `includeMethods?`, `targetFramework?`, `backend?` |

---

## MCP Prompts

Standardized workflows available via `prompts/list` and `prompts/get`:

| Prompt | Purpose | Arguments |
| :--- | :--- | :--- |
| `audit-architecture` | End-to-end architectural audit (overview, rules, analyzers, hotspots, DI, dead code, SQL) with a prioritized remediation plan | `path`, `style?` |
| `plan-layer-refactoring` | Step-by-step plan to break cycles or refactor a layer with baseline, blast radius, and `diff` acceptance criteria | `path`, `target` |
| `review-pr-architecture` | Architectural review of the current branch/PR against a baseline with APPROVE / REQUEST CHANGES verdict | `path`, `baselineFile?` |
| `investigate-dead-code` | Safe triage workflow for dead-code candidates and unreachable islands before deletion | `path`, `filter?` |

---

## MCP Resources

| URI | Purpose |
| :--- | :--- |
| `archlens://schema/sql` | Full table/column schema and example queries for `query` |
| `archlens://presets` | Catalog of built-in presets (`clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`) and `.archlens.yaml` templates |
| `archlens://rules/reference` | Reference of `.archlens.yaml` rule types, supported metrics, and plugin conventions |

---

## Recommended Agent Workflow

1. **Orient:** call `overview` — verify `loadStatus` has no fatal MSBuild load errors, review projects and cycle counts.
2. **Diagnose:** call `check_rules`, `hotspots(sortBy: "god_class")`, `hotspots(sortBy: "cognitive")`, and `hotspots(sortBy: "lcom4")`.
3. **Plan Refactoring:** before editing a core type or interface, call `inspect_symbol` and `impact` to gauge blast radius.
4. **Verify (Feedback Loop):** after editing files, call `diff` to confirm `cycleCountDelta <= 0`, `violationCountDelta <= 0`, and `newViolations` is empty.
