# ProjectWriter

**File:** `tckit/ports/writer.py`
**Purpose:** Structural writes that go through the IDE's authoring interface, not raw file edits.

| Method | Returns |
|--------|---------|
| `open_project(solution_path)` | `Result` |
| `create_project(name, path)` | `Result` |
| `add_plc_project(sln_path, plc_name, *, project_type='standard')` | `Result` |
| `save_plc_as_library(plc_name, output_path, *, install=True, repository='System', overwrite=False)` | `Result` |
| `add_library_reference(consumer_plc_name, library_name, *, version='*', distributor='Tc3 Project')` | `Result` |
| `add_library_placeholder(consumer_plc_name, placeholder_name, default_library, *, version='*', distributor='', parameters=None)` | `Result` |
| `add_pou(name, pou_type, code, *, plc_name=None)` | `Result` |
| `add_gvl(name, code, *, plc_name=None)` | `Result` |
| `add_dut(name, code, *, dut_kind=DUTKind.STRUCT, plc_name=None)` | `Result` |
| `add_method(pou_name, method_name, code, *, plc_name=None)` | `Result` |
| `add_property(pou_name, property_name, return_type, *, getter_code=None, setter_code=None, plc_name=None)` | `Result` |
| `update_pou_declaration(pou_name, code, *, plc_name=None)` | `Result` |
| `update_pou_implementation(pou_name, code, *, plc_name=None)` | `Result` |
| `update_method_body(pou_name, method_name, code, *, plc_name=None)` | `Result` |
| `update_pou_declaration_patch(pou_name, old, new, *, plc_name=None)` | `Result` |
| `update_pou_implementation_patch(pou_name, old, new, *, plc_name=None)` | `Result` |
| `update_method_body_patch(pou_name, method_name, old, new, *, plc_name=None)` | `Result` |
| `add_variable(pou_name, scope, declaration, item_name=None, *, plc_name=None)` | `Result` |

## POU updates

A POU has its own declaration block and (for FBs / programs) its own
cyclic body, plus zero or more methods / actions / properties hanging
underneath. The three `update_pou_*` calls target the POU itself; the
three `update_method_body*` calls target a named child item.
The patch variants mirror Claude Code's `Edit` semantics — exactly one
unique anchor, fail otherwise.

## Multi-project solutions

PLC-scoped writes take an optional `plc_name`. The bridge already enforces
the same fallback policy on the PowerShell side (`Resolve-TcPlcName`):
per-call name → `PLC_PROJECT_NAME` env var → auto-resolve on a
single-project sln → throw with the candidate list. `open_project` and
`create_project` stay solution-scoped.

To author a multi-PLC sln from scratch (e.g. a Library + Tests split), use
`create_project` for the sln + first PLC project, then `add_plc_project` for
each additional PLC. `save_plc_as_library` and `add_library_reference` author
a compiled library reference between in-sln PLC projects. See
[multi-plc](multi-plc.md) for the full workflow.

## Why this shape

A TwinCAT project is more than a folder of `.TcPOU` files — there are GUIDs, cross-references in `.plcproj`, and tree indexes that have to stay consistent. If the agent edits XML directly it ends up reasoning over two parallel realities: the files on disk, and what the IDE thinks the project is. ProjectWriter routes every structural change through the IDE so there is **one source of truth** and the agent never has to invent GUIDs or reconcile drift.
