# ArchLens MCP Tools Specification

Tools follow a **coarse-grained + token-efficient** design: a focused set of 13 tools without redundant `archlens_` prefixes (MCP clients namespace tools by server name), compact JSON omitting `null` fields, relative `file:line` locations, and built-in pagination/size caps.

> **Plugin Security:** Repository-local plugins in `.archlens/plugins/*.dll` are disabled by default and loaded only when explicitly opted in at host startup (`archlens-mcp --allow-plugins` or `ARCHLENS_ALLOW_PLUGINS=1`).

---

## Tool Catalog

| Tool | Purpose | Key Parameters |
| :--- | :--- | :--- |
| `overview` | Single-call solution map: projects, references, top namespaces, actionable + raw/suppressed cycle counts, MSBuild load status | `path`, `refresh?`, `includeTests?`, `includeMethods?`, `summaryOnly?`, `targetFramework?`, `backend?` |
| `dependency_graph` | Dependency graph and detected cycles (scoped to `filter`, aligned with default namespace cycle suppression; includes method-level call graph) | `path`, `scope` (`project\|namespace\|type\|method`), `filter?`, `maxNodes?`, `maxEdges?`, `includeSuppressedCycles?`, `format?`, `refresh?`, `targetFramework?`, `backend?` |
| `hotspots` | Top-N hotspots by selected metric (including composite `god_class` with `godClassScore`, peak and total Cyclomatic/Cognitive Complexity, LCOM4, and custom plugin metrics) | `path`, `sortBy` (`complexity\|total_complexity\|god_class\|cognitive\|total_cognitive\|lcom4\|coupling\|afferent\|instability\|distance\|lines\|ctor\|<custom>`), `scope` (`type\|namespace\|method`), `filter?`, `topN?`, `fields?`, `refresh?`, `targetFramework?`, `backend?` |
| `check_rules` | Evaluate `.archlens.yaml`, built-in presets (including comma-separated presets), and enabled plugins (optionally with Roslyn analyzer diagnostics); returns `snapshotId`/`capturedAt`/`evaluatedRules`, reports `suppressedCycles`, flags 0-match `rootNamespace` as `ARCHLENS-CONFIG-001` error with detected prefixes, and skips degraded projects by default | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `maxViolations?`, `includeDegraded?`, `refresh?`, `includeTests?`, `includeMethods?`, `includeAnalyzers?`, `analyzerCategories?`, `targetFramework?`, `backend?` |
| `query` | Execute read-only SQL (`SELECT`/`WITH`) over an in-memory SQLite projection (`nodes`, `edges`, `metrics`, `projects`, `di_registrations`, `analyzer_diagnostics`, `custom_metrics`, `schema_info`) with `sqlite3_set_authorizer`, column hints, and `includeSchema: true` | `path?`, `sql?`, `includeSchema?`, `maxRows?`, `includeMethods?`, `includeTests?`, `includeAnalyzers?`, `refresh?`, `targetFramework?`, `backend?` |
| `export_diagram` | Export the dependency graph to Mermaid, Graphviz DOT, or Structurizr DSL with cycle highlighting and truncation comments | `path`, `format` (`mermaid\|dot\|structurizr`), `scope`, `filter?`, `maxNodes?`, `refresh?`, `targetFramework?`, `backend?` |
| `dead_code` | Detect potentially dead types/methods via reachability BFS with `auto\|app\|lib\|strict` profiles, Blazor/XAML/ASP.NET/EF Core/DI detectors, test-support exclusion, and `.archlens.yaml` `acknowledge:` support. Use `scope="type"` first for highest signal; `scope="method"` skips implicit/parameterless/DTO-record serializer constructors on reachable types | `path`, `scope` (`type\|method`), `profile?`, `treatPublicApiAsEntryPoint?`, `filter?`, `maxItems?`, `includeTests?`, `refresh?`, `targetFramework?`, `backend?` |
| `di_analysis` | Analyze `IServiceCollection` registrations: compact lifetime/project summary, filters, captive dependencies, and single-implementation interfaces (`includeRegistrations` defaults to `false`) | `path`, `includeRegistrations?`, `lifetime?`, `project?`, `serviceType?`, `onlyCaptive?`, `onlyUnregistered?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `analyzers` | Run Roslyn Analyzers (`NetAnalyzers`/`Roslynator`) filtered by category and severity; defaults to `minSeverity="warning"`, reports `analyzersLoaded`/`hiddenBySeverityCount`/`note`, and skips generated/Migrations files | `path`, `categories?`, `minSeverity?`, `elevateToWarning?`, `filter?`, `maxItems?`, `includeDegraded?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `inspect_symbol` | Inspect a single type or method: metrics (CC, Cognitive, LCOM4, coupling, custom metrics), outgoing dependencies, incoming dependents, and source location | `path`, `symbol`, `fields?`, `maxEdges?`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `impact` | Transitive blast radius for a type or method (excludes DI registrations and tests by default). Auto-summarizes when fan-in > 20 unless `summaryOnly: false` or `maxItems` is explicitly passed | `path`, `symbol`, `maxDepth?`, `maxItems?`, `summaryOnly?`, `excludeRegistrations?`, `includeTests?`, `strict?`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `diff` | Incrementally reload modified files and compare against the session baseline or `.archlens-baseline.json` on disk | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `resetBaseline?`, `baselineFile?`, `saveBaseline?`, `includeTests?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `give_feedback` | Report a bug, wrong/confusing result, missing capability, or performance problem in **ArchLens itself**; sanitizes credentials and user paths, deduplicates by fingerprint, queues offline and retries at the next server start | `category`, `severity`, `summary`, `details?`, `area?`, `tool?` |

---

## Feedback Delivery (`give_feedback`)

Delivery goes to the **shared bomdenet intake** by default, so no token is needed on the user's machine. Set
`ARCHLENS_FEEDBACK_MODE=off` to disable reporting completely, or point `ARCHLENS_FEEDBACK_URL` at your own endpoint.

| Variable | Default | Purpose |
| :--- | :--- | :--- |
| `ARCHLENS_FEEDBACK_TOKEN` | — | Fine-grained GitHub PAT with `issues: write` on the target repository |
| `GITHUB_TOKEN` | — | Fallback when the ArchLens-specific token is unset |
| `ARCHLENS_FEEDBACK_REPO` | `bomdenet/ArchLens` | Target repository in `owner/name` form |
| `ARCHLENS_FEEDBACK_URL` | `https://feedback.bomdenet.com/api/feedback` | HTTP intake endpoint; takes precedence over direct GitHub calls, so end users need no token |
| `ARCHLENS_FEEDBACK_API` | `https://api.github.com` | API base override (GitHub Enterprise or a self-hosted mock) |
| `ARCHLENS_FEEDBACK_MODE` | `auto` | `auto` (deliver to the intake), `outbox` (queue only, nothing leaves the machine), `off` (disable the tool) |
| `ARCHLENS_FEEDBACK_MAX_PER_HOUR` | `5` | Sliding one-hour delivery limit, persisted across restarts |
| `ARCHLENS_FEEDBACK_OUTBOX` | `%LOCALAPPDATA%/ArchLens/feedback` | Queue of undelivered reports (`failed/` after 5 attempts) |

