# Формат декларативных правил (`.archlens.yaml`)

Файл `.archlens.yaml` (или `.archlens.yml`) размещается в корне репозитория рядом с `.slnx` / `.sln` и обнаруживается автоматически.

---

## Архитектурные пресеты (`preset` / `presets`)

Вместо ручного описания типовых слоёв можно подключить один или несколько встроенных пресетов (`clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`) и при необходимости дополнить их собственными `rules`:

```yaml
version: 1
preset: modular-monolith
root_namespace: "MyApp"

rules:
  - id: CUSTOM-001
    name: Keep classes cohesive
    severity: warning
    type: max_metric
    metric: lcom4
    threshold: 2
```

Сгенерировать шаблон можно командой `archlens init --preset clean-architecture --root-namespace MyApp`.

---

## Поддерживаемые типы правил (`type`)

### 1. `forbid_dependency`
Запрещает типам, подходящим под `from`, ссылаться на типы, подходящие под любой паттерн из `to`.

```yaml
- id: ARCH-001
  name: Domain must not depend on Infrastructure
  severity: error
  type: forbid_dependency
  from: "MyApp.Domain.*"
  exclude_from:
    - "MyApp.Domain.Bootstrap.*"
  to:
    - "MyApp.Infrastructure.*"
    - "MyApp.Web.*"
```

### 2. `allow_only_dependencies`
Белый список зависимостей: типы из `from` могут зависеть только от самих себя (`from`) и перечисленных в `allow` паттернов.

```yaml
- id: ARCH-002
  name: Application may only depend on Domain
  severity: error
  type: allow_only_dependencies
  from: "MyApp.Application.*"
  allow:
    - "MyApp.Domain.*"
```

### 3. `no_cycles`
Запрещает циклические зависимости на заданном уровне (`project`, `namespace`, `type` или `method`).

```yaml
- id: ARCH-003
  name: No namespace cycles
  severity: error
  type: no_cycles
  scope: namespace
```

### 4. `max_metric`
Контролирует пороговые значения метрик для типов.

Поддерживаемые `metric`:
- `cyclomatic_complexity` — максимальная цикломатическая сложность метода в типе
- `total_cyclomatic_complexity` — суммарная цикломатическая сложность типа
- `cognitive_complexity` — максимальная когнитивная сложность метода в типе
- `total_cognitive_complexity` — суммарная когнитивная сложность типа
- `lcom4` — Lack of Cohesion of Methods 4 (количество несвязанных компонент методов; `>= 2` — кандидат на разбиение)
- `efferent_coupling` (или `ce`) — количество исходящих зависимостей на внутренние типы
- `afferent_coupling` (или `ca`) — количество входящих зависимостей
- `instability` — индекс нестабильности ($0.0 \dots 1.0$)
- `line_count` (или `loc`) — размер типа в строках
- `constructor_params` — максимальное количество параметров конструктора
- *Любая кастомная метрика* (`MetricName`), зарегистрированная через плагин `ICustomMetricCalculator` в `.archlens/plugins/*.dll`

```yaml
- id: ARCH-004
  name: Limit constructor injection bloat
  severity: warning
  type: max_metric
  metric: constructor_params
  threshold: 6
  target: "MyApp.*"
```

### 5. `naming_convention`
Проверяет соответствие короткого имени типа glob-паттерну.

```yaml
- id: ARCH-005
  name: Interfaces must start with I
  severity: warning
  type: naming_convention
  target: "MyApp.*"
  type_kind: Interface
  pattern: "I*"
```

### 6. `no_captive_dependencies`
Запрещает captive dependencies в DI-контейнере (`Singleton -> Scoped`, включая транзитивные цепочки через `Transient`).

```yaml
- id: ARCH-006
  name: No captive DI dependencies
  severity: error
  type: no_captive_dependencies
```

### 7. `no_dead_code`
Проверяет наличие недостижимых (потенциально мёртвых) типов или методов относительно встроенных и плагинных детекторов точек входа (`IEntryPointDetector`).

```yaml
- id: ARCH-007
  name: No dead internal types
  severity: warning
  type: no_dead_code
  scope: type
  target: "MyApp.*"
  treat_public_api_as_entry_point: true
```

### 8. `slice_isolation`
Изолирует модули или вертикальные фичи (например, `*.Modules.<Module>.*` или `*.Features.<Slice>.*`), разрешая межмодульные ссылки только на явно перечисленные контрактные под-неймспейсы.

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

## Glob-паттерны

- `*` — любая последовательность символов (включая точки в неймспейсах).
- `?` — один любой символ.
- Сопоставление регистронезависимое.
