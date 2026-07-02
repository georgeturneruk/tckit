# Contributing to TcKit

TcKit is a C#/.NET 8 solution under `dotnet/`. Code conventions (naming, async rules, COM discipline, the MCP contract, result shapes) live in [dotnet/CONVENTIONS.md](dotnet/CONVENTIONS.md); this file covers structure and workflow.

## Adding a new adapter

Adapters are separate projects under `dotnet/src/TcKit.Adapters.<Name>/`. The one hard rule: an adapter references only `TcKit.Core` and its own external SDK, never a sibling adapter. The project graph enforces it. `TcKit.Server` is the composition root and references every adapter to register it in DI.

1. **Create** the project and reference `TcKit.Core` only.
2. **Implement** the port interface from `TcKit.Core.Ports`. If the adapter drives an external system (COM, ADS, HTTP), put that behind a seam interface so the logic is testable against an in-memory fake; see the automation seam in CONVENTIONS.md.
3. **Register** it in `dotnet/src/TcKit.Server/Program.cs` and expose the tools in `dotnet/src/TcKit.Server/Tools/` (PascalCase tool names, camelCase parameters, snake_case output JSON).
4. **Add** a `TcKit.Cli` verb so the adapter can be driven directly during development and live smokes.
5. **Test** in `dotnet/tests/TcKit.Tests` against the fake seam. When you learn a quirk on live XAE or a live runtime, pin it as a fake-backed test so it cannot regress.
6. **Document** the capability under `docs/content/capabilities/` and add it to `docs/mkdocs.yml`.

## Adding a new port

Only do this if there is a genuinely new external concern, not a variation of an existing one. Open an issue first to discuss before implementing.

1. Define the interface in `dotnet/src/TcKit.Core/Ports/`, `I`-prefixed, async, `CancellationToken` on every method.
2. Return types are immutable records in `TcKit.Core/Models/`.
3. Keep method signatures minimal.
4. Implement at least one adapter before merging.

## Local commands

Requires the .NET 8 SDK.

```bash
dotnet build dotnet/TcKit.sln
dotnet test dotnet/TcKit.sln
python scripts/sync-skills.py --check
```

CI (`.github/workflows/dotnet-ci.yml`) runs the build, the tests, and the skills drift check on Linux. Live validation against a real TwinCAT 4026 is inherently on-machine; the harnesses are in [dotnet/oracle/](dotnet/oracle/).

## Editing or adding skills

Skills live in two places by necessity: `.claude/skills/` is read by Claude Code when working in this repo, and `plugin/skills/` is the copy that ships to end users via the Claude Code plugin marketplace. The plugin manifest expects its own bundled copy, so both must be in git.

When you add a skill under `.claude/skills/<name>/SKILL.md`, decide its audience:

- **User-facing** (about working on a TwinCAT project — e.g. reading a project, writing ST, building, beckhoff research). It ships to users. After editing, run `python scripts/sync-skills.py` to mirror it to `plugin/skills/` and commit both.
- **Internal** (about working on the TcKit codebase itself — e.g. editing docs, writing an ADR). It must NOT ship to users. Add its folder name to the `INTERNAL` set in [scripts/sync-skills.py](scripts/sync-skills.py); the sync will then skip it and CI will tolerate its absence from `plugin/skills/`.

For SKILL.md frontmatter, body conventions, and trigger-phrase tuning, use the built-in `skill-creator` skill — it covers the general format. Match the tone and structure of the existing `tc-*` skills in `.claude/skills/` for project consistency (numbered procedure, "Anti-patterns" section, "Next" handoff).

CI verifies parity with `python scripts/sync-skills.py --check` and rejects PRs that have drifted.

## Opening a pull request

- Branch from `main`, name: `feat/`, `fix/`, `chore/`
- One logical change per PR
- Unit tests must pass
- Include a description of what the adapter/feature does and how you tested it