Behavior notes:

- **Sanitization at two points** (tool input and issue body): tokens, keys, `Authorization` headers and `C:\Users\<name>` / `/home/<name>` paths are replaced with placeholders, HTML comments are stripped so the dedupe marker cannot be forged, and lengths are capped. This is a guard against mistakes, not a DLP system — issue bodies are public.
- **Deduplication** by `category + area + summary` fingerprint: an existing `agent-feedback` issue with the same marker returns `duplicate` instead of a second issue, and an identical queued report is not queued twice.
- **Labels:** issues are created with the `agent-feedback` label; a repository that lacks the label gets an unlabeled issue instead of a failed request.
- **Never blocks analysis:** transport failures return `queued` with the error, keep the report on disk, and retry once at the next server start.

### Proxy contract (`ARCHLENS_FEEDBACK_URL`)

A proxy lets distributed users report feedback without holding a repository PAT. ArchLens sends one JSON `POST` and accepts any `2xx`; a URL field (`issueUrl`, `issue_url`, `html_url`, or `url`) is echoed back when present:

```json
{
  "id": "a5b92016-94ce-479e-8d29-c697d265aa5b",
  "source": "archlens",
  "category": "bug",
  "severity": "medium",
  "summary": "hotspots ignores the filter argument",
  "details": "Observed: 15 rows. Expected: only rows matching the glob.",
  "area": "hotspots",
  "tool": "hotspots",
  "fingerprint": "9d07abd8f6e3",
  "appVersion": "0.9.0",
  "runtime": ".NET 10.0.12 (win-x64)",
  "createdAtUtc": "2026-11-25T16:49:14.0000000+00:00"
}
```

`id` is generated once per report and reused by every retry, so an intake can store a resent report instead of duplicating it; `source` lets one service serve several tools; `appVersion` matches the field name of the other clients. The same contract is implemented by the shared intake at `https://feedback.bomdenet.com/api/feedback`, so pointing `ARCHLENS_FEEDBACK_URL` there is enough — no repository token on the user's machine.

The proxy should apply its own spam controls (per-IP rate limit, proof-of-work, or moderation) because it becomes an unauthenticated issue-creation path into the repository.

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
5. **Report friction:** when a tool returns a wrong or confusing payload, silently drops a parameter, or errors out, call `give_feedback` with the exact call and the observed versus expected result. This is the only channel that tells the maintainers an agent hit a real problem; without a token or proxy endpoint the tool answers `disabled` and nothing is sent.
