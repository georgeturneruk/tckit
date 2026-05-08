---
name: tc-add-adapter
description: Use when adding a new adapter to TcKit's hexagonal architecture — a new reader, writer, builder, test runner, doc generator, or docs searcher implementation. Triggers on requests like "add a new adapter for X", "implement an LSP-based reader", "wire up a new build backend". Enforces the 7-step procedure and the adapter-isolation rule (adapters import only from ports and stdlib). Do NOT use for adding a new port — that is a separate, rarer change that needs user discussion first.
allowed-tools: Read, Write, Edit, Glob, Grep, Bash
---

# Adding a new adapter to TcKit

Adapters live under `tckit/adapters/<port_kind>/`. The one hard rule: adapters import only from `tckit.ports` and stdlib. Never from another adapter. Linting enforces this.

## Procedure

1. **Locate.** Place the new file under the correct subdirectory: `tckit/adapters/readers/`, `writers/`, `builders/`, `test_runners/`, `doc_generators/`, or `docs_searchers/`. Filename is descriptive (e.g. `lsp_reader.py`).
2. **Imports.** Top of file: `from tckit.ports.<port_module> import <PortClass>` plus stdlib only. If you find yourself wanting to import another adapter, stop — extract the shared logic into `tckit/utils/` first.
3. **Implement.** Subclass the port. Implement every abstract method. Return the dataclasses defined in `tckit/ports/types.py`; don't invent new return shapes.
4. **Register.** Add the adapter to the registry in `tckit/config.py` so it can be selected by name.
5. **Config example.** Add the adapter's name to `config.example.json` under the relevant port slot.
6. **Tests.** Add unit tests under `tests/unit/test_<short_name>.py`. Use the fixtures in `tests/fixtures/sample_project/` for anything that needs a real `.TcPOU`.
7. **Docs.** Add a page under `docs/content/capabilities/<port_kind>/<short_name>.md` describing what external tool it wraps, its config keys, and any platform requirements.

## Verify

- `ruff check tckit/` — the adapter-isolation lint must pass.
- `pytest tests/unit/test_<short_name>.py` — new unit tests green.
- `pytest tests/` — nothing else regressed.
