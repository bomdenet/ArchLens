# Оценка полезности ArchLens 0.6.0

| | |
|---|---|
| **Дата** | 2026-11-25 |
| **Версия** | ArchLens 0.6.0 (`Directory.Build.props`), Roslyn 5.12.0 |
| **Окружение** | Windows 10.0.26300, .NET SDK 10.0, CLI Release build |
| **Что оценивалось** | практическая полезность как MCP-инструмента для AI-агента и как CI-гейта |
| **Материал** | 6 реальных репозиториев из `C:\Users\bomde\source\repos` + синтетический полигон с известными дефектами |
| **Изменения в репозиториях** | нет, только чтение; полигон вынесен в скретч-каталог |

---

## 1. Резюме

ArchLens 0.6.0 делает то, что заявляет, и делает это точнее, чем можно ожидать от проекта версии 0.6. На контрольном примере с пятью заранее известными дефектами найдены все пять (плюс один настоящий мусорный тип из шаблона), ложных срабатываний — ноль. Анализ мёртвого кода показал низкий уровень шума даже на Blazor-решении с 40 Razor-компонентами, где наивные анализаторы обычно выдают десятки ложных кандидатов.

Основная ценность — не в отдельных метриках (их можно получить и в IDE), а в трёх вещах, которые плохо собираются из готовых бесплатных инструментов: **`impact` (blast radius с исключением DI-регистраций), SQL поверх графа и machine-readable вывод с SARIF/exit code**. Именно они превращают граф в рабочий инструмент агента, а не в красивую диаграмму.

Основной риск — калибровка сигнала и шума в правилах циклов: `no_cycles` на уровне namespace считает нарушением нормальные для C# отношения «родительский ↔ дочерний namespace». С `severity: error` в CI это даёт красную сборку на ровном месте.

| Роль | Оценка | Комментарий |
|---|---|---|
| MCP-инструмент для агента | **8 / 10** | компактный JSON, быстрый warm-режим, `impact` и SQL не имеют бесплатных аналогов |
| CI-гейт (PR, SARIF) | **7 / 10** → 8 / 10 после P0 | механика работает (exit code, diff, SARIF), мешает шум циклов |
| Замена NDepend | **6,5 / 10** | метрики и тренды есть, но нет исторических дашбордов и глубина анализа ниже |
| Визуализация / отчёты | не оценивалось | проверен только Mermaid; DOT, Structurizr, HTML-отчёт — вне scope |

---

## 2. Методика

### 2.1 Реальные решения

| Решение | Тип | Проектов | Типов | Рёбер | Циклов (ns) | Загрузка |
|---|---|---|---|---|---|---|
| `ArchLens.slnx` | библиотека + 2 адаптера | 8 | 140 | 1613 | 0 | 8/8, без деградаций |
| `DreamAgents.slnx` | WPF-приложение, слоистая архитектура | 7 | 665 | 6056 | 4 | 7/7 |
| `TicTacToe.slnx` | Blazor + ASP.NET + MCP-сервер | 7 | 345 | 3543 | 5 | 7/7, 40 Razor-компонентов |
| `MoneyScripts.slnx` | приложение | 4 | 127 | 1026 | 1 | 4/4 |
| `AiLearningLab.sln` (классический `.sln`) | библиотеки + десктоп | 6 | 49 | 432 | 0 | 6/6 |
| `BadArch.slnx` | синтетический полигон | 3 | 14 | — | 1 (намеренный) | 3/3 |

### 2.2 Синтетический полигон (ground truth)

Собрано решение `BadArch` (Domain, Infrastructure, App/Exe) с пятью намеренными дефектами и одним `.archlens.yaml` из пяти правил:

| # | Заложенный дефект | Правило | Ожидание |
|---|---|---|---|
| 1 | `Domain` ссылается на `Infrastructure` | `forbid_dependency` | 1 ошибка |
| 2 | Цикл `Domain.Alpha ↔ Domain.Beta` | `no_cycles` (namespace) | 1 ошибка |
| 3 | Singleton захватывает Scoped | `no_captive_dependencies` | 1 ошибка |
| 4 | `OrphanReportBuilder` не используется | `no_dead_code` | 1 warning |
| 5 | Метод с цикломатической сложностью 19 | `max_metric` (15) | 1 warning |

