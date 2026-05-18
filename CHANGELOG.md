# Changelog

All notable changes to TcKit are documented here. The format is loosely based
on [Keep a Changelog](https://keepachangelog.com/), and this project follows
[Semantic Versioning](https://semver.org/).

## [0.2.1] - 2026-05-18

### Changed

- **Breaking - writer API.** `update_pou_item` is split into three explicit
  methods: `update_pou_declaration`, `update_pou_implementation`, and
  `update_method_body`. Callers that used the old single entry point will
  need to pick the explicit method. (#93)

### Added

- **Multi-PLC solutions.** Solutions can now host more than one PLC with
  one `.tsproj` per PLC, plus library-placeholder authoring for cross-PLC
  references. (#58, #71, #76, #81)
- **New writer primitives.** `add_gvl`, `save_plc_as_library(overwrite=)`,
  `add_library_placeholder`, and `read_symbols`. (#76, #92)
- **Patch-style writes.** Surgical edits via `update_*_patch` variants
  and a substitution language documented in the `tc-write-st` skill. (#52)
- **TcUnit bridge harness.** New `TcUnitRunner` adapter, runtime-mode
  rewrite, and xUnit cascade for library-parameter overrides. (#65, #69,
  #90, #94)
- **CLI UX.** `tckit init` walkthrough, `tckit doctor` surfacing of
  missing config, a single bundled config template, and the `tckit docgen`
  subcommand with HTML and markdown adapters. (#60, #99)

### Fixed

- **Doc-generator extraction.** `.TcIO` interface files, struct / enum /
  GVL field tables, inline `(* ... *)` comments on variable lines,
  `:= ...` default values, and inline return-type rendering on method
  headers all work now. Generated HTML is also responsive at narrow
  viewports. (#62)
- **Bridge robustness.** Central route timeouts, lazy-loaded source
  trees, explicit BootAutostart, COM retries, flushed `File.SaveAll`,
  and stale `.~u` lock cleanup. (#80, #91, #94)

### Skills

- New `tc-orient-project` skill for first-touch project orientation (#44),
  plus a compacted `CLAUDE.md` and split-out topic skills (#96).
- ADR convention and `tc-adr` skill formalised (#37).

## [0.2.0] - 2026-05-09

First release published to PyPI and as a Claude Code plugin.

### Added

- **Stdio MCP transport as the default.** `tckit` now defaults to stdio so the
  package can be installed via `pipx`/`uvx` and registered with
  `claude mcp add tckit -- tckit`. SSE remains available via
  `tckit --transport sse` for the Docker / long-running server path.
- **Layered config loading.** Reads from `~/.tckit/config.toml` (Python 3.11
  stdlib `tomllib`), walks up from cwd for `.env`, falls back to
  `$TCKIT_HOME/.env`. `TCKIT_HOME` env var redirects the user-global location.
  Resolution order: env vars > project `config.json` > user TOML > defaults.
- **`tckit` CLI subcommands**: `tckit config show`, `tckit config validate`,
  `tckit doctor`. Doctor pings the Windows bridge and validates AMS Net IDs.
- **`tc-config` skill** under `.claude/skills/` and bundled into the plugin.
  Drives the init walkthrough and ongoing edits to safety stance, target,
  and runtime mode.
- **Claude Code plugin packaging** under `plugin/`, with manifest, `.mcp.json`,
  bundled skills, README, and licence. Self-hosted marketplace at
  `.claude-plugin/marketplace.json`.
- **`uv` first-run install** via `uvx tckit` in the plugin's MCP config — no
  separate `pipx install` step for plugin users.
- CI matrix: existing Docker-based lint + unit tests, plus a new
  pip-install smoke-test job that confirms the package installs cleanly
  outside Docker and the `tckit` console script is wired correctly.
- CI skills-drift check that fails the build if `.claude/skills/` and
  `plugin/skills/` diverge.

### Changed

- `pyproject.toml` console script `tckit` now points at `tckit.cli:main`
  (was `tckit.server:main`). The bare `python -m tckit.server` invocation
  still works for the server-only path.
- `TcKitConfig.get()` precedence flipped: env vars now win over file values
  (was: file values won over env). Aligns with the Unix convention and the
  documented resolution order.
- `pytest` defaults to deselecting the `network` marker (`-m "not network"`)
  so `pytest tests/` no longer hangs on infosys lookups. Network tests still
  runnable with `pytest -m network`. Closes #28.
- Dockerfile `CMD` is now `tckit --transport sse` (explicit) so the container
  path keeps binding `:8000` even though the package default is stdio.

### Fixed

- `jinja2` (and `markupsafe`) moved from the optional `[docs]` extras to
  base dependencies. They are imported unconditionally by the html and
  markdown doc generators, so a bare `pip install tckit` was previously
  broken on first doc-generator call.

### Skills

Five skills now ship: `tc-read-project`, `tc-beckhoff-docs`, `tc-write-st`,
`tc-build-test-loop`, `tc-config`. The first four were introduced in #23 (a
0.1.x change); `tc-config` is new in 0.2.0.

[0.2.1]: https://github.com/georgeturneruk/tckit/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/georgeturneruk/tckit/releases/tag/v0.2.0
