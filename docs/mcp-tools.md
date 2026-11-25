# Спецификация MCP-инструментов ArchLens

Инструменты спроектированы по принципу **coarse-grained + token-efficient**: компактный набор чётких тулов без дублирующей приставки `archlens_` (клиент/харнесс сам добавляет неймспейс сервера), компактный JSON без `null`-полей, относительные пути `file:line`, встроенные лимиты (`maxNodes`, `topN`, `maxViolations`).

> **Безопасность плагинов:** выполнение пользовательских плагинов из `.archlens/plugins/*.dll` отключено по умолчанию и включается явным флагом при запуске MCP-сервера (`archlens-mcp --allow-plugins` или переменная окружения `ARCHLENS_ALLOW_PLUGINS=1`).

---

## Список инструментов

| Инструмент | Назначение | Ключевые параметры |
| :--- | :--- | :--- |
| `overview` | Карта решения за 1 вызов: проекты, ссылки, топ неймспейсов, счётчики циклов, ошибки загрузки MSBuild | `path`, `refresh?`, `includeTests?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `dependency_graph` | Граф зависимостей и список всех циклов (включая method-level call graph) | `path`, `scope` (`project\|namespace\|type\|method`), `filter?`, `maxNodes?`, `refresh?`, `targetFramework?`, `backend?` |
| `hotspots` | Топ-N проблемных мест по выбранной метрике (включая Cognitive Complexity, LCOM4 и кастомные метрики плагинов) | `path`, `sortBy` (`complexity\|cognitive\|lcom4\|coupling\|afferent\|instability\|distance\|lines\|ctor\|<custom>`), `scope` (`type\|namespace\|method`), `filter?`, `topN?`, `refresh?`, `targetFramework?`, `backend?` |
| `check_rules` | Проверка правил из `.archlens.yaml`, встроенных пресетов и включённых плагинов `.archlens/plugins/*.dll` (опционально с диагностиками Roslyn Analyzers); возвращает `snapshotId`/`capturedAt` и исключает degraded-проекты по умолчанию | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `maxViolations?`, `includeDegraded?`, `refresh?`, `includeTests?`, `includeMethods?`, `includeAnalyzers?`, `analyzerCategories?`, `targetFramework?`, `backend?` |
| `query` | Выполнение read-only SQL-запроса (`SELECT` / `WITH`) поверх in-memory SQLite-проекции графа (`nodes`, `edges`, `metrics`, `projects`, `di_registrations`, `analyzer_diagnostics`, `custom_metrics`, `schema_info`) с `sqlite3_set_authorizer`, подсказками колонок и `includeSchema: true` | `path?`, `sql?`, `includeSchema?`, `maxRows?`, `includeMethods?`, `includeTests?`, `includeAnalyzers?`, `refresh?`, `targetFramework?`, `backend?` |
| `export_diagram` | Экспорт графа зависимостей в Mermaid, Graphviz DOT или Structurizr DSL с подсветкой циклов | `path`, `format` (`mermaid\|dot\|structurizr`), `scope`, `filter?`, `maxNodes?`, `refresh?`, `targetFramework?`, `backend?` |
| `dead_code` | Поиск мёртвого кода и недостижимых островов через reachability BFS с профилями `auto\|app\|lib\|strict`, поддержкой Blazor/XAML/ASP.NET/DI и исключением test-support проектов | `path`, `scope` (`type\|method`), `profile?`, `treatPublicApiAsEntryPoint?`, `filter?`, `maxItems?`, `includeTests?`, `refresh?`, `targetFramework?`, `backend?` |
| `di_analysis` | Анализ `IServiceCollection`: компактная сводка по lifetime/проектам, фильтры, captive dependencies и интерфейсы с 1 реализацией (`includeRegistrations` по умолчанию `false`) | `path`, `includeRegistrations?`, `lifetime?`, `project?`, `serviceType?`, `onlyCaptive?`, `onlyUnregistered?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `analyzers` | Агрегация диагностик Roslyn Analyzers (`NetAnalyzers` / `Roslynator`) с фильтрацией по категориям, `minSeverity`, `elevateToWarning` и пропуском generated/Migrations | `path`, `categories?`, `minSeverity?`, `elevateToWarning?`, `filter?`, `maxItems?`, `includeDegraded?`, `includeTests?`, `refresh?`, `targetFramework?` |
| `inspect_symbol` | Карточка типа или метода: метрики (CC, Cognitive, LCOM4, кастомные метрики плагинов), входящие/исходящие зависимости, координаты | `path`, `symbol`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `impact` | Blast radius типа или метода: прямые и транзитивные потребители (без DI-регистраций и тестов по умолчанию) и затронутые проекты | `path`, `symbol`, `maxDepth?`, `maxItems?`, `excludeRegistrations?`, `includeTests?`, `strict?`, `refresh?`, `includeMethods?`, `targetFramework?`, `backend?` |
| `diff` | Инкрементально перечитывает изменённые проекты и сравнивает с baseline сессии или `.archlens-baseline.json` на диске | `path`, `rulesPath?`, `preset?`, `rootNamespace?`, `resetBaseline?`, `baselineFile?`, `saveBaseline?`, `includeTests?`, `includeMethods?`, `targetFramework?`, `backend?` |

---

## MCP Prompts

Готовые стандартизированные промпты, доступные агенту через `prompts/list` и `prompts/get`:

| Prompt | Назначение | Аргументы |
| :--- | :--- | :--- |
| `audit-architecture` | Полный пошаговый архитектурный аудит (overview, rules, analyzers, hotspots, DI, dead code, SQL) и приоритизированный план исправлений | `path`, `style?` |
| `plan-layer-refactoring` | Безопасный пошаговый план разрыва циклов или рефакторинга слоя с baseline, blast radius и критериями приёмки `diff` | `path`, `target` |
| `review-pr-architecture` | Архитектурное ревью текущей ветки/PR относительно baseline с вердиктом APPROVE / REQUEST CHANGES | `path`, `baselineFile?` |
| `investigate-dead-code` | Безопасная триаж-проверка кандидатов мёртвого кода и недостижимых островов перед удалением | `path`, `filter?` |

---

## MCP Resources

| URI | Назначение |
| :--- | :--- |
| `archlens://schema/sql` | Полная схема таблиц и примеры запросов для `query` |
| `archlens://presets` | Каталог встроенных архитектурных пресетов (`clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`) и шаблоны `.archlens.yaml` |
| `archlens://rules/reference` | Справочник типов правил `.archlens.yaml`, поддерживаемых метрик и конвенций плагинов `.archlens/plugins/` |

---

## Рекомендуемый workflow AI-агента

1. **Ориентация:** вызвать `overview` — убедиться, что `diagnostics` не содержат фатальных ошибок загрузки MSBuild, увидеть проекты и число циклов.
2. **Диагностика:** вызвать `check_rules` и `hotspots(sortBy: "cognitive")` / `hotspots(sortBy: "lcom4")`.
3. **Планирование рефакторинга:** перед изменением ключевого класса/интерфейса вызвать `inspect_symbol` и `impact`, чтобы оценить риск и не сломать соседние слои.
4. **Верификация (Feedback Loop):** после редактирования файлов вызвать `diff` — убедиться, что `cycleCountDelta <= 0`, `violationCountDelta <= 0` и `newViolations` пуст.
