# CLAUDE.md — TcKit

This file is read automatically by Claude Code at the start of every session.
It contains everything Claude needs to work effectively on the TcKit codebase.

---

## What This Project Is

TcKit is an MCP server that connects Claude Code to TwinCAT 3 PLC projects.
It allows Claude to read project structure, write ST code, trigger builds,
deploy to external targets, run TcUnit tests, and iterate autonomously.

Target platform: TwinCAT 3.1 Build 4026.
Python package lives in `tckit/`. Bridge service (Windows) lives in `bridge/`.

---

## Architecture — The One Rule

**Adapters may only import from ports and stdlib. Never from each other.**

This is enforced by linting. Do not break this rule under any circumstances.

```
MCP Server → Port (abstract) → Adapter (concrete) → External tool
```

The MCP server calls ports. Ports define interfaces. Adapters implement them.
If you need to share logic between adapters, put it in a utility module under
`tckit/utils/` and import that — never import adapter-to-adapter.

---

## Project Structure

```
tckit/
├── tckit/
│   ├── server.py           ← MCP server entry point
│   ├── config.py           ← config + .env loader
│   ├── ports/              ← abstract base classes ONLY
│   │   ├── reader.py
│   │   ├── writer.py
│   │   ├── builder.py
│   │   ├── test_runner.py
│   │   ├── doc_generator.py
│   │   └── docs_searcher.py
│   └── adapters/           ← concrete implementations
│       ├── readers/
│       ├── writers/
│       ├── builders/
│       ├── test_runners/
│       ├── doc_generators/
│       └── docs_searchers/
├── bridge/                 ← PowerShell, Windows only
├── tests/
│   └── fixtures/sample_project/   ← real .TcPOU files for testing
├── docs/                   ← MkDocs website
└── docker/
```

---

## How to Read TwinCAT Project Files

Always use the layered approach. Never fetch more than needed.

```
1. get_structure()          → names and types only, no code
2. get_pou_interface()      → declarations + method signatures, no bodies
3. get_pou_item()           → single method/action/property body only
```

**Never** fetch a full POU when you only need one method.
**Never** call `get_structure()` on every task — only when you need the map.
If you need to understand one method, call `get_pou_item()` directly if you
already know the POU name.

---

## Before Writing Code That Uses an Unknown FB

Always call `find_fb()` on the DocsSearcher port first.
Beckhoff FBs have specific input/output conventions and timing requirements
that are not reliably in training data, especially for newer TF libraries.

```python
# Before writing code that calls FB_EcCoESdoRead:
docs = docs_searcher.find_fb("FB_EcCoESdoRead")
# Read inputs, outputs, timing notes, then write the code
```

---

## Writing ST Code

### Comment Style — RST (reStructuredText)
Always write RST-format comments. This is aligned with Beckhoff TE1030
and feeds into the Sphinx doc generation pipeline.

```pascal
// :Description: Brief description of what this FB does
// :param bEnable: Rising edge triggers the operation
// :param sNetId: AMS Net ID of the target device
// :returns: TRUE when operation completes successfully
METHOD Execute : BOOL
VAR_INPUT
    bEnable  : BOOL;
    sNetId   : T_AmsNetId;
END_VAR
```

### Naming Conventions
Follow standard TwinCAT conventions:
- Function Blocks: `FB_` prefix e.g. `FB_MotorControl`
- Programs: `PRG_` prefix e.g. `PRG_Main`
- Global Variable Lists: `GVL_` prefix e.g. `GVL_Parameters`
- Enumerations: `E_` prefix e.g. `E_MotorState`
- Structures: `ST_` prefix e.g. `ST_MotorConfig`
- Methods: PascalCase, no prefix e.g. `Execute`, `Reset`, `GetStatus`
- Variables: camelCase e.g. `bEnable`, `nPosition`, `sNetId`
- Type prefixes: `b` BOOL, `n` INT/UINT, `f` REAL/LREAL, `s` STRING,
  `e` ENUM, `st` STRUCT, `a` ARRAY, `p` POINTER, `i` interface

### Error Handling
Use the standard TwinCAT pattern — never leave errors unhandled:
```pascal
IF fbSomeOperation.bError THEN
    eState := E_State.Error;
    nErrorId := fbSomeOperation.nErrorId;
END_IF
```

---

## Writing to the Project

Writes go through the `ProjectWriter` port → `automation_writer` adapter →
Windows bridge → PowerShell → TcXaeShell.DTE.17.0 COM.

The automation interface handles:
- GUID assignment for new POUs/methods (you never generate GUIDs)
- .plcproj cross-reference updates
- Internal tree indexing

