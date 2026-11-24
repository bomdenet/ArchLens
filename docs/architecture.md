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
│  Analysis & Rules                                                  │
│   ├── ArchLens.Analysis   (Tarjan cycles, Metrics, Impact, Diff)   │
│   └── ArchLens.Rules.Yaml (.archlens.yaml -> IArchitectureRule[])  │
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
│   ├── ArchLens.Extraction.Roslyn (MSBuildWorkspace + SemanticModel)│
│   └── [Future] ArchLens.Extraction.Cecil (Fast IL/Metadata reader) │
└────────────────────────────────────────────────────────────────────┘
```

---

## Зависимости между проектами

Слои жёстко контролируются самим ArchLens через `.archlens.yaml` (см. тест `RoslynSelfAnalysisTests`):

| Проект | Зависит от | Ответственность |
| :--- | :--- | :--- |
| `ArchLens.Model` | *(ничего)* | Канонический граф, DTO, контракты расширений |
| `ArchLens.Analysis` | `Model` | Алгоритмы (Tarjan SCC, Coupling/Instability, Impact BFS, Diff), встроенные правила |
| `ArchLens.Rules.Yaml` | `Model`, `Analysis` | Парсинг `.archlens.yaml` в `IArchitectureRule` |
| `ArchLens.Extraction.Roslyn` | `Model` | Загрузка `.slnx/.sln/.csproj` через Roslyn, кэш `WorkspaceSessionManager` |
| `ArchLens.Mcp` | все слои | Тонкая обвязка MCP-тулов, JSON-проекция, DI-сборка |
| `ArchLens.Cli` | все слои | Тонкий CLI для людей, отладки и CI/CD |

---

## Точки расширения (Extension Points)

1. **`ICodeGraphExtractor`** — позволяет добавить новый источник данных (например, быстрый IL-экстрактор на `Mono.Cecil` или экстрактор для другого языка), не меняя ни одной строчки в правилах, метриках и MCP-тулах.
2. **`IArchitectureRule`** — позволяет писать произвольные C#-правила поверх `CodeGraph` и `NodeMetrics` и регистрировать их через DI (или в будущем загружать из плагинов).
3. **`IRuleSetProvider`** — позволяет добавить загрузку правил из других форматов (JSON, C# scripts, Roslyn `.editorconfig`, ArchUnit-совместимый формат).
4. **`ICustomMetricCalculator`** — позволяет добавлять собственные числовые метрики (например, LCOM4, Cognitive Complexity, Churn-weighted complexity из git).
5. **`CodeNode.Metadata`** — расширяемый словарь строковых метаданных на узле графа, позволяющий экстракторам передавать дополнительные характеристики без ломки базового контракта.

---

## Почему MCP-сервер stateful

Открытие C#-солюшена через `MSBuildWorkspace` — дорогая операция (секунды на средних проектах).  
`WorkspaceSessionManager` держит последний `ArchitectureSnapshot` в памяти (потокобезопасно) и одновременно запоминает первый снимок как `baseline` сессии:
- Повторные вызовы (`graph`, `hotspots`, `check`, `inspect`, `impact`) выполняются за миллисекунды.
- Когда агент вносит правки в код, он вызывает `archlens_diff` (или любой тул с `refresh: true`), чтобы перечитать код с диска и проверить дельту нарушений и циклов.
