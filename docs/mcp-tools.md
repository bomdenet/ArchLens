# Спецификация MCP-инструментов ArchLens

Инструменты спроектированы по принципу **coarse-grained + token-efficient**: компактный набор чётких тулов вместо десятков мелких, компактный JSON без `null`-полей, относительные пути `file:line`, встроенные лимиты (`maxNodes`, `topN`, `maxViolations`).

---

## Список инструментов

| Инструмент | Назначение | Ключевые параметры |
| :--- | :--- | :--- |
| `archlens_overview` | Карта решения за 1 вызов: проекты, ссылки, топ неймспейсов, счётчики циклов, ошибки загрузки MSBuild | `path`, `refresh?`, `includeTests?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `archlens_dependency_graph` | Граф зависимостей и список всех циклов (включая method-level call graph) | `path`, `scope` (`project\|namespace\|type\|method`), `filter?`, `maxNodes?`, `refresh?`, `targetFramework?`, `backend?` |
| `archlens_hotspots` | Топ-N проблемных мест по выбранной метрике (включая Cognitive Complexity и LCOM4) | `path`, `sortBy` (`complexity\|cognitive\|lcom4\|coupling\|afferent\|instability\|distance\|lines\|ctor`), `scope` (`type\|namespace\|method`), `filter?`, `topN?`, `refresh?`, `targetFramework?`, `backend?` |
| `archlens_check_rules` | Проверка правил из `.archlens.yaml`, встроенных пресетов и плагинов `.archlens/plugins/*.dll` (опционально с диагностиками Roslyn Analyzers) | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `maxViolations?`, `refresh?`, `includeTests?`, `includeMethods?`, `includeAnalyzers?`, `analyzerCategories?`, `targetFramework?`, `backend?` |
| `archlens_query` | Выполнение read-only SQL-запроса (SELECT / WITH) поверх in-memory SQLite-проекции графа (`nodes`, `edges`, `metrics`, `projects`, `di_registrations`, `analyzer_diagnostics`, `custom_metrics`) | `path`, `sql`, `maxRows?`, `includeMethods?`, `includeTests?`, `includeAnalyzers?`, `refresh?`, `targetFramework?`, `backend?` |
| `archlens_export_diagram` | Экспорт графа зависимостей в Mermaid, Graphviz DOT или Structurizr DSL с подсветкой циклов | `path`, `format` (`mermaid\|dot\|structurizr`), `scope`, `filter?`, `maxNodes?`, `refresh?`, `targetFramework?`, `backend?` |
| `archlens_dead_code` | Поиск мёртвого кода и недостижимых островов через reachability BFS от встроенных и плагинных `IEntryPointDetector` | `path`, `scope` (`type\|method`), `treatPublicApiAsEntryPoint?`, `filter?`, `maxItems?`, `includeTests?`, `refresh?`, `targetFramework?`, `backend?` |
| `archlens_di_analysis` | Анализ `IServiceCollection`: captive dependencies (`Singleton -> Scoped`, в т.ч. через `Transient`) и интерфейсы с 1 реализацией | `path`, `includeTests?`, `refresh?`, `targetFramework?` |
| `archlens_analyzers` | Агрегация диагностик Roslyn Analyzers (`NetAnalyzers` / `Roslynator`) с фильтрацией по категориям | `path`, `categories?`, `maxItems?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `archlens_inspect_symbol` | Карточка типа или метода: метрики (CC, Cognitive, LCOM4, кастомные метрики плагинов), входящие/исходящие зависимости, координаты | `path`, `symbol`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `archlens_impact` | Blast radius типа или метода: прямые и транзитивные зависимые, затронутые проекты | `path`, `symbol`, `maxDepth?`, `maxItems?`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `archlens_diff` | Инкрементально перечитывает изменённые проекты и сравнивает с baseline сессии или `.archlens-baseline.json` на диске | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `resetBaseline?`, `baselineFile?`, `saveBaseline?`, `includeTests?`, `includeMethods?`, `targetFramework?`, `backend?` |

---

## MCP Prompts

Готовые стандартизированные промпты, доступные агенту через `prompts/list` и `prompts/get`:

| Prompt | Назначение | Аргументы |
| :--- | :--- | :--- |
| `audit-architecture` | Полный пошаговый архитектурный аудит (overview, rules, analyzers, hotspots, DI, dead code, SQL) и приоритизированный план исправлений | `path`, `style?` |
| `plan-layer-refactoring` | Безопасный пошаговый план разрыва циклов или рефакторинга слоя с baseline, blast radius и критериями приёмки `archlens_diff` | `path`, `target` |
| `review-pr-architecture` | Архитектурное ревью текущей ветки/PR относительно baseline с вердиктом APPROVE / REQUEST CHANGES | `path`, `baselineFile?` |
| `investigate-dead-code` | Безопасная триаж-проверка кандидатов мёртвого кода и недостижимых островов перед удалением | `path`, `filter?` |

---

## MCP Resources

| URI | Назначение |
| :--- | :--- |
| `archlens://schema/sql` | Полная схема таблиц и примеры запросов для `archlens_query` |
| `archlens://presets` | Каталог встроенных архитектурных пресетов (`clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`) и шаблоны `.archlens.yaml` |
| `archlens://rules/reference` | Справочник типов правил `.archlens.yaml`, поддерживаемых метрик и конвенций плагинов `.archlens/plugins/` |

---

## Рекомендуемый workflow AI-агента

1. **Ориентация:** вызвать `archlens_overview` — убедиться, что `diagnostics` не содержат фатальных ошибок загрузки MSBuild, увидеть проекты и число циклов.
2. **Диагностика:** вызвать `archlens_check_rules` и `archlens_hotspots(sortBy: "complexity")` / `archlens_hotspots(sortBy: "coupling")`.
3. **Планирование рефакторинга:** перед изменением ключевого класса/интерфейса вызвать `archlens_inspect_symbol` и `archlens_impact`, чтобы оценить риск и не сломать соседние слои.
4. **Верификация (Feedback Loop):** после редактирования файлов вызвать `archlens_diff` — убедиться, что `cycleCountDelta <= 0`, `violationCountDelta <= 0` и `newViolations` пуст.