Полигон собирался и билдился штатно (`dotnet build`), чтобы MSBuildWorkspace видел реальные ссылки. Дополнительно проверялись инкрементальность (`diff` после добавления файла) и экспорт SARIF.

### 2.3 Ограничения

- Одна машина, прогретый файловый кэш; замеры CLI, а не MCP-сервера (у MCP время не инструментировано).
- Не проверялись: плагины `.archlens/plugins`, MCP Prompts/Resources, HTML-отчёт, экспорт в DOT и Structurizr, файловый baseline (`archlens baseline`), флаг `--fail-on-new-errors`.
- Поддержка XAML проверена косвенно (WPF-проект DreamAgents загружается, entry points WPF распознаны).
- Razor проверен на `TicTacToe` (40 компонентов, 38 узлов в графе, 4846 LOC).
- `analyzers` проверялся только на двух решениях.

---

## 3. Результаты по функциям

### 3.1 Точность диагностики на полигоне

Первый прогон `check` — **6 находок, 6 true positive, 0 false positive**:

```
[Error] BAD-001: BadArch.Domain.DomainService -> BadArch.Infrastructure.InfrastructureGateway (FieldOrProperty)  DomainService.cs:8
[Error] BAD-002: Namespace cycle (2 nodes, 2 edges): BadArch.Domain.Alpha -> BadArch.Domain.Beta -> BadArch.Domain.Alpha
[Error] BAD-003: Captive: BadArch.App.SingletonWorker (Singleton) -> IScopedContext => ScopedContext (Scoped)  Composition.cs:18
[Warn ] BAD-004: Potentially dead type 'BadArch.App.OrphanReportBuilder'  Composition.cs:24
[Warn ] BAD-004: Potentially dead type 'BadArch.App.Class1'  Class1.cs:3      <- бонус: мусор от `dotnet new`
[Warn ] BAD-005: max_method_cyclomatic_complexity 19 > 15 on OrderPricer  OrderPricer.cs:4
```

`Class1` — не ложное срабатывание, а реально оставшийся от шаблона класс; инструмент нашёл то, о чём я забыл. После добавления нового нарушения (`LateDomainService`) и запуска `diff` получено ровно `typeCountDelta: 1, edgeCountDelta: 4, violationCountDelta: 1` — один новый тип, четыре новых ребра, одно новое нарушение с точной локацией.

### 3.2 Dead code

| Решение | Типов | Entry points | Кандидатов | Шум |
|---|---|---|---|---|
| DreamAgents | 665 | 161 | 2 | 0 (оба — код, оставленный только для тестов) |
| TicTacToe (Blazor) | 345 | 75 | 1 | 0 |
| BadArch | 14 | — | 2 | 0 |

Ключевой результат — Blazor: 40 Razor-компонентов не попали в «мёртвый код», потому что распознаны как entry points (`UiFrameworks`, `AspNetEndpoints`, DI-регистрации). Профиль `app` по умолчанию корректно исключает тестовые проекты и честно пишет об этом в `note`.

Замечание: `ChatHistoryAgentTool` помечен unreferenced, хотя в `InfrastructureServiceCollectionExtensions.cs:249` прямо указано, что он оставлен вместе с тестами намеренно. Формально инструмент прав, но механизма «подтверждено, не удалять» нет — только ручной `exclude` в yaml.

### 3.3 DI-граф

DreamAgents: 143 регистрации (141 singleton, 2 transient, 0 scoped), **0 captive dependencies**, 35 интерфейсов с единственной реализацией.

На полигоне captive-зависимость определена точно, вместе с путём разрешения:

```
BadArch.App.SingletonWorker (Singleton) -> BadArch.App.IScopedContext => BadArch.App.ScopedContext (Scoped)
```

Это один из самых практичных видов анализа: ошибка ловится статически, до запуска, и в рантайме она проявляется как «singleton держит устаревший scoped-контекст».

### 3.4 Impact (blast radius)

`IChatRepository` в DreamAgents:

- 21 прямой зависимый, 35 транзитивных, 4 затронутых проекта;
- 2 DI-регистрации исключены (с явной пометкой в `note`);
- для каждого узла — `distance` и `file:line`.

