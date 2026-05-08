---
name: tc-read-project
description: Use when inspecting, navigating, mapping, or searching a TwinCAT 3 PLC project through TcKit's MCP tools (get_structure, get_pou_interface, get_pou_item, get_gvl, get_dut). Triggers on requests like "show me FB_X", "what's in this project", "summarise the motor controller POU", "what does ST_Config look like", or any task that requires reading PLC code before writing it. Do NOT use for writing, building, or testing, and do NOT use for researching Beckhoff library FBs (use tc-beckhoff-docs for that).
allowed-tools: mcp__tckit__get_structure, mcp__tckit__get_pou_interface, mcp__tckit__get_pou_item, mcp__tckit__get_gvl, mcp__tckit__get_dut, Read, Grep, Glob
---

# Reading TwinCAT projects through TcKit

Always read in layers. Never fetch a full POU when one method suffices.

## Procedure

1. **Map first, only if needed.** If you don't already know the POU/GVL/DUT name, call `get_structure(project_path)`. Skip if the user named the symbol.
2. **Interface before body.** For any POU you'll touch, call `get_pou_interface(pou_name)` to get declarations and method signatures. Do NOT fetch bodies yet.
3. **Single-item bodies.** For each method, action, or property whose logic you actually need, call `get_pou_item(pou_name, item_name)`. One call per item. Stop fetching as soon as you have what you need.
4. **GVLs and DUTs separately.**
   - `get_gvl(gvl_name)` for global variable lists.
   - `get_dut(dut_name)` for structs, enums, unions, and aliases. Do NOT try to read these via `get_pou_item` — they are not POUs.
5. **Don't refresh the map.** `get_structure` is a one-shot orientation, not a per-turn refresh.
6. **Report with citations.** When summarising, name the POU and item you actually read so the user can verify. Don't paraphrase code you didn't fetch.

## Anti-patterns

- Fetching `get_pou_item` for every method "just in case".
- Calling `get_structure` at the start of every turn.
- Quoting Beckhoff FB behaviour from memory — that's `tc-beckhoff-docs` territory.
- Using `get_pou_item` for an enum or struct (use `get_dut`).

## Next

If the task moves into writing or modifying code, hand off to `tc-write-st`. If a Beckhoff library FB needs research, hand off to `tc-beckhoff-docs`.
