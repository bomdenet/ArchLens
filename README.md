# ArchLens

**ArchLens** — open-source, agent-friendly движок анализа архитектуры C#/.NET проектов, построенный поверх **Roslyn** и предоставляющий компактные, структурированные инструменты через **MCP (Model Context Protocol)** и **CLI**.

Цель проекта — дать AI-агентам «архитектурное зрение» (аналогичное NDepend, но бесплатное, расширяемое и спроектированное под ограничения контекстного окна LLM), а также возможность запускать проверки чистоты архитектуры в CI/CD и локально.

---

## Возможности MVP (v0.1)

- **Семантический граф зависимостей** (Roslyn `MSBuildWorkspace`, поддержка `.slnx`, `.sln`, `.csproj`).
- **Поиск циклов** (Strongly Connected Components / алгоритм Тарьяна) на уровне проектов, неймспейсов и типов.
- **Архитектурные метрики:** Afferent/Efferent Coupling ($C_a$, $C_e$), Instability ($I$), Abstractness ($A$), Distance from Main Sequence ($D$), цикломатическая сложность методов, LOC, количество параметров конструктора.
- **Декларативные правила архитектуры** в `.archlens.yaml` (`forbid_dependency`, `allow_only_dependencies`, `no_cycles`, `max_metric`, `naming_convention`).
- **Blast Radius (`impact`)** — оценка транзитивного влияния изменений в типе перед рефакторингом.
- **Цикл обратной связи (`diff`)** — сравнение текущего состояния на диске с baseline-снимком сессии.
- **Два тонких адаптера поверх единого ядра:**
  - `ArchLens.Mcp` — stateful MCP-сервер (`stdio`), держит граф солюшена в памяти для мгновенных ответов агенту.
  - `ArchLens.Cli` — CLI-утилита с человекочитаемым и `--json` выводом (и exit code `1` при ошибках правил в CI).

---

## Быстрый старт

### Требования
- [.NET 10 SDK](https://dotnet.microsoft.com/)

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
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:/Users/bomde/source/repos/ArchLens/src/ArchLens.Mcp/ArchLens.Mcp.csproj",
        "-c",
        "Release"
      ]
    }
  }
}
```
> Для более быстрого старта собери `dotnet publish src/ArchLens.Mcp -c Release` и укажи путь к скомпилированному `.exe` / `.dll`.

---

## Документация

- [docs/architecture.md](docs/architecture.md) — слои, каноническая модель `CodeGraph` и точки расширения.
- [docs/mcp-tools.md](docs/mcp-tools.md) — список MCP-инструментов, параметры и рекомендуемый workflow агента.
- [docs/rules-format.md](docs/rules-format.md) — справочник по формату `.archlens.yaml`.
- [todo.md](todo.md) — дорожная карта развития проекта.
