---
name: tc-write-st
description: Use when writing or modifying Structured Text (ST) code in a TwinCAT 3 project. Triggers on requests like "add a method to FB_Motor", "create FB_PumpControl", "tweak the Execute body", "rename X to Y inside Execute", "add a VAR_INPUT to FB_PumpControl", "change one line in this method", or any other ST edit. Uses TcKit's writer MCP tools (open_project, create_project, add_pou, add_method, update_pou_item, update_pou_item_patch, add_variable). Use these tools INSTEAD of Edit/Write on .TcPOU or .plcproj files; the MCP tools route through XAE so GUIDs and project cross-references stay consistent. Enforces comment style, naming conventions, the bError propagation pattern, the rename guard, and the safety-critical naming guard. If the code uses an unfamiliar Beckhoff library FB, hand off to tc-beckhoff-docs first. Do NOT use for read-only inspection or for build/test orchestration.
allowed-tools: mcp__tckit__open_project, mcp__tckit__create_project, mcp__tckit__add_pou, mcp__tckit__add_method, mcp__tckit__update_pou_item, mcp__tckit__update_pou_item_patch, mcp__tckit__add_variable, mcp__tckit__get_pou_interface, mcp__tckit__get_pou_declaration, mcp__tckit__get_pou_item, Read
---

# Writing ST through TcKit

Follow this procedure every time you produce ST that will be written to the project.

## Tool selection — read this before calling anything

These TcKit writer tools route through the XAE automation interface, which keeps GUIDs and project cross-references consistent. Use them in place of `Edit`/`Write` on `.TcPOU` or `.plcproj` files.

| Request                                                                    | Tool                                                |
| -------------------------------------------------------------------------- | --------------------------------------------------- |
| Tweak one line / small block in an existing method or FB body              | `update_pou_item_patch(pou, item, old, new)`        |
| Add one variable to a `VAR_INPUT` / `VAR_OUTPUT` / `VAR` etc. scope        | `add_variable(pou, scope, declaration, item?)`      |
| Rewrite an entire method/action/property body                              | `update_pou_item(pou, item, code)`                  |
| Add a brand-new method to an existing POU                                  | `add_method(pou, method_name, code)`                |
| Add a brand-new POU                                                        | `add_pou(name, pou_type, code)`                     |
| Create a brand-new PLC project                                             | `create_project(name, path)`                        |
| Open / re-open a TwinCAT solution in XAE                                   | `open_project(solution_path)`                       |
| Read the current item body before deciding on a patch anchor               | `get_pou_item(pou, item)` (reader)                  |
| Read just the FB-level `VAR` block before `add_variable`                   | `get_pou_declaration(pou)` (reader)                 |

**Patch primitive semantics.** `update_pou_item_patch` mirrors Claude Code's own `Edit` tool: it replaces exactly one occurrence of `old_string` with `new_string`. It fails if the anchor is missing or appears more than once. If the call fails on non-uniqueness, extend the anchor with more surrounding context and retry. This is the right tool for a one-line tweak; do NOT rewrite the whole method body for a small change.

**`add_variable` semantics.** Inserts the declaration line before the matching scope's `END_VAR`. If the scope block does not exist on the target item, a new one is created. Use this instead of reading the full declaration, hand-editing the VAR block, and writing it back.

**Bridge requirement.** Writer tools call out to the Windows bridge service, which talks to XAE over COM. They will not work if the bridge is down. Reader tools (used for planning the edit) read XML from disk and have no such requirement. There is no fallback for the writer side; do not work around bridge unavailability by editing `.TcPOU` / `.plcproj` directly.

## Pre-write checks (in order)

1. **Safety-name guard.** If any name in the change touches `Safety`, `SIL`, `TÜV`/`TUV`, `Emergency`, `EStop`, `SafetyDoor`, or anything else that suggests safety-critical functionality, STOP. Show the user the proposed change, explain that it appears safety-critical, and wait for explicit approval before any writer call. This applies even if the change seems trivial.
2. **Rename guard.** TcKit's automation interface has no rename API. If the change involves renaming a symbol that exists elsewhere in the project, STOP. Report how many references you found and ask the user to approve before any manual find-and-replace. Never execute a cross-project rename autonomously.
3. **Unfamiliar Beckhoff FB.** If the new code instantiates a Beckhoff library FB you have not just researched via `tc-beckhoff-docs`, hand off to `tc-beckhoff-docs` now. Do not write code against a Beckhoff FB whose inputs/outputs/timing you only know from memory.

## Style rules

- **Comments.** Prefer RST line comments (`// :Description:`, `// :param x:`, `// :returns:`) for new code. Beckhoff XML `<docu>` is also accepted; the doc generator auto-detects both. Match the style already present in the file you are editing. See `examples/fb_template.st` for the RST line layout.
- **Naming.**
  - `FB_` function blocks, `PRG_` programs, `GVL_` globals, `E_` enums, `ST_` structs, `I_` interfaces.
  - Methods: PascalCase, no prefix.
  - Variables: camelCase with type prefix; `b` BOOL, `n` INT/UINT, `f` REAL/LREAL, `s` STRING, `e` ENUM, `st` STRUCT, `a` ARRAY, `p` POINTER, `i` interface.
- **Error propagation.** Always check `.bError` and surface `.nErrorId`:
  ```pascal
  IF fbOp.bError THEN
      eState := E_State.Error;
      nErrorId := fbOp.nErrorId;
  END_IF
  ```

## Write procedure

1. Confirm the target POU exists via `get_pou_interface` if you haven't already (skip for greenfield).
2. **Pick the smallest write that does the job** using the Tool selection table above. Small edit -> `update_pou_item_patch`. Single new variable -> `add_variable`. Full body rewrite -> `update_pou_item`. New unit -> `add_pou` / `add_method` / `create_project`.
3. For patch-based edits, fetch the current item with `get_pou_item` (or `get_pou_declaration` if only the FB-level VAR block matters) so the anchor you choose is grounded in the real text, not your memory of it.
4. NEVER edit `.TcPOU` or `.plcproj` XML directly. GUIDs and cross-references go through the automation interface.
5. After the writer returns success, summarise what changed (POU, item, lines). Do not assume it builds.

## Anti-patterns

- Reading a `.TcPOU` file with `Read` and then editing it with `Edit`. The MCP writer tools exist to keep GUIDs and project cross-references consistent; bypassing them silently breaks the project.
- Greping for a method name in the raw XML and trying to patch the XML around it.
- Opening `.plcproj` with `Read` or `Edit` to add or remove `<Compile Include="..."/>` entries. `add_pou` does this through XAE.
- Calling `update_pou_item` (full-body rewrite) for a one-line change. Use `update_pou_item_patch` instead.
- Reading the full FB declaration, hand-editing the VAR block, and writing it back. Use `add_variable` instead.
- Concluding "TcKit isn't working" because a reader tool succeeded but a writer tool failed. Writer tools require the bridge; reader tools do not. Surface the bridge error to the user rather than reaching for stock-tool edits.

## Next

After successful writes, hand off to `tc-build-test-loop` for build → deploy → test.
