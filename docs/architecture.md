# ArchLens Architecture

## Core Principle

**Roslyn and transport adapters (MCP / CLI) are isolated from analysis logic.**
The entire analysis engine operates on an immutable canonical graph model: `CodeGraph` (`ArchLens.Model`).

```text
┌────────────────────────────────────────────────────────────────────┐
│  Adapters (Thin Layer)                                             │
│   ├── ArchLens.Mcp (ModelContextProtocol SDK, stdio, stateful)     │
│   └── ArchLens.Cli (Console / CI / JSON / SARIF output)            │
└───────────────────────────────┬────────────────────────────────────┘
                                │
                                ▼
┌────────────────────────────────────────────────────────────────────┐
│  Analysis, Rules & Query                                           │
│   ├── ArchLens.Analysis   (Cycles, Metrics, Plugins, Presets, Viz) │
│   ├── ArchLens.Rules.Yaml (.archlens.yaml + presets -> Rules[])    │
│   └── ArchLens.Query      (Read-only in-memory SQLite SQL engine)  │
└───────────────────────────────┬────────────────────────────────────┘
                                │
                                ▼
┌────────────────────────────────────────────────────────────────────┐
│  ArchLens.Model (Zero external dependencies)                       │
│   CodeGraph, CodeNode, DependencyEdge, NodeMetrics,                │
│   ICodeGraphExtractor, IArchitectureRule, ICustomMetricCalculator  │
└───────────────────────────────▲────────────────────────────────────┘
                                │
┌───────────────────────────────┴────────────────────────────────────┐
│  Extractors                                                        │
│   ├── ArchLens.Extraction.Roslyn   (MSBuildWorkspace + incremental)│
│   └── ArchLens.Extraction.Metadata (Fast IL + Portable PDB reader) │
└────────────────────────────────────────────────────────────────────┘
```

---

## Project Dependencies

Layering boundaries are enforced by ArchLens itself via `.archlens.yaml` (see `RoslynSelfAnalysisTests`):

| Project | Depends On | Responsibility |
| :--- | :--- | :--- |
| `ArchLens.Model` | *(none)* | Canonical graph, DTOs, extension contracts |
| `ArchLens.Analysis` | `Model` | Algorithms (Tarjan SCC, Coupling/Instability, LCOM4, Impact BFS, Diff), built-in rules, presets, `PluginLoader` (ALC), Mermaid/DOT/Structurizr/HTML/SARIF export |
| `ArchLens.Rules.Yaml` | `Model`, `Analysis` | Parsing `.archlens.yaml`, presets, custom layers, and dead-code acknowledgements into `IArchitectureRule` |
| `ArchLens.Query` | `Model`, `Analysis` | Projecting `CodeGraph` and metrics into in-memory SQLite for safe read-only SQL queries |
| `ArchLens.Extraction.Roslyn` | `Model` | Loading `.slnx/.sln/.csproj` via Roslyn, incremental `Document` updates, `WorkspaceSessionManager`, `SqliteGraphCache` |
| `ArchLens.Extraction.Metadata` | `Model` | Fast IL + Portable PDB extractor built on `System.Reflection.Metadata` (no MSBuild/CLR loading) |
| `ArchLens.Mcp` | all layers | Thin MCP tool wiring, compact JSON formatting, DI setup |
| `ArchLens.Cli` | all layers | Thin CLI for developers, debugging, and CI/CD |

---

## Extension Points

1. **`ICodeGraphExtractor` / `IIncrementalCodeGraphExtractor`** — implemented by both `RoslynCodeGraphExtractor` and `MetadataCodeGraphExtractor`, allowing new data sources without changing rules, metrics, or MCP tools.
2. **`IPersistentGraphCache`** — persistent graph cache across process restarts (built-in implementation: `SqliteGraphCache` using SQLite + GZip).
3. **`IArchitectureRule`** — write custom C# rules over `CodeGraph` and `NodeMetrics`, register via DI, or drop compiled `.dll` plugins into `.archlens/plugins/` for isolated loading via collectible `AssemblyLoadContext`.
4. **`IRuleSetProvider`** — add rule loaders for additional formats (JSON, C# scripts, `.editorconfig`, ArchUnit-style rules).
5. **`ICustomMetricCalculator`** — register custom numeric metrics (e.g., git churn-weighted complexity or domain indices) via DI or `.archlens/plugins/*.dll`; values appear in `NodeMetrics.CustomMetrics`, `max_metric`, `hotspots`, and the SQL `custom_metrics` table.
6. **`IEntryPointDetector`** — add project-specific entry-point detectors (e.g., Quartz jobs, Hangfire tasks, custom attributes) via DI or `.archlens/plugins/*.dll` for `DeadCodeAnalyzer`.
7. **`CodeNode.Metadata`** — extensible string dictionary on graph nodes enabling extractors to attach extra metadata without breaking the core model contract.

---

## Why the MCP Server Is Stateful

Opening a C# solution through `MSBuildWorkspace` takes seconds on medium-to-large repositories.
`WorkspaceSessionManager` keeps the latest `ArchitectureSnapshot` in memory (thread-safe), watches disk changes via `FileSystemWatcher` + mtime, and remembers the initial snapshot as the session baseline:

- Subsequent queries (`dependency_graph`, `hotspots`, `check_rules`, `inspect_symbol`, `impact`) execute in milliseconds.
- When an agent edits files, `WorkspaceSessionManager` detects modified files and incrementally recompiles only dirty projects (and their dependents), while `diff` immediately reports cycle and violation deltas.
