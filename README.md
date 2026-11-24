# ArchLens

**ArchLens** — open-source, agent-friendly движок анализа архитектуры C#/.NET проектов, построенный поверх **Roslyn** и предоставляющий компактные, структурированные инструменты через **MCP (Model Context Protocol)** и **CLI**.

Цель проекта — дать AI-агентам «архитектурное зрение» (аналогичное NDepend, но бесплатное, расширяемое и спроектированное под ограничения контекстного окна LLM), а также возможность запускать проверки чистоты архитектуры в CI/CD и локально.

---

## Возможности (v0.5)

- **Два бэкенда экстракции поверх единого `ICodeGraphExtractor`**:
  - **Roslyn (`ArchLens.Extraction.Roslyn`)** — полный семантический анализ исходного кода (`.slnx`, `.sln`, `.csproj`), выбор целевого TFM (`--tfm`), **инкрементальная перекомпиляция только изменившихся `Document` в `MSBuildWorkspace`** без переоткрытия `.sln`.
  - **IL + Portable PDB (`ArchLens.Extraction.Metadata`)** — сверхбыстрое чтение скомпилированных `.dll` + `.pdb` через `System.Reflection.Metadata` (`--backend il` / `backend: "il"`), ускоряющее анализ крупных решений в 5–10×.
- **Трёхуровневое кэширование и автоинвалидация**: in-memory кэш с `FileSystemWatcher` + mtime, warm Roslyn `Document` updates и **персистентный SQLite-кэш (`SqliteGraphCache`)** между перезапусками MCP-сервера.
- **Динамические плагины (`.archlens/plugins/*.dll`)**: загрузка пользовательских `IArchitectureRule`, `ICustomMetricCalculator` и `IEntryPointDetector` в изолированных collectible `AssemblyLoadContext` без блокировки DLL-файлов (с явным opt-in `--allow-plugins` или `ARCHLENS_ALLOW_PLUGINS=1`).
- **SQL over Graph (`query` / `archlens query`)**: проекция графа в in-memory SQLite (`nodes`, `edges`, `metrics`, `projects`, `di_registrations`, `analyzer_diagnostics`, `custom_metrics`) с многоуровневой read-only защитой (`sqlite3_set_authorizer` + `PRAGMA query_only = ON` + валидатор SQL + таймаут/прерывание рекурсивных CTE).
- **Архитектурные пресеты (`archlens presets` / `archlens init --preset ...`)**: готовые наборы правил для `clean-architecture`, `vertical-slice`, `modular-monolith` и `ddd` (поддерживаются в `.archlens.yaml`, CLI и MCP).
- **Визуализация и отчёты (`archlens export` / `archlens report` / `export_diagram`)**: экспорт в **Mermaid**, **Graphviz DOT** и **Structurizr DSL** с подсветкой циклов, плюс автономный **HTML-отчёт** с Dependency Structure Matrix (DSM), таблицей hotspots и SVG-графиком трендов относительно baseline.
- **MCP Prompts & Resources**: готовые промпты (`audit-architecture`, `plan-layer-refactoring`, `review-pr-architecture`, `investigate-dead-code`) и ресурсы (`archlens://schema/sql`, `archlens://presets`, `archlens://rules/reference`).
- **Dead Code & Unreferenced Symbols (`dead_code` / `archlens dead-code`)**: reachability BFS с расширяемым интерфейсом `IEntryPointDetector` и встроенными детекторами (`Program.Main`, ASP.NET Controllers / Minimal APIs, MediatR / MassTransit handlers, xUnit / NUnit / MSTest, DI-регистрации, публичные API библиотек).
- **Анализ DI-графа (`di_analysis` / `archlens di`)**: парсинг `IServiceCollection` (`AddSingleton` / `AddScoped` / `AddTransient` / `AddHostedService`), поиск прямых и транзитивных *captive dependencies* и интерфейсов с единственной реализацией.
- **Агрегация Roslyn Analyzers (`analyzers` / `archlens analyzers`)**: подмешивание диагностик `Microsoft.CodeAnalysis.NetAnalyzers` / `Roslynator` с фильтрацией по категориям (`Design`, `Maintainability`, `Performance`).
- **Экспорт в SARIF 2.1.0** (`--format sarif --output report.sarif`) для GitHub Code Scanning / Azure DevOps / IDE.
- **Персистентный baseline на диске (`.archlens-baseline.json`)**: команды `archlens baseline` и `archlens diff --fail-on-new-errors` для сравнения веток и PR в CI/CD.
- **Поиск циклов** (Tarjan SCC) на уровне проектов, неймспейсов, типов и методов.
- **Архитектурные метрики:** $C_a$, $C_e$, Instability ($I$), Abstractness ($A$), Distance from Main Sequence ($D$), цикломатическая и **когнитивная сложность**, **LCOM4**, LOC, параметры конструктора, а также кастомные метрики из плагинов.
- **Декларативные правила архитектуры** в `.archlens.yaml` (`forbid_dependency`, `allow_only_dependencies`, `slice_isolation`, `no_cycles`, `max_metric`, `naming_convention`, `no_captive_dependencies`, `no_dead_code`, `preset`/`presets`).
- **Два тонких адаптера (`.NET Global Tool`):** `ArchLens.Mcp` (`archlens-mcp`) и `ArchLens.Cli` (`archlens`).

---

## Быстрый старт

### Требования
- [.NET 10 SDK](https://dotnet.microsoft.com/)

### Установка как .NET Global Tool
```bash
dotnet tool install -g ArchLens.Cli
dotnet tool install -g ArchLens.Mcp
```
Или локально из собранных пакетов:
```bash
dotnet pack src/ArchLens.Cli -c Release -o artifacts/nupkgs
dotnet pack src/ArchLens.Mcp -c Release -o artifacts/nupkgs
dotnet tool install -g --add-source artifacts/nupkgs ArchLens.Cli
dotnet tool install -g --add-source artifacts/nupkgs ArchLens.Mcp
```

### Сборка и тесты
```bash
dotnet build ArchLens.slnx
dotnet test ArchLens.slnx
```

### Запуск CLI (например, самопроверка ArchLens)
```bash
dotnet run --project src/ArchLens.Cli -- overview .
dotnet run --project src/ArchLens.Cli -- check .
dotnet run --project src/ArchLens.Cli -- hotspots . --by complexity --top 10
dotnet run --project src/ArchLens.Cli -- graph . --scope namespace
dotnet run --project src/ArchLens.Cli -- impact . CodeGraph
```

### Подключение MCP-сервера к агенту
Добавь конфигурацию в `mcp.json` твоего клиента (Claude Code, Cursor, Antigravity, VS Code Copilot и т.д.):

```json
{
  "mcpServers": {
    "archlens": {
      "command": "archlens-mcp"
    }
  }
}
```
> Или запуск напрямую из исходников: `dotnet run --project src/ArchLens.Mcp/ArchLens.Mcp.csproj -c Release`.

---

## Документация

- [docs/architecture.md](docs/architecture.md) — слои, каноническая модель `CodeGraph` и точки расширения.
- [docs/mcp-tools.md](docs/mcp-tools.md) — список MCP-инструментов, параметры и рекомендуемый workflow агента.
- [docs/rules-format.md](docs/rules-format.md) — справочник по формату `.archlens.yaml`.
- [todo.md](todo.md) — дорожная карта развития проекта.