`AgentTurnRunner`: 1 прямой, 4 транзитивных, 2 проекта.

Это самый полезный инструмент для планирования рефакторинга: ответ на вопрос «что сломается, если поменять контракт» агент иначе собирает десятком grep-ов и без гарантии полноты. Обратная сторона — размер вывода: 35 узлов ≈ 2 КБ, поэтому `summaryOnly` и `fields` обязательны в агентском workflow.

### 3.5 Hotspots и метрики

DreamAgents, топ по цикломатической сложности:

| Тип | CC (сумма) | Max метод | Строк | Ce |
|---|---|---|---|---|
| `DreamAgents.Shell.ShellHost` | 382 | 198 | 2152 | 68 |
| `DreamAgents.Application...AgentTurnRunner` | 169 | 122 (cognitive 234) | 985 | — |
| `DreamAgents.Infrastructure.Tools.SsrfGuard` | 131 | 82 | 396 | — |
| `DreamAgents.Infrastructure.Tools.GrepAgentTool` | 114 | 74 | 674 | — |
| `DreamAgents.Infrastructure.Persistence.EfCoreChatRepository` | 251 | 34 | 1416 | — |

Вывод пригоден для постановки задач без дополнительного чтения кода: адрес, метрика, величина, порог.

**Ограничение LCOM4:** у `ShellHost` LCOM4 = 1, то есть «идеальная связность», при 2152 строках и CC 382 — потому что все методы работают с теми же полями. Сортировка `hotspots --by lcom4` худший класс проекта не покажет; надёжнее `complexity` или `coupling`. Для god-class нужна составная эвристика (строки × CC × Ce).

### 3.6 SQL поверх графа

Работает и даёт то, чего нет в готовом виде ни у кого из бесплатных аналогов:

```sql
SELECT n.project_name, COUNT(*) AS types, SUM(m.line_count) AS loc,
       MAX(m.max_method_cognitive_complexity) AS worst_method
FROM nodes n JOIN metrics m ON m.node_id = n.full_name
WHERE n.kind = 'Type'
GROUP BY n.project_name ORDER BY loc DESC;
```

| Проект | Типов | LOC | Худший метод (cognitive) |
|---|---|---|---|
| Infrastructure | 155 | 22 375 | 98 |
| Shell | 156 | 12 771 | 235 |
| Application | 124 | 11 238 | 234 |
| Domain | 89 | 4 059 | 18 |
| App | 24 | 3 461 | 26 |
| Server | 41 | 3 016 | 32 |
| Dream.Ai | 76 | 2 530 | 52 |

Отдельно отмечу UX ошибок: при неверном имени колонки возвращается перечень доступных колонок, а не «SQLite Error 1». Это правильное поведение для инструмента, которым пользуется LLM.

### 3.7 Правила и пресеты

- **Кастомный `.archlens.yaml`** — сильная сторона: 5 правил, точные сообщения, координаты, пути рёбер. На полигоне сработали все.
- **Пресет `clean-architecture`** на DreamAgents: 71 нарушение, **все — CLEAN-006** (сложность), слойных нарушений 0. На TicTacToe — 12 нарушений. То есть пресет на реальных проектах вырождается в линтер сложности: слойная часть либо проходит, либо не детектируется из-за неканонических имён слоёв. Для репозитория без суффиксов `Domain`/`Application`/`Infrastructure` (как сам ArchLens) пресет бесполезен — нужен маппинг слоёв через конфиг.

### 3.8 CI: exit codes, SARIF, diff

| Проверка | Результат |
|---|---|
| `check` при наличии error-нарушений | exit code **1** |
| SARIF | 2.1.0, 1 run, 7 results, 5 rules — совместим с GitHub Code Scanning |
| `diff` к baseline | 1 новый тип, 4 ребра, 1 новое нарушение, 0 resolved |
| Инкрементальный reload | новый файл подхвачен без полного пересчёта |

Механика CI готова. Единственное, что мешает включать `no_cycles` с `severity: error` — шум, описанный ниже.

### 3.9 Analyzers

