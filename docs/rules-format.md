# Формат декларативных правил (`.archlens.yaml`)

Файл `.archlens.yaml` (или `.archlens.yml`) размещается в корне репозитория рядом с `.slnx` / `.sln` и обнаруживается автоматически.

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

---

## Glob-паттерны

- `*` — любая последовательность символов (включая точки в неймспейсах).
- `?` — один любой символ.
- Сопоставление регистронезависимое.
