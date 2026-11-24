# ArchLens — Roadmap & TODO

Этот файл описывает направления развития ArchLens от текущего MVP (v0.1) к полноценной расширяемой платформе архитектурного анализа для AI-агентов и разработчиков.

---

## Milestone 1: Стабилизация MVP и качество экстракции (v0.2)
- [x] **Автоинвалидация кэша (mtime / `FileSystemWatcher`)**: автоматически пересчитывать только изменённые проекты при правках на диске вместо ручного `refresh: true`.
- [x] **Точная привязка рёбер к методам (`NodeKind.Method`)**: сейчас граф строится на уровне `Project -> Namespace -> Type`; добавить опциональный метод-уровень для call-graph и поиска неиспользуемых методов.
- [x] **Улучшение работы с multi-targeting (`TargetFrameworks`)**: позволить выбирать целевой TFM через `ExtractionOptions`.
- [x] **Когнитивная сложность (Cognitive Complexity)** в дополнение к цикломатической — гораздо лучше отражает реальную трудность чтения кода.
- [x] **LCOM4 (Lack of Cohesion of Methods)** — для точного выявления кандидатов на разбиение God-классов.
- [x] **Упаковка в `.NET Global Tool`** (`dotnet tool install -g ArchLens.Mcp` и `ArchLens.Cli`) + GitHub Actions CI.

## Milestone 2: Агентские суперсилы (v0.3)
- [x] **Dead Code & Unreferenced Symbols (`dead_code`)**:
  - спроектировать интерфейс `IEntryPointDetector`;
  - встроенные детекторы точек входа: `Program.Main`, ASP.NET Controllers / Minimal API endpoints, MediatR/MassTransit handlers, xUnit/NUnit/MSTest, классы с DI-регистрацией, публичные API библиотек.
- [x] **Анализ DI-графа (`IServiceCollection`)**:
  - парсинг `AddSingleton / AddScoped / AddTransient`;
  - выявление *captive dependencies* (Scoped внутри Singleton);
  - обнаружение интерфейсов с единственной реализацией и «скрытой» связности.
- [x] **Поддержка SARIF 2.1.0** в CLI для интеграции с GitHub Code Scanning / Azure DevOps / IDE.
- [x] **Агрегация Roslyn Analyzers**: подмешивание диагностик `Microsoft.CodeAnalysis.NetAnalyzers` / `Roslynator` в общий архитектурный отчёт (с фильтрацией по категориям `Design`, `Maintainability`, `Performance`).
- [x] **Сохранение baseline на диск (`.archlens-baseline.json`)**: чтобы сравнивать ветку/PR с `main` не только внутри одной MCP-сессии, но и в CI/CD.

## Milestone 3: Производительность и альтернативные бэкенды (v0.4)
- [x] **IL-экстрактор (`ArchLens.Extraction.Cecil` / `System.Reflection.Metadata`)**:
  - реализовать `ICodeGraphExtractor` для чтения скомпилированных `.dll` + `.pdb`;
  - ускорение анализа на монорепозиториях из 100+ проектов в 5–10×.
- [x] **Персистентный кэш графа** (SQLite / MessagePack) между перезапусками MCP-сервера.
- [x] **Инкрементальная перекомпиляция Roslyn**: обновлять только изменившиеся `Document` в `MSBuildWorkspace` вместо полного переоткрытия `.sln`.

## Milestone 4: Максимальная расширяемость и экосистема (v0.5+)
- [x] **Динамическая загрузка плагинов (`AssemblyLoadContext`)**:
  - пользователь кладёт свой `.dll` с реализациями `IArchitectureRule`, `ICustomMetricCalculator` или `IEntryPointDetector` в `.archlens/plugins/` — движок подхватывает их при включённом opt-in (`--allow-plugins` / `ARCHLENS_ALLOW_PLUGINS=1`).
- [x] **Язык ad-hoc запросов (мини-CQLinq / SQL over Graph)**:
  - проекция `CodeGraph` в in-memory SQLite (`nodes`, `edges`, `metrics`) + MCP-инструмент `query` (read-only SQL), позволяющий агенту задавать произвольные вопросы к архитектуре.
- [x] **Архитектурные пресеты (Templates)**: готовые наборы правил для Clean Architecture, Vertical Slice Architecture, Modular Monolith, DDD.
- [x] **Визуализация для людей (опционально)**:
  - экспорт графа в Mermaid / DOT (Graphviz) / Structurizr DSL;
  - генерация статического HTML-отчёта (dependency matrix, тренды метрик).
- [x] **MCP Resources & Prompts**:
  - добавить готовые MCP Prompts (например, `audit-architecture`, `plan-layer-refactoring`) для стандартизированного архитектурного ревью агентом.
