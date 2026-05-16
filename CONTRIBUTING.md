# Contributing to TcKit

## Adding a new adapter

Adapters live under `tckit/adapters/<port_kind>/`. The one hard rule: adapters import only from `tckit.ports` and stdlib. Never from another adapter (linting enforces this).

**File naming.** Match the last word (singular) of the adapter folder. So files under `readers/` end in `_reader`, files under `writers/` end in `_writer`, files under `test_runners/` end in `_runner`, `doc_generators/` ends in `_generator`, `docs_searchers/` ends in `_searcher`, `builders/` ends in `_builder`. Examples: `xml_reader.py`, `xae_com_builder.py`, `tcunit_runner.py`, `beckhoff_infosys_searcher.py`. The class name is the PascalCase form (`XmlReader`, `BeckhoffInfosysSearcher`).

**Registry key.** The short string a user can put in `~/.tckit/config.toml` (or a project-local `config.json`) is the tool name only, no port suffix. Keep it terse. Pattern: `key = "<tool>"` maps to `class = <Tool><PortSuffix>`. Examples:

| Registry key | Class |
|---|---|
| `"xml"` | `XmlReader` |
| `"automation_interface"` | `AutomationWriter` |
| `"xae_com"` | `XaeComBuilder` |
| `"tcunit"` | `TcUnitRunner` |
| `"html"` | `HtmlGenerator` |
| `"beckhoff_infosys"` | `BeckhoffInfosysSearcher` |

The docs page filename also follows a pattern: kebab-case `{tool}-{port_suffix}.md` under `docs/content/capabilities/<port>/`, e.g. `xml-reader.md`, `beckhoff-infosys-searcher.md`.

1. **Create** the file in the correct `adapters/` subdirectory:
   ```
   tckit/adapters/<port_folder>/<tool>_<suffix>.py
   ```

2. **Import only** from `tckit.ports` and stdlib. Never from other adapters.

3. **Implement** all abstract methods from the port ABC. For `ProjectReader` that's six:
   ```python
   from tckit.ports.reader import ProjectReader
   from tckit.ports.types import (
       DUT, GVL, POUDeclaration, POUInterface, POUItem, ProjectStructure,
   )

   class MyFormatReader(ProjectReader):
       def get_structure(self, project_path: str, *, plc_name: str | None = None) -> ProjectStructure: ...
       def get_pou_interface(self, pou_name: str, *, plc_name: str | None = None) -> POUInterface: ...
       def get_pou_declaration(self, pou_name: str, *, plc_name: str | None = None) -> POUDeclaration: ...
       def get_pou_item(self, pou_name: str, item_name: str, *, plc_name: str | None = None) -> POUItem: ...
       def get_gvl(self, gvl_name: str, *, plc_name: str | None = None) -> GVL: ...
       def get_dut(self, dut_name: str, *, plc_name: str | None = None) -> DUT: ...
   ```

4. **Register** the class in `tckit/config.py` under the appropriate registry, using a short tool-name key:
   ```python
   _READER_REGISTRY["my_format"] = MyFormatReader
   ```

5. **Write** unit tests in `tests/unit/`.

6. **Document** in `docs/content/capabilities/<port>/<adapter>.md` and add it to `docs/mkdocs.yml`. If the adapter changes a default that users would set, mention the key in `tckit/templates/config.toml.example`.

## Adding a new port

Only do this if there is a genuinely new external concern — not a variation of an existing one. Open an issue first to discuss before implementing.

1. Define the ABC in `tckit/ports/`
2. Keep method signatures minimal
3. Return types must be dataclasses defined in `tckit/ports/types.py`
4. Update `tckit/server.py` to expose the port as MCP tools
5. Implement at least one adapter before merging

## Code style

- Python 3.11+, type hints everywhere, `strict` mypy
- Run `ruff check tckit/` before committing — CI will reject lint failures
- RST-format docstrings on all public methods
- No comments explaining what the code does — only why (non-obvious constraints, workarounds)

## Editing or adding skills

Skills live in two places by necessity: `.claude/skills/` is read by Claude Code when working in this repo, and `plugin/skills/` is the copy that ships to end users via the Claude Code plugin marketplace. The plugin manifest expects its own bundled copy, so both must be in git.

When you add a skill under `.claude/skills/<name>/SKILL.md`, decide its audience:

- **User-facing** (about working on a TwinCAT project — e.g. reading a project, writing ST, building, beckhoff research). It ships to users. After editing, run `python scripts/sync-skills.py` to mirror it to `plugin/skills/` and commit both.
- **Internal** (about working on the TcKit codebase itself — e.g. editing docs, writing an ADR). It must NOT ship to users. Add its folder name to the `INTERNAL` set in [scripts/sync-skills.py](scripts/sync-skills.py); the sync will then skip it and CI will tolerate its absence from `plugin/skills/`.

For SKILL.md frontmatter, body conventions, and trigger-phrase tuning, use the built-in `skill-creator` skill — it covers the general format. Match the tone and structure of the existing `tc-*` skills in `.claude/skills/` for project consistency (numbered procedure, "Anti-patterns" section, "Next" handoff).

CI verifies parity with `python scripts/sync-skills.py --check` and rejects PRs that have drifted.

## Running the full check locally

```bash
docker compose -f docker/docker-compose.yml run tckit ruff check tckit/
python scripts/check-adapter-isolation.py
python scripts/sync-skills.py --check
docker compose -f docker/docker-compose.yml run tckit pytest tests/unit/ -v
```

## Opening a pull request

- Branch from `main`, name: `feat/`, `fix/`, `chore/`
- One logical change per PR
- Unit tests must pass
- Include a description of what the adapter/feature does and how you tested it
