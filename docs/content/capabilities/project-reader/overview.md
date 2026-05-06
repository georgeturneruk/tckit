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
| `get_structure(project_path)` | `ProjectStructure` |
| `get_pou_interface(pou_name)` | `POUInterface` |
| `get_pou_item(pou_name, item_name)` | `POUItem` |
| `get_gvl(gvl_name)` | `GVL` |
| `get_dut(dut_name)` | `DUT` |

`get_pou_item` accepts dotted property accessor syntax: `"Status.Get"` / `"Status.Set"`.

## Why this shape

Pasting an entire POU file into context to ask about one method is the cheapest way to burn an agent's working memory. Layering reads from structure → interface → single item lets the model pull only the tokens it actually needs, which is the core of [just-in-time context retrieval](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents).