**Never** manipulate .TcPOU XML or .plcproj files directly for structural changes.
Direct XML editing is only acceptable for editing ST code inside existing CDATA
sections when the automation interface is unavailable.

---

## Rename Operations

The automation interface does NOT expose a rename/refactor API.
Cross-project variable renames require a manual find-and-replace across `.TcPOU` files.

**Always flag rename operations for human review before executing.**
Add a comment in your response like:
> "I'm about to rename `nPosition` across the project. I found N references.
> Please review before I proceed."

Never execute a cross-project rename autonomously.

---

## Build Errors

Build errors come back as structured JSON:
```json
{
  "success": false,
  "errors": [
    {
      "file": "FB_MotorControl.TcPOU",
      "line": 42,
      "message": "Identifier 'nSpeed' not declared",
      "severity": "error"
    }
  ]
}
```

Fix errors one file at a time. After fixing, rebuild before moving on.
If the same error persists after two fix attempts, stop and explain the
situation to the user rather than continuing to guess.

---

## Test Loop Behaviour

When running tests autonomously:
1. Write code
2. Build — fix any errors before proceeding
3. Deploy to target
4. Run TcUnit tests
5. Read results JSON
6. If failures — read the specific failing test, understand the assertion, fix code
7. Repeat from step 2

**Max autonomous iterations: 5**
If tests are still failing after 5 iterations, stop and present:
- What you tried
- The current failing tests with their messages
- Your hypothesis about what's wrong
- Ask the user how to proceed

Never deploy to a target without a successful build first.

---

## Safety-Critical Code

If any POU, method, or variable name suggests safety-critical functionality
(Safety, SIL, TÜV, Emergency, EStop, SafetyDoor, etc.), **always** flag it
for human review before making any changes:

> "This appears to involve safety-critical code. I will not modify it
> autonomously. Please review my proposed changes before I proceed."

This applies even if the change seems trivial.

---

## Running the Project Locally

```bash
# Start MCP server
docker compose -f docker/docker-compose.yml up

# Run tests
docker compose -f docker/docker-compose.yml run tckit pytest tests/

# Lint (includes adapter isolation check)
docker compose -f docker/docker-compose.yml run tckit ruff check tckit/
```

Windows bridge (run natively on Windows PC):
```powershell
.\bridge\Start-Bridge.ps1
```

---

## Config & Secrets

- `config.json` — committed, no secrets, adapter names and non-sensitive settings
- `.env` — gitignored, machine-specific values (paths, IPs, AMS IDs)
- `.env.example` — committed template

Never put secrets, AMS IDs, or file paths in `config.json`.
Never commit any `.env` file.

---

## Adding a New Adapter

1. Create the file in the correct `adapters/` subdirectory
2. Import only from `tckit.ports` and stdlib
3. Implement all abstract methods from the port
4. Register it in `tckit/config.py` adapter registry
5. Add the config name to `config.example.json`
6. Write unit tests in `tests/unit/`
7. Document it in `docs/content/adapters/`

---

## Adding a New Port

Only do this if there is a genuinely new external concern — not a variation
of an existing one. Discuss with the user before adding ports.

1. Define the abstract base class in `tckit/ports/`
2. Keep method signatures minimal — only what adapters need to implement
3. Return types should be dataclasses defined in `tckit/ports/types.py`
4. Update `tckit/server.py` to expose the new port as MCP tools
5. Implement at least one adapter before merging

---

## What NOT to Do

- Do not import one adapter from another
- Do not hardcode file paths, AMS IDs, or COM version strings
- Do not fetch full POUs when you need one method
- Do not skip `find_fb()` when using an unfamiliar Beckhoff FB
- Do not make structural project changes via direct XML editing
- Do not execute cross-project renames autonomously
- Do not modify safety-critical code without human review
- Do not deploy without a successful build
- Do not continue looping after 5 failed test iterations
- Do not put secrets in config.json or commit .env files
- Do not write code comments without RST format

---

## Quick Reference — Port Methods

| Port | Key Methods |
|------|------------|
| ProjectReader | `get_structure()`, `get_pou_interface()`, `get_pou_item()`, `get_gvl()` |
| ProjectWriter | `open_project()`, `add_pou()`, `add_method()`, `update_pou_item()` |
| BuildRunner | `build()`, `deploy()`, `start_runtime()`, `get_status()` |
| TestRunner | `run_tests()`, `wait_complete()`, `get_results()`, `get_status()` |
| DocGenerator | `generate()`, `get_status()` |
| DocsSearcher | `find_fb()`, `find_library()`, `search()`, `get_page()` |
