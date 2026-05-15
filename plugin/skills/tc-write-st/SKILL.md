---
name: tc-write-st
description: Use when writing or modifying Structured Text (ST) code in a TwinCAT 3 project. Triggers on requests like "add a method to FB_Motor", "create FB_PumpControl", "tweak the Execute body", "rename X to Y inside Execute", "add a VAR_INPUT to FB_PumpControl", "change one line in this method", or any other ST edit. Uses TcKit's writer MCP tools (open_project, create_project, add_pou, add_gvl, add_method, update_pou_declaration, update_pou_implementation, update_method_body, update_pou_declaration_patch, update_pou_implementation_patch, update_method_body_patch, add_variable). Use these tools INSTEAD of Edit/Write on .TcPOU or .plcproj files; the MCP tools route through XAE so GUIDs and project cross-references stay consistent. Enforces comment style, naming conventions, the bError propagation pattern, the rename guard, and the safety-critical naming guard. If the code uses an unfamiliar Beckhoff library FB, hand off to tc-beckhoff-docs first. Do NOT use for read-only inspection or for build/test orchestration.
allowed-tools: mcp__tckit__open_project, mcp__tckit__create_project, mcp__tckit__add_pou, mcp__tckit__add_gvl, mcp__tckit__add_method, mcp__tckit__update_pou_declaration, mcp__tckit__update_pou_implementation, mcp__tckit__update_method_body, mcp__tckit__update_pou_declaration_patch, mcp__tckit__update_pou_implementation_patch, mcp__tckit__update_method_body_patch, mcp__tckit__add_variable, mcp__tckit__get_pou_interface, mcp__tckit__get_pou_declaration, mcp__tckit__get_pou_item, Read
---

# Writing ST through TcKit

Follow this procedure every time you produce ST that will be written to the project.

## Tool selection — read this before calling anything

These TcKit writer tools route through the XAE automation interface, which keeps GUIDs and project cross-references consistent. Use them in place of `Edit`/`Write` on `.TcPOU` or `.plcproj` files.

| Request                                                                    | Tool                                                          |
| -------------------------------------------------------------------------- | ------------------------------------------------------------- |
| Tweak one line in a method / action / property body                        | `update_method_body_patch(pou, method, old, new)`             |
| Tweak one line in the POU's own declaration block (FB-level `VAR`)         | `update_pou_declaration_patch(pou, old, new)`                 |
| Tweak one line in the POU's own cyclic body (FB / PRG implementation)      | `update_pou_implementation_patch(pou, old, new)`              |
| Add one variable to a `VAR_INPUT` / `VAR_OUTPUT` / `VAR` etc. scope        | `add_variable(pou, scope, declaration, item?)`                |
| Rewrite a method / action / property body                                  | `update_method_body(pou, method, code)`                       |
| Rewrite the POU's own declaration block                                    | `update_pou_declaration(pou, code)`                           |
| Rewrite the POU's own cyclic body (FB / PRG implementation)                | `update_pou_implementation(pou, code)`                        |
| Add a brand-new method to an existing POU                                  | `add_method(pou, method_name, code)`                          |
| Add a brand-new POU (FB / function / program / interface)                  | `add_pou(name, pou_type, code)`                               |
| Add a brand-new GVL (`VAR_GLOBAL` declarations)                            | `add_gvl(name, code)`                                         |
| Create a brand-new PLC project                                             | `create_project(name, path)`                                  |
| Open / re-open a TwinCAT solution in XAE                                   | `open_project(solution_path)`                                 |
| Read the current item body before deciding on a patch anchor               | `get_pou_item(pou, item)` (reader)                            |
| Read just the FB-level `VAR` block before `add_variable`                   | `get_pou_declaration(pou)` (reader)                           |

**FB-level vs method-level.** A POU has its own declaration block and (for FBs / programs) its own cyclic body, plus zero or more methods / actions / properties hanging underneath. The three `update_pou_*` calls target the POU itself; the three `update_method_body*` calls target a named child item. Pick the level that matches the change — patching a method body via `update_pou_implementation_patch` will not find the anchor.

**Patch primitive semantics.** All three `_patch` calls mirror Claude Code's own `Edit` tool: each replaces exactly one occurrence of `old_string` with `new_string` on the targeted block. They fail if the anchor is missing or appears more than once. If the call fails on non-uniqueness, extend the anchor with more surrounding context and retry. Patches are the right tool for a one-line tweak; do NOT rewrite the whole body for a small change.

