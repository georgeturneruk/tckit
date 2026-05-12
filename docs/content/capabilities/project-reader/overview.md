# ProjectReader

**File:** `tckit/ports/reader.py`
**Purpose:** Read-only access to TwinCAT project structure and code.

Three precision levels — never fetch more than the task needs.

```
get_structure()      → names and types only, no code
get_pou_interface()  → declarations + method signatures, no bodies
get_pou_item()       → single method/action/property body only
```

| Method | Returns |
|--------|---------|
| `get_structure(project_path, *, plc_name=None)` | `ProjectStructure` |
| `get_pou_interface(pou_name, *, plc_name=None)` | `POUInterface` |
| `get_pou_item(pou_name, item_name, *, plc_name=None)` | `POUItem` |
| `get_gvl(gvl_name, *, plc_name=None)` | `GVL` |
| `get_dut(dut_name, *, plc_name=None)` | `DUT` |

`get_pou_item` accepts dotted property accessor syntax: `"Status.Get"` / `"Status.Set"`.

## Multi-project solutions

A TwinCAT `.sln` can reference more than one `.plcproj` — e.g. a library
PLC project plus a TcUnit test PLC project. `ProjectStructure.plcs` keys
the result by PLC-project name (the `.plcproj` filename stem); single-project
solutions return a one-entry dict. Each `PLCSection` carries that PLC
project's `pous` / `gvls` / `duts` / `libraries`; tasks live at the
solution level.

Per-symbol methods take an optional `plc_name`. The fallback chain (see
ADR-0005):

1. explicit `plc_name`,
2. `PLC_PROJECT_NAME` env var,
3. unique-symbol auto-resolve (the symbol exists in exactly one PLC project),
4. ambiguous → raise with the names of the PLC projects that contain the symbol.

```python
reader.get_pou_interface("FB_Filter", plc_name="Library")
reader.get_dut("E_State")  # raises if E_State exists in multiple PLCs
```

## Why this shape

Pasting an entire POU file into context to ask about one method is the cheapest way to burn an agent's working memory. Layering reads from structure → interface → single item lets the model pull only the tokens it actually needs, which is the core of [just-in-time context retrieval](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents).
