---
name: tc-write-st
description: Use when writing or modifying Structured Text (ST) code in a TwinCAT 3 project. Triggers on requests like "add a method to FB_Motor", "create FB_PumpControl", "update the Execute body", "add a new GVL". Uses TcKit's writer MCP tools (open_project, create_project, add_pou, add_method, update_pou_item). Enforces comment style, naming conventions, the bError propagation pattern, the rename guard, and the safety-critical naming guard. If the code uses an unfamiliar Beckhoff library FB, hand off to tc-beckhoff-docs first. Do NOT use for read-only inspection or for build/test orchestration.
allowed-tools: mcp__tckit__open_project, mcp__tckit__create_project, mcp__tckit__add_pou, mcp__tckit__add_method, mcp__tckit__update_pou_item, mcp__tckit__get_pou_interface, mcp__tckit__get_pou_item, Read
---

# Writing ST through TcKit

Follow this procedure every time you produce ST that will be written to the project.

## Pre-write checks (in order)

1. **Safety-name guard.** If any name in the change touches `Safety`, `SIL`, `TÜV`/`TUV`, `Emergency`, `EStop`, `SafetyDoor`, or anything else that suggests safety-critical functionality, STOP. Show the user the proposed change, explain that it appears safety-critical, and wait for explicit approval before any writer call. This applies even if the change seems trivial.
2. **Rename guard.** TcKit's automation interface has no rename API. If the change involves renaming a symbol that exists elsewhere in the project, STOP. Report how many references you found and ask the user to approve before any manual find-and-replace. Never execute a cross-project rename autonomously.
3. **Unfamiliar Beckhoff FB.** If the new code instantiates a Beckhoff library FB you have not just researched via `tc-beckhoff-docs`, hand off to `tc-beckhoff-docs` now. Do not write code against a Beckhoff FB whose inputs/outputs/timing you only know from memory.

## Style rules

- **Comments.** Prefer RST line comments (`// :Description:`, `// :param x:`, `// :returns:`) for new code. Beckhoff XML `<docu>` is also accepted — the doc generator auto-detects both. Match the style already present in the file you are editing. See `examples/fb_template.st` for the RST line layout.
- **Naming.**
  - `FB_` function blocks, `PRG_` programs, `GVL_` globals, `E_` enums, `ST_` structs, `I_` interfaces.
  - Methods: PascalCase, no prefix.
  - Variables: camelCase with type prefix — `b` BOOL, `n` INT/UINT, `f` REAL/LREAL, `s` STRING, `e` ENUM, `st` STRUCT, `a` ARRAY, `p` POINTER, `i` interface.
- **Error propagation.** Always check `.bError` and surface `.nErrorId`:
  ```pascal
  IF fbOp.bError THEN
      eState := E_State.Error;
      nErrorId := fbOp.nErrorId;
  END_IF
  ```

## Write procedure

1. Confirm the target POU exists via `get_pou_interface` if you haven't already (skip for greenfield).
2. New POU: `add_pou(name, pou_type, code)`. New method on an existing POU: `add_method(pou_name, method_name, code)`. Edit to an existing method body: `update_pou_item(pou_name, item_name, code)`.
3. NEVER edit `.TcPOU` or `.plcproj` XML directly for structural changes. GUID assignment and cross-references go through the automation interface. Direct XML editing is only acceptable for ST inside an existing CDATA when the automation interface is unavailable.
4. After the writer returns success, summarise what changed (POU, item, lines). Do not assume it builds.

## Next

After successful writes, hand off to `tc-build-test-loop` for build → deploy → test.
