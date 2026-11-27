# Declarative Rules Format (`.archlens.yaml`)

Place `.archlens.yaml` (or `.archlens.yml`) at the repository root alongside `.slnx` / `.sln`; ArchLens discovers it automatically when neither `--rules` nor `--preset` is passed. Passing `--preset` runs that preset standalone (combine `--preset` with `--rules` to merge both).

---

## Architecture Presets & Custom Layer Mapping

Enable one or more built-in presets (`clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`) and optionally customize layer namespaces via `layers:` when your repository does not follow the default `*.Domain.*` / `*.Application.*` / `*.Infrastructure.*` / `*.Presentation.*` naming conventions:

```yaml
version: 1
preset: clean-architecture
root_namespace: "MyApp"

layers:
  domain: ["MyApp.Core.*", "MyApp.Models.*"]
  application: ["MyApp.UseCases.*", "MyApp.Services.*"]
  infrastructure: ["MyApp.Data.*", "MyApp.Adapters.*"]
  presentation: ["MyApp.Web.*", "MyApp.Host.*"]

exclude:
  - "*.Generated.*"

acknowledge:
  - symbol: "MyApp.Legacy.ScriptRunner"
    reason: "Invoked dynamically via reflection from external CLI scripts"

rules:
  - id: CUSTOM-001
    name: Keep classes cohesive
    severity: warning
    type: max_metric
    metric: lcom4
    threshold: 2
```

Generate a starter template with `archlens init --preset clean-architecture --root-namespace MyApp`.

---

## Supported Rule Types (`type`)

### 1. `forbid_dependency`
Forbids types matching `from` (and/or `from_project` / `from_projects`) from referencing types matching any pattern in `to` (and/or `to_project` / `to_projects`). Internal edges where both source and target match the same forbidden pattern are excluded.

```yaml
- id: ARCH-001
  name: Domain must not depend on Infrastructure
  severity: error
  type: forbid_dependency
  from: "MyApp.Domain.*"
  from_project: "MyApp.Domain"        # optional single project glob (or from_projects: [...])
  exclude_from:
    - "MyApp.Domain.Bootstrap.*"
  exclude_from_projects:              # optional (or exclude_from_project: "...")
    - "MyApp.Tests.*"
  to:
    - "MyApp.Infrastructure.*"
    - "MyApp.Web.*"
  to_projects:                        # optional (or to_project: "...")
    - "MyApp.Infrastructure"
```

### 2. `allow_only_dependencies`
Allowlist of dependencies: types matching `from` (and/or `from_project` / `from_projects`) may only depend on `from` itself and the patterns listed in `allow`.

```yaml
- id: ARCH-002
  name: Application may only depend on Domain
  severity: error
  type: allow_only_dependencies
  from: "MyApp.Application.*"
  from_project: "MyApp.Application"   # optional single project glob (or from_projects: [...])
  allow:
    - "MyApp.Domain.*"
```

### 3. `no_cycles`
Forbids dependency cycles at `project`, `namespace`, `type`, or `method` scope. For `namespace` scope, EF Core migration cycles (`include_migrations: false`) and pure parent-child namespace chains like `MyApp.Ai <-> MyApp.Ai.Abstractions` (`include_parent_child: false`) are excluded by default and reported under `suppressedCycles`. Violation messages include the directed cycle path and sample concrete type edges with locations.

```yaml
- id: ARCH-003
  name: No namespace cycles
  severity: error
  type: no_cycles
  scope: namespace
  target: "MyApp.*"            # optional
  cross_project_only: false    # optional: true checks only cross-project namespace cycles
  include_migrations: false    # optional: true includes EF Core Migrations cycles
  include_parent_child: false  # optional: true flags A <-> A.Sub parent-child cycles
```

### 4. `max_metric`
Enforces numeric thresholds on types (or methods with `scope: method`). By default, EF Core migrations, `ModelSnapshot`, `DbContext` (for `lcom4`), and `JsonSerializerContext` are excluded from noise metrics (disable via `include_ef_and_generated: true`; legacy alias `include_infrastructure: true` is still accepted).

