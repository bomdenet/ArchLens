# Архитектура ArchLens

## Главный принцип

**Roslyn и транспорт (MCP / CLI) изолированы от логики анализа.**  
Всё ядро анализа работает поверх неизменяемой канонической графовой модели `CodeGraph` (`ArchLens.Model`).

```text
┌────────────────────────────────────────────────────────────────────┐
│  Adapters (Thin Layer)                                             │
│   ├── ArchLens.Mcp (ModelContextProtocol SDK, stdio, stateful)     │
│   └── ArchLens.Cli (Console / CI / JSON output)                    │
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

## Зависимости между проектами

Слои жёстко контролируются самим ArchLens через `.archlens.yaml` (см. тест `RoslynSelfAnalysisTests`):

| Проект | Зависит от | Ответственность |
| :--- | :--- | :--- |
| `ArchLens.Model` | *(ничего)* | Канонический граф, DTO, контракты расширений |
| `ArchLens.Analysis` | `Model` | Алгоритмы (Tarjan SCC, Coupling/Instability, Impact BFS, Diff), встроенные правила, пресеты, `PluginLoader` (ALC), Mermaid/DOT/Structurizr/HTML-экспорт |
| `ArchLens.Rules.Yaml` | `Model`, `Analysis` | Парсинг `.archlens.yaml` и архитектурных пресетов в `IArchitectureRule` |
| `ArchLens.Query` | `Model`, `Analysis` | Проекция `CodeGraph` и метрик в in-memory SQLite для безопасных read-only SQL-запросов |
| `ArchLens.Extraction.Roslyn` | `Model` | Загрузка `.slnx/.sln/.csproj` через Roslyn, инкрементальные `Document`-обновления, `WorkspaceSessionManager`, `SqliteGraphCache` |
| `ArchLens.Extraction.Metadata` | `Model` | Сверхбыстрый IL + Portable PDB экстрактор на `System.Reflection.Metadata` (без MSBuild/CLR-загрузки) |
| `ArchLens.Mcp` | все слои | Тонкая обвязка MCP-тулов, JSON-проекция, DI-сборка |
| `ArchLens.Cli` | все слои | Тонкий CLI для людей, отладки и CI/CD |

---

## Точки расширения (Extension Points)

1. **`ICodeGraphExtractor` / `IIncrementalCodeGraphExtractor`** — реализован в двух бэкендах (`RoslynCodeGraphExtractor` и `MetadataCodeGraphExtractor`), позволяет добавить любой новый источник данных без изменения правил, метрик и MCP-тулов.
2. **`IPersistentGraphCache`** — персистентный кэш графа между перезапусками (встроенная реализация — `SqliteGraphCache` на SQLite + GZip).
3. **`IArchitectureRule`** — позволяет писать произвольные C#-правила поверх `CodeGraph` и `NodeMetrics`, регистрировать их через DI или класть скомпилированную `.dll` в `.archlens/plugins/` для автоматической загрузки через collectible `AssemblyLoadContext`.
4. **`IRuleSetProvider`** — позволяет добавить загрузку правил из других форматов (JSON, C# scripts, Roslyn `.editorconfig`, ArchUnit-совместимый формат).
5. **`ICustomMetricCalculator`** — позволяет добавлять собственные числовые метрики (например, Churn-weighted complexity из git или доменные индексы) через DI или `.archlens/plugins/*.dll`; значения попадают в `NodeMetrics.CustomMetrics`, `max_metric`, `hotspots` и SQL-таблицу `custom_metrics`.
6. **`IEntryPointDetector`** — позволяет добавлять проектно-специфичные детекторы точек входа (например, Quartz jobs, Hangfire tasks, кастомные атрибуты) через DI или `.archlens/plugins/*.dll` для анализа мёртвого кода (`DeadCodeAnalyzer`).
7. **`CodeNode.Metadata`** — расширяемый словарь строковых метаданных на узле графа, позволяющий экстракторам передавать дополнительные характеристики без ломки базового контракта.

---

## Почему MCP-сервер stateful

Открытие C#-солюшена через `MSBuildWorkspace` — дорогая операция (секунды на средних проектах).  
`WorkspaceSessionManager` держит последний `ArchitectureSnapshot` в памяти (потокобезопасно), отслеживает изменения на диске через `FileSystemWatcher` + mtime и одновременно запоминает первый снимок как `baseline` сессии:
- Повторные вызовы (`graph`, `hotspots`, `check`, `inspect`, `impact`) выполняются за миллисекунды.
- Когда агент вносит правки в код, `WorkspaceSessionManager` автоматически определяет изменённые файлы и инкрементально пересчитывает только затронутые проекты (и их зависимые), а `diff` сразу показывает дельту нарушений и циклов.
