# Спецификация MCP-инструментов ArchLens

Инструменты спроектированы по принципу **coarse-grained + token-efficient**: всего 7 чётких тулов вместо десятков мелких, компактный JSON без `null`-полей, относительные пути `file:line`, встроенные лимиты (`maxNodes`, `topN`, `maxViolations`).

---

## Список инструментов

| Инструмент | Назначение | Ключевые параметры |
| :--- | :--- | :--- |
| `archlens_overview` | Карта решения за 1 вызов: проекты, ссылки, топ неймспейсов, счётчики циклов, ошибки загрузки MSBuild | `path`, `refresh?`, `includeTests?` |
| `archlens_dependency_graph` | Граф зависимостей и список всех циклов | `path`, `scope` (`project\|namespace\|type`), `filter?`, `maxNodes?`, `refresh?` |
| `archlens_hotspots` | Топ-N проблемных мест по выбранной метрике | `path`, `sortBy` (`complexity\|coupling\|afferent\|instability\|distance\|lines\|ctor`), `scope` (`type\|namespace`), `filter?`, `topN?`, `refresh?` |
| `archlens_check_rules` | Проверка правил из `.archlens.yaml` (автопоиск в корне или явный путь; fallback — проверка циклов) | `path`, `rulesPath?`, `maxViolations?`, `refresh?` |
| `archlens_inspect_symbol` | Карточка одного типа: метрики, входящие/исходящие зависимости, координаты | `path`, `symbol`, `refresh?` |
| `archlens_impact` | Blast radius: прямые и транзитивные зависимые, затронутые проекты | `path`, `symbol`, `maxDepth?`, `maxItems?`, `refresh?` |
| `archlens_diff` | Перечитывает проект с диска и сравнивает с baseline сессии (дельта циклов и нарушений) | `path`, `rulesPath?`, `resetBaseline?` |

---

## Рекомендуемый workflow AI-агента

1. **Ориентация:** вызвать `archlens_overview` — убедиться, что `diagnostics` не содержат фатальных ошибок загрузки MSBuild, увидеть проекты и число циклов.
2. **Диагностика:** вызвать `archlens_check_rules` и `archlens_hotspots(sortBy: "complexity")` / `archlens_hotspots(sortBy: "coupling")`.
3. **Планирование рефакторинга:** перед изменением ключевого класса/интерфейса вызвать `archlens_inspect_symbol` и `archlens_impact`, чтобы оценить риск и не сломать соседние слои.
4. **Верификация (Feedback Loop):** после редактирования файлов вызвать `archlens_diff` — убедиться, что `cycleCountDelta <= 0`, `violationCountDelta <= 0` и `newViolations` пуст.