Supported `metric` values:
- `max_method_cyclomatic_complexity` (alias `cyclomatic_complexity`) — highest cyclomatic complexity of any single method in the type (**note:** in `hotspots` JSON and SQL `nodes`/`metrics`, `cyclomaticComplexity` / `cyclomatic_complexity` is the type *total*; use `total_cyclomatic_complexity` below to threshold type total)
- `total_cyclomatic_complexity` — sum of cyclomatic complexity across all methods in the type (matches `hotspots` `cyclomaticComplexity` and SQL `nodes.cyclomatic_complexity`)
- `max_method_cognitive_complexity` (alias `cognitive_complexity`) — highest cognitive complexity of any single method in the type
- `total_cognitive_complexity` — sum of cognitive complexity across all methods in the type (matches `hotspots` `cognitiveComplexity` and SQL `nodes.cognitive_complexity`)
- `lcom4` — Lack of Cohesion of Methods 4 (number of disconnected method-field components; `>= 2` indicates a class that can be split into independent responsibilities. Note: a very large class whose methods all touch the same shared fields still has `LCOM4 = 1`; use `god_class` to catch cohesive god classes)
- `efferent_coupling` (or `ce`) — outgoing dependencies on solution types
- `afferent_coupling` (or `ca`) — incoming dependencies from solution types
- `instability` — Robert C. Martin instability index ($0.0 \dots 1.0$)
- `line_count` (or `loc`) — source line count
- `constructor_params` — maximum constructor parameter count (excludes pure data/DTO records and classes without methods by default; pass `include_ef_and_generated: true` to include all types)
- *Any custom metric* (`MetricName`) registered via an `ICustomMetricCalculator` plugin in `.archlens/plugins/*.dll`

> **Note on the IL backend:** `--backend il` is intended for fast `overview` and dependency maps; use `--backend roslyn` for CI gates on `max_metric` (especially `cognitive_complexity`).

```yaml
- id: ARCH-004
  name: Limit constructor injection bloat
  severity: warning
  type: max_metric
  metric: constructor_params
  threshold: 6
  target: "MyApp.*"
  include_ef_and_generated: false
```

### 5. `god_class`
Composite detector for large, complex, heavily coupled classes — including cohesive god classes where `LCOM4 == 1` because methods share common state. Flags a type when at least `min_conditions` of the three thresholds (`max_lines`, `max_complexity`, `max_coupling`) are exceeded.

```yaml
- id: ARCH-009
  name: No god classes
  severity: warning
  type: god_class
  max_lines: 500        # default: 500
  max_complexity: 50    # default: 50 (total cyclomatic complexity)
  max_coupling: 20      # default: 20 (efferent coupling Ce)
  min_conditions: 3     # default: 3 (1..3)
  target: "MyApp.*"     # optional
```

### 6. `naming_convention`
Checks whether a type's short name matches a glob pattern.

```yaml
- id: ARCH-005
  name: Interfaces must start with I
  severity: warning
  type: naming_convention
  target: "MyApp.*"
  type_kind: Interface
  pattern: "I*"
```

### 7. `no_captive_dependencies`
Forbids DI captive dependencies (`Singleton -> Scoped`, including transitive chains via `Transient`).

```yaml
- id: ARCH-006
  name: No captive DI dependencies
  severity: error
  type: no_captive_dependencies
```

### 8. `no_dead_code`
Flags unreachable (potentially dead) types or methods relative to built-in and plugin entry-point detectors (`IEntryPointDetector`). Symbols listed under top-level `acknowledge:` or rule-level `acknowledge:` are excluded from violations and reported separately.

```yaml
- id: ARCH-007
  name: No dead internal types
  severity: warning
  type: no_dead_code
  scope: type
  profile: auto
  target: "MyApp.*"
  treat_public_api_as_entry_point: true
  acknowledge:
    - symbol: "MyApp.Plugins.*"
      reason: "Loaded via reflection at runtime"
```

### 9. `slice_isolation`
Isolates vertical feature slices or business modules (e.g., `*.Modules.<Module>.*` or `*.Features.<Slice>.*`), allowing cross-slice references only to explicitly permitted contract sub-namespaces.

```yaml
- id: ARCH-008
  name: Isolate business modules
  severity: error
  type: slice_isolation
  segment: Modules
  allow:
    - Contracts
    - Public
    - IntegrationEvents
    - Abstractions
```

---

## Glob Patterns & Top-Level `exclude:`

- `*` — matches any sequence of characters (including namespace dots).
- `?` — matches any single character.
- Bracket expressions (`[` / `]`) are not supported and rejected as invalid globs.
- Matching is case-insensitive.
- Top-level `exclude:` patterns apply across `forbid_dependency`, `max_metric`, `god_class`, `naming_convention`, `slice_isolation`, and `no_dead_code`.

## CLI Exit Codes

| Exit Code | Meaning |
| --- | --- |
| `0` | Success / clean check (no failing rule violations) |
| `1` | Analysis findings triggered a gate (`check` errors, `--fail-on-warning`, `diff --fail-on-new-errors`, `di --fail-on-captive`, `analyzers` errors, or missing symbol in `inspect`/`impact`) |
| `2` | Usage, configuration, or input error (unknown or repeated scalar option, invalid option value, invalid YAML/glob, vacuous `root_namespace`, missing/empty explicit rules file, missing baseline file, existing `.archlens.yaml` on `init` without `--force`, ambiguous symbol, 0 analyzable projects, or degraded/partially-loaded Roslyn/IL workspace or baseline in `check`/`diff`/`baseline` without `--include-degraded`) |
