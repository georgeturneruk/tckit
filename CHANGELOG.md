# Changelog

All notable changes to TcKit are documented here. The format is loosely based
on [Keep a Changelog](https://keepachangelog.com/), and this project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- **`.TcIO` interface files are now discovered.** The doc generator only
  globbed `.TcPOU`/`.TcGVL`/`.TcDUT`; projects that store interfaces in
  the dedicated `.TcIO` extension (TcUnit being the canonical example)
  had their interface pages silently dropped, leaving dangling links
  from any FB that referenced them.
- **GVL page-level description no longer eats the whole declaration.**
  The comment extractor's preamble walker didn't recognise `VAR_GLOBAL`
  as a boundary keyword, so for a GVL it consumed the entire declaration
  and any inline `(* ... *)` per-variable comment was misclassified as
  the GVL's doc comment.
- **Block comments no longer leak across newlines into the wrong
  variable.** The trailing-comment branch in `_VAR_LINE_RE` used
  `\s*` (which crosses newlines) so a `(* ... *)` preceding the *next*
  variable was being attached to the variable above. Tightened all
  horizontal-whitespace matches in the variable-line regex to `[ \t]*`
  and constrained type/default groups to a single line.
- **GVL fields are now extracted.** `_VAR_BLOCK_RE` previously skipped
  `VAR_GLOBAL` blocks, so every GVL page rendered with an empty variable
  table and only the raw declaration at the bottom. `GVL_Param_TcUnit` on
  the published example now shows a proper field listing.
- **Struct fields are now extracted.** TwinCAT structs use
  `STRUCT … END_STRUCT` rather than `VAR … END_VAR`, which the parser
  missed entirely. Added `_parse_struct_fields()` (covers `UNION` too).
- **Enum members are now extracted** into a name/value table instead of
  dumping the raw `.st` declaration.
- **Inline `(* … *)` block comments** on variable lines are now captured
  alongside `// …` line comments. The `(* seconds *)`-style annotations
  in the TcUnit fixture used to be silently dropped.
- **Default values** (`:= 3`, `:= TRUE`, etc.) on variable, GVL, and
  struct-field declarations are now captured and rendered in their own
  Default column. The column only appears when at least one row in the
  table has a default, so FB inputs/outputs (which typically don't) keep
  the previous three-column layout.
- **Method and property return types** sit inline next to the name in
  the item-card header (`Execute : BOOL`). Previously they floated to
  the right of the row via `margin-left: auto` and were easy to miss.

### Changed

- `Implementation` per method and `Declaration source` at the bottom of
  every object page are now collapsed by default using native HTML
  `<details>`. No JS required to operate them.
- Generated HTML is now responsive at narrow viewports. Added a mobile
  `@media (max-width: 768px)` block: the sidebar becomes a tap-to-open
  drawer, padding shrinks, headings scale down, and tables wrap on word
  boundaries instead of forcing horizontal page scroll.

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

[0.2.0]: https://github.com/georgeturneruk/tckit/releases/tag/v0.2.0