**`add_variable` semantics.** Inserts the declaration line before the matching scope's `END_VAR`. If the scope block does not exist on the target item, a new one is created at the conventional position (order: `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR`, `VAR CONSTANT`, `VAR_PERSISTENT`, `VAR_TEMP`). Use this instead of reading the full declaration, hand-editing the VAR block, and writing it back.

**Bridge requirement.** Writer tools call out to the Windows bridge service, which talks to XAE over COM. They will not work if the bridge is down. Reader tools (used for planning the edit) read XML from disk and have no such requirement. There is no fallback for the writer side; do not work around bridge unavailability by editing `.TcPOU` / `.plcproj` directly.

## Pre-write checks (in order)

1. **Safety-name guard.** If any name in the change touches `Safety`, `SIL`, `TÜV`/`TUV`, `Emergency`, `EStop`, `SafetyDoor`, or anything else that suggests safety-critical functionality, STOP. Show the user the proposed change, explain that it appears safety-critical, and wait for explicit approval before any writer call. This applies even if the change seems trivial.
2. **Rename guard.** TcKit's automation interface has no rename API. If the change involves renaming a symbol that exists elsewhere in the project, STOP. Report how many references you found and ask the user to approve before any manual find-and-replace. Never execute a cross-project rename autonomously.
3. **Unfamiliar Beckhoff FB.** If the new code instantiates a Beckhoff library FB you have not just researched via `tc-beckhoff-docs`, hand off to `tc-beckhoff-docs` now. Do not write code against a Beckhoff FB whose inputs/outputs/timing you only know from memory.

## Style

- **Project conventions.** Follow the conventions in the project's `CLAUDE.md` if it specifies any (naming, error pattern, public/private boundaries, etc.). Where the project does not specify, match the existing style of the file you are editing. The skill does not impose a default naming or error-handling convention.
- **Comments.** The doc generator detects both RST line comments (`// :Description:`, `// :param x:`, `// :returns:`) and Beckhoff XML (`(*~ <docu> ~*)`). Match the file's existing style.

## Write procedure

1. If the user has named a specific FB and the change is a clear add (one variable, one method, or one patch with the anchor already stated), call the writer directly. The writer fails cleanly if the target FB is missing, so a defensive `get_pou_interface` "to confirm it exists" is wasted. Only read first when you actually need the existing shape, e.g. to choose a patch anchor or check a signature.
2. **Pick the smallest write that does the job** using the Tool selection table above. Small edit on a method -> `update_method_body_patch`. Small edit on the FB-level decl / cyclic body -> `update_pou_declaration_patch` / `update_pou_implementation_patch`. Single new variable -> `add_variable`. Full method-body rewrite -> `update_method_body`. Full POU declaration / implementation rewrite -> `update_pou_declaration` / `update_pou_implementation`. New unit -> `add_pou` / `add_method` / `create_project`.
3. For patch-based edits, fetch the current item with `get_pou_item` (or `get_pou_declaration` if only the FB-level VAR block matters) so the anchor you choose is grounded in the real text, not your memory of it.
4. NEVER edit `.TcPOU` or `.plcproj` XML directly. GUIDs and cross-references go through the automation interface.
5. After the writer returns success, summarise what changed (POU, item, lines). The writer's success response is the confirmation; do not read the change back to "verify" it landed and do not call `build` to "check it builds". The operator and harness verify the artefact. Re-read or rebuild only if the user explicitly asks you to check something specific.

## Anti-patterns

- Reading a `.TcPOU` file with `Read` and then editing it with `Edit`. The MCP writer tools exist to keep GUIDs and project cross-references consistent; bypassing them silently breaks the project.
- Greping for a method name in the raw XML and trying to patch the XML around it.
- Opening `.plcproj` with `Read` or `Edit` to add or remove `<Compile Include="..."/>` entries. `add_pou` does this through XAE.
- Calling a full-body rewrite (`update_method_body` / `update_pou_declaration` / `update_pou_implementation`) for a one-line change. Use the matching `_patch` variant instead.
- Reading the full FB declaration, hand-editing the VAR block, and writing it back. Use `add_variable` instead.
- Concluding "TcKit isn't working" because a reader tool succeeded but a writer tool failed. Writer tools require the bridge; reader tools do not. Surface the bridge error to the user rather than reaching for stock-tool edits.
- Re-reading the changed item with `get_pou_item` / `get_pou_declaration` / `Read` immediately after a successful writer call to confirm it landed. The writer already told you it succeeded. Self-verification reads (and verification builds) are bench-noise; the operator and harness verify the artefact.

## Next

After successful writes, hand off to `tc-build-test-loop` for build → deploy → test.
