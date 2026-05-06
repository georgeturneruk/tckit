# ProjectWriter

**File:** `tckit/ports/writer.py`
**Purpose:** Structural writes that go through the IDE's authoring interface, not raw file edits.

| Method | Returns |
|--------|---------|
| `open_project(solution_path)` | `Result` |
| `create_project(name, path)` | `Result` |
| `add_pou(name, pou_type, code)` | `Result` |
| `add_method(pou_name, method_name, code)` | `Result` |
| `update_pou_item(pou_name, item_name, code)` | `Result` |

## Why this shape

A TwinCAT project is more than a folder of `.TcPOU` files — there are GUIDs, cross-references in `.plcproj`, and tree indexes that have to stay consistent. If the agent edits XML directly it ends up reasoning over two parallel realities: the files on disk, and what the IDE thinks the project is. ProjectWriter routes every structural change through the IDE so there is **one source of truth** and the agent never has to invent GUIDs or reconcile drift.
