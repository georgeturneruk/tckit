# ProjectWriter

**File:** `tckit/ports/writer.py`
**Purpose:** Structural writes that go through the IDE's authoring interface, not raw file edits.

| Method | Returns |
|--------|---------|
| `open_project(solution_path)` | `Result` |
| `create_project(name, path)` | `Result` |
| `add_pou(name, pou_type, code, *, plc_name=None)` | `Result` |
| `add_method(pou_name, method_name, code, *, plc_name=None)` | `Result` |
| `update_pou_item(pou_name, item_name, code, *, plc_name=None)` | `Result` |
| `update_pou_item_patch(pou_name, item_name, old, new, *, plc_name=None)` | `Result` |
| `add_variable(pou_name, scope, declaration, item_name=None, *, plc_name=None)` | `Result` |

## Multi-project solutions

PLC-scoped writes take an optional `plc_name`. The bridge already enforces
the same fallback policy on the PowerShell side (`Resolve-TcPlcName`):
per-call name → `PLC_PROJECT_NAME` env var → auto-resolve on a
single-project sln → throw with the candidate list. `open_project` and
`create_project` stay solution-scoped. See ADR-0005.

## Why this shape

A TwinCAT project is more than a folder of `.TcPOU` files — there are GUIDs, cross-references in `.plcproj`, and tree indexes that have to stay consistent. If the agent edits XML directly it ends up reasoning over two parallel realities: the files on disk, and what the IDE thinks the project is. ProjectWriter routes every structural change through the IDE so there is **one source of truth** and the agent never has to invent GUIDs or reconcile drift.