| Решение | Info | Warning | Error |
|---|---|---|---|
| DreamAgents | 122 | 0 | 0 |
| ArchLens | 0 | 0 | 0 |

Функция технически работает, но на этих репозиториях практической ценности не дала: анализаторы либо не подключены, либо дают только info-шум. Дефолт `minSeverity=info` скорее вредит; при отсутствии анализаторов инструмент возвращает пустоту без подсказки «в решении не подключены анализаторы». Против обычного `dotnet build` с warnings это дублирование.

### 3.10 Производительность

DreamAgents (7 проектов, 665 типов, 6056 рёбер), CLI Release:

| Режим | Время | Что теряется |
|---|---|---|
| roslyn cold (`--refresh --no-cache`) | 31,5 с | — |
| roslyn warm (SQLite cache) | **0,9 с** | — |
| il cold (`--refresh --no-cache`) | 1,3 с (**×24**) | −1 проект, −4 namespace, −77 типов, −2129 рёбер, −1 цикл |

Заявленные в README «10–30×» подтвердились (24×). Для агентского цикла критичен warm-режим: 0,9 с позволяют обращаться к графу на каждом шаге рассуждения. Оговорка README «для CI-гейтов используйте Roslyn» обоснована: IL-бэкенд теряет ~12 % типов и часть рёбер, поэтому для правил и циклов его нельзя считать эквивалентным.

### 3.11 Эргономика для агента

- `overview` DreamAgents — ~2,6 КБ JSON на 665 типов вместо чтения файлов; компактность выдержана.
- `loadStatus` честно сообщает backend, degraded, approximate — это важно для доверия к результату.
- Ошибки содержат подсказки (список колонок, доступные значения).
- Все инструменты имеют `summaryOnly`/`limit`/`maxItems` — контекст контролируется.
- 14 MCP-инструментов — разумный размер: нет «простыни» из 40 тулов, выбор очевиден.

---

## 4. Найденные проблемы

### P0. `no_cycles` считает нарушением отношения «родительский ↔ дочерний namespace»

DreamAgents, правило `no_cycles` (namespace): 3 нарушения, из них **2 — артефакты**:

1. `Dream.Ai ↔ Dream.Ai.Abstractions` — реализация интерфейса в дочернем namespace и тип модели в родительском.
2. `DreamAgents.Shell ↔ DreamAgents.Shell.Bridge` — `ShellBridge` реализует `IWebViewTransport` из родительского namespace.

Реальный архитектурный цикл только один: `Application.Subagents ↔ UseCases.Sessions ↔ Teams` (+ крупный кластер из 8 namespace внутри `Shell`).

Для C# отношение «родитель ссылается на ребёнка и наоборот» — нормальная организация кода, а не дефект. С `severity: error` в CI это красная сборка на ровном месте, после которой правило отключают целиком.

**Предложение:** по умолчанию пропускать циклы, где все узлы образуют цепочку предок/потомок (сравнение по префиксу namespace), либо сделать `cross_project_only: true` дефолтом для namespace-скоупа. Оба варианта требуют минимум изменений и снимают основной источник шума.

### P1. `graph` и `check` расходятся в количестве циклов

На одном снапшоте `snap-20261125-152833-22d9bb`: `graph --scope namespace` → 4 цикла, `check` с `no_cycles` → 3.

Причина найдена: `NoCyclesRule` имеет `excludeMigrations = true` по умолчанию (`src/ArchLens.Analysis/BuiltInRules.cs:191-196`), проброс — `src/ArchLens.Rules.Yaml/YamlRuleSetProvider.cs:118`. Пара `Persistence ↔ Persistence.Migrations` молча выпадает из правил, но остаётся в графе. Поведение документировано (`docs/rules-format.md:61`, `include_migrations`), но при сравнении двух команд выглядит как нестабильность.

**Предложение:** в выводе `check` показывать «N циклов скрыто (exclude_migrations)» — одна строка снимает вопрос.

### P1. Нет механизма «подтверждённого» мёртвого кода

`ChatHistoryAgentTool` (DreamAgents) и `StaticAiApiKeyProvider` (Dream.Ai) помечены unreferenced, хотя первый осознанно оставлен с тестами (комментарий в composition root), второй используется в документации и тесте. Подавление — только ручным `exclude` в yaml, без указания причины. Для живого проекта такие warning'и накапливаются и обесценивают отчёт.

