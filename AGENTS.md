# ArchLens — Agent Guidelines

## Architecture Rules & Layering
- Keep `ArchLens.Model` dependency-free (zero references to other ArchLens projects).
- Keep `ArchLens.Analysis` independent of Roslyn, YAML, SQLite/Query, MCP, and CLI.
- Keep `ArchLens.Extraction.*` dependent only on `ArchLens.Model`.
- Keep `ArchLens.Query` dependent only on `ArchLens.Model` and `ArchLens.Analysis`.
- Keep namespaces acyclic (`dotnet run --project src/ArchLens.Cli -- check .`).

## Build & Verification
- Build: `dotnet build ArchLens.slnx`
- Test: `dotnet test ArchLens.slnx`
- Self-check: `dotnet run --project src/ArchLens.Cli -- check .`

## Commits & Versioning Policy (Mandatory)
After completing any task, always commit your changes cleanly to git following this versioning rule:

1. **Functional / behavioral changes — bump `<Version>` in `Directory.Build.props` before committing:**
   - Any change that affects runtime behavior, analysis results, metrics, extraction, rules, presets, CLI commands/flags, MCP tools/payloads, caching, or bug fixes **must** increment `<Version>` in `Directory.Build.props` following SemVer:
     - **Patch** (`0.x.Y+1`) for bug fixes and backwards-compatible internal improvements.
     - **Minor** (`0.X+1.0`) for new features, new CLI/MCP options, or significant behavioral enhancements.
   - `ArchLens.Mcp` reads its `ServerInfo.Version` dynamically from assembly metadata (`Directory.Build.props`), so `Directory.Build.props` is the single source of truth.
2. **Non-functional changes — commit WITHOUT bumping `<Version>`:**
   - Pure documentation edits (`README.md`, `docs/*`, `AGENTS.md`), comments, formatting, or cleanup of unused/temporary files do **not** bump `<Version>`.
3. Always run `dotnet test ArchLens.slnx` before committing functional changes and ensure working tree has no leftover temporary files.