**Предложение:** поле `acknowledge:` с причиной (по аналогии с baseline) либо учёт ссылок из документации как отдельной категории.

### P2. LCOM4 не выявляет god class

`ShellHost`: LCOM4 = 1 при 2152 строках и CC 382. Метрика измеряет связность через поля и «склеивает» большой класс, который всё делает через общий state. Сортировка по LCOM4 даёт ложное чувство благополучия.

**Предложение:** составное правило `god_class` (строки > N, Ce > M, max CC > K) или явная пометка в docs, что LCOM4 не предназначен для поиска god class.

### P2. `analyzers`: шумный дефолт и тихий ноль

122 info / 0 warning на DreamAgents, 0 на ArchLens, без пояснения причин. Дефолт `minSeverity=info` забивает вывод; отсутствие анализаторов не отличается от «всё чисто».

**Предложение:** дефолт `warning`, плюс диагностика «в решении не найдено пакетов анализаторов» вместо пустого результата.

### P2. Пресеты зависят от имён слоёв

`clean-architecture` на DreamAgents дал только нарушения сложности, на ArchLens слойная часть не сработала вовсе (нет суффиксов `Domain`/`Application`/`Infrastructure`). Для проектов с другой конвенцией пресет не применим без ручных правил.

**Предложение:** маппинг слоёв через конфиг (пути или regex), а не только по сегментам namespace.

### P2. Имя флага `include_infrastructure` вводит в заблуждение

Флаг отключает исключение EF-миграций, `ModelSnapshot`, `JsonSerializerContext` и `DbContext` (для LCOM4) — `src/ArchLens.Analysis/BuiltInRules.cs:396-428`. К слою `Infrastructure` это отношения не имеет: проверено правилом с `target: "DreamAgents.Infrastructure.*"` — все нарушения инфраструктуры найдены при дефолтном флаге. Формулировка в `docs/rules-format.md:74` перечисляет исключения корректно, но имя флага читается иначе.

**Предложение:** переименовать в `include_ef_noise` / `include_generated` либо явно оговорить в docs.

### P2. Размер вывода `impact` при большом fan-in

35 узлов ≈ 2 КБ на один запрос. Для агента это заметная доля контекста; при fan-in > 20 стоит по умолчанию отдавать summary, а полный список — по явному флагу.

---

## 5. Рекомендуемый рабочий процесс

**В чате с агентом:**

1. `overview` — понять масштаб и состояние загрузки.
2. `hotspots --by complexity --top 10` и `--by coupling` — выбрать цели.
3. `impact <символ> --summaryOnly` перед изменением контракта.
4. `query` для любых ад-хок вопросов вместо чтения файлов.
5. `dead_code` и `di_analysis --onlyCaptive` — раз в спринт, не на каждом шаге.

**В CI:**

1. Собственный `.archlens.yaml` (не пресет): `forbid_dependency`, `no_captive_dependencies`, `max_metric`.
2. `no_cycles` — только после P0-исправления либо с `cross_project_only: true`.
3. `archlens baseline` в main, в PR — `archlens diff --fail-on-new-errors`.
4. Отдельная job с `--backend roslyn` (не IL) для гейтов; IL — только для быстрых информационных прогонов.
5. SARIF — в GitHub Code Scanning, чтобы нарушения жили в интерфейсе ревью, а не в логах.

---

## 6. Сравнение с альтернативами

| Инструмент | Сильные стороны | Чего нет у ArchLens относительно него |
|---|---|---|
| NDepend | глубина метрик, исторические тренды, дашборды | платный, нет MCP, тяжелее в настройке |
| ArchUnitNET / NetArchTest | правила как код, интеграция в тесты | нет графа, blast radius, DI-анализа, SARIF; правила выполняются только в тестах |
| CodeQL | мощный межпроцедурный анализ, security | тяжёлая настройка под C#, нет архитектурных метрик и DI-модели |
| IDE (Rider/VS) | интерактивные диаграммы, Find Usages | недоступно агенту, нет CI-артефактов, нет SQL |
| Roslyn analyzers | точные правила на уровне синтаксиса/семантики | нет архитектурного уровня (слои, циклы, blast radius, DI lifetimes) |

Ниша ArchLens определена точно: **MCP-нативный архитектурный анализ с SQL, blast radius и SARIF**, бесплатный и расширяемый. Связка этих четырёх возможностей из готовых бесплатных компонентов не собирается.

**Когда инструмент не нужен:** решения меньше ~50 типов (cold-start Roslyn не окупается) и разовые вопросы «кто вызывает метод» (grep дешевле).

---

## 7. Итог и приоритеты

**Итог:** 8/10 как агентский инструмент, 7/10 как CI-гейт с потенциалом 8/10 после P0. Функциональность соответствует README, точность на контрольном примере — 100 % при нуле ложных срабатываний, производительность в warm-режиме — субсекундная.

Приоритеты:

| Приоритет | Задача | Эффект |
|---|---|---|
| P0 | Не считать parent↔child циклом (или `cross_project_only` по умолчанию) | убирает основной источник ложных CI-падений |
| P0 | Показывать в `check` число скрытых migration-циклов | согласует цифры `graph` и `check` |
| P1 | `analyzers`: дефолт `warning` + диагностика отсутствия анализаторов | убирает шум и «тихий ноль» |
| P1 | Правило `god_class` (строки × CC × Ce) | закрывает слепую зону LCOM4 |
| P2 | `acknowledge` с причиной для dead code | снижает накопление вечных warning'ов |
| P2 | Маппинг слоёв для пресетов через конфиг | делает пресеты применимыми вне канонических имён |
| P2 | Авто-summary в `impact` при fan-in > 20 | экономит контекст агента |
| P2 | Переименовать `include_infrastructure` | убирает неоднозначность |

---

## Приложение A. Воспроизведение

```powershell
# Полигон
dotnet new classlib -o BadArch.Domain -f net10.0
dotnet new classlib -o BadArch.Infrastructure -f net10.0
dotnet new classlib -o BadArch.App -f net10.0   # затем OutputType=Exe
dotnet new sln -n BadArch
dotnet sln BadArch.slnx add BadArch.Domain\BadArch.Domain.csproj BadArch.Infrastructure\BadArch.Infrastructure.csproj BadArch.App\BadArch.App.csproj
dotnet build BadArch.slnx

# Проверка правил
archlens check BadArch.slnx
archlens check BadArch.slnx --format sarif --output badarch.sarif

# Diff: изменить код и сравнить с baseline
archlens baseline BadArch.slnx --output .archlens-baseline.json
archlens diff BadArch.slnx --baseline .archlens-baseline.json --fail-on-new-errors

# Замеры на реальном решении
archlens overview DreamAgents.slnx --backend roslyn --refresh --no-cache --summary-only
archlens overview DreamAgents.slnx --backend il --refresh --no-cache --summary-only
archlens overview DreamAgents.slnx --summary-only           # warm cache
```

## Приложение B. Сырые числа

| Метрика | Значение |
|---|---|
| DreamAgents: проекты / namespace / типы / рёбра | 7 / 55 / 665 / 6056 |
| DreamAgents: циклы namespace (всего) | 4 (2 — parent↔child, 1 — реальный, 1 — кластер Shell) |
| DreamAgents: DI-регистрации | 143 (141 singleton, 2 transient) |
| DreamAgents: captive dependencies | 0 |
| DreamAgents: интерфейсы с одной реализацией | 35 |
| DreamAgents: dead code | 2 из 665 |
| TicTacToe: Razor-компоненты / узлы / LOC | 40 / 38 / 4846 |
| TicTacToe: dead code | 1 из 345 |
| BadArch: нарушений / TP / FP (первый прогон) | 6 / 6 / 0 |
| BadArch: нарушений после добавления дефекта | 7 / 7 / 0 |
| ArchLens self-check | 7 правил, 30 нарушений (все ARCH-005) |
| Roslyn cold / IL cold / warm | 31,5 с / 1,3 с / 0,9 с |
| IL-потери относительно Roslyn | −77 типов, −2129 рёбер, −1 цикл |
| SARIF | 2.1.0, 7 results, 5 rules, exit 1 |
