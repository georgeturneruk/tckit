---
name: tc-write-st
description: Use when writing or modifying Structured Text (ST) code in a TwinCAT 3 project. Triggers on requests like "add a method to FB_Motor", "add a property to FB_Pid", "create FB_PumpControl", "add ST_Config struct", "add E_State enum", "tweak the Execute body", "rename X to Y inside Execute", "add a VAR_INPUT to FB_PumpControl", "change one line in this method", or any other ST edit. Uses TcKit's writer MCP tools (OpenProject, CreateProject, AddPou, AddGvl, AddDut, AddMethod, AddProperty, UpdatePouDeclaration, UpdatePouImplementation, UpdateMethodBody, UpdatePouDeclarationPatch, UpdatePouImplementationPatch, UpdateMethodBodyPatch, AddVariable). Use these tools INSTEAD of Edit/Write on .TcPOU or .plcproj files; the MCP tools go through TcKit's writer backend so GUIDs and project cross-references stay consistent. Enforces comment style, naming conventions, the bError propagation pattern, the rename guard, and the safety-critical naming guard. If the code uses an unfamiliar Beckhoff library FB, hand off to tc-beckhoff-docs first. Do NOT use for read-only inspection or for build/test orchestration.
allowed-tools: mcp__tckit__OpenProject, mcp__tckit__CreateProject, mcp__tckit__AddPou, mcp__tckit__AddGvl, mcp__tckit__AddDut, mcp__tckit__AddMethod, mcp__tckit__AddProperty, mcp__tckit__UpdatePouDeclaration, mcp__tckit__UpdatePouImplementation, mcp__tckit__UpdateMethodBody, mcp__tckit__UpdatePouDeclarationPatch, mcp__tckit__UpdatePouImplementationPatch, mcp__tckit__UpdateMethodBodyPatch, mcp__tckit__AddVariable, mcp__tckit__GetPouInterface, mcp__tckit__GetPouDeclaration, mcp__tckit__GetPouItem, mcp__tckit__AnalyseProject, Read
---

# Writing ST through TcKit

Follow this procedure every time you produce ST that will be written to the project.

## Tool selection — read this before calling anything

These TcKit writer tools go through the XAE Automation Interface, which keeps GUIDs and project cross-references consistent. Use them in place of `Edit`/`Write` on `.TcPOU` or `.plcproj` files.

| Request                                                                    | Tool                                                          |
| -------------------------------------------------------------------------- | ------------------------------------------------------------- |
| Tweak one line in a method / action / property body                        | `UpdateMethodBodyPatch(pou, method, old, new)`             |
| Tweak one line in the POU's own declaration block (FB-level `VAR`)         | `UpdatePouDeclarationPatch(pou, old, new)`                 |
| Tweak one line in the POU's own cyclic body (FB / PRG implementation)      | `UpdatePouImplementationPatch(pou, old, new)`              |
| Add one variable to a `VAR_INPUT` / `VAR_OUTPUT` / `VAR` etc. scope        | `AddVariable(pou, scope, declaration, item?)`                |
| Rewrite a method / action / property body                                  | `UpdateMethodBody(pou, method, code)`                       |
| Rewrite the POU's own declaration block                                    | `UpdatePouDeclaration(pou, code)`                           |
| Rewrite the POU's own cyclic body (FB / PRG implementation)                | `UpdatePouImplementation(pou, code)`                        |
| Add a brand-new method to an existing POU                                  | `AddMethod(pou, method_name, code)`                          |
| Add a brand-new property to an existing POU (Get, Set, or both)            | `AddProperty(pou, name, return_type, getter_code?, setter_code?)` |
| Add a brand-new POU (FB / function / program / interface)                  | `AddPou(name, pou_type, code)`                               |
| Add a brand-new GVL (`VAR_GLOBAL` declarations)                            | `AddGvl(name, code)`                                         |
| Add a brand-new DUT (struct, enum, or union)                               | `AddDut(name, code, dut_kind="struct"|"enum"|"union")`       |
| Create a brand-new PLC project                                             | `CreateProject(name, path)`                                  |
| Open / re-open a TwinCAT solution in XAE                                   | `OpenProject(solution_path)`                                 |
| Read the current item body before deciding on a patch anchor               | `GetPouItem(pou, item)` (reader)                            |
| Read just the FB-level `VAR` block before `AddVariable`                   | `GetPouDeclaration(pou)` (reader)                           |

**FB-level vs method-level.** A POU has its own declaration block and (for FBs / programs) its own cyclic body, plus zero or more methods / actions / properties hanging underneath. The three `UpdatePou*` calls target the POU itself; the three `UpdateMethodBody*` calls target a named child item. Pick the level that matches the change — patching a method body via `UpdatePouImplementationPatch` will not find the anchor.

**Patch primitive semantics.** All three `_patch` calls mirror Claude Code's own `Edit` tool: each replaces exactly one occurrence of `old_string` with `new_string` on the targeted block. They fail if the anchor is missing or appears more than once. If the call fails on non-uniqueness, extend the anchor with more surrounding context and retry. Patches are the right tool for a one-line tweak; do NOT rewrite the whole body for a small change.

**`AddVariable` semantics.** Inserts the declaration line before the matching scope's `END_VAR`. If the scope block does not exist on the target item, a new one is created at the conventional position (order: `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR`, `VAR CONSTANT`, `VAR_PERSISTENT`, `VAR_TEMP`). Use this instead of reading the full declaration, hand-editing the VAR block, and writing it back.

**Backend requirement.** Writer tools drive XAE over the COM Automation Interface and will not work unless TcXaeShell is open with the solution loaded (Windows only). Never work around a writer failure by editing `.TcPOU` / `.plcproj` yourself.

## Pre-write checks (in order)

1. **Safety-name guard.** If any name in the change touches `Safety`, `SIL`, `TÜV`/`TUV`, `Emergency`, `EStop`, `SafetyDoor`, or anything else that suggests safety-critical functionality, STOP. Show the user the proposed change, explain that it appears safety-critical, and wait for explicit approval before any writer call. This applies even if the change seems trivial.
2. **Rename guard.** TcKit's writer has no rename API. If the change involves renaming a symbol that exists elsewhere in the project, STOP. Report how many references you found and ask the user to approve before any manual find-and-replace. Never execute a cross-project rename autonomously.
3. **Unfamiliar Beckhoff FB.** If the new code instantiates a Beckhoff library FB you have not just researched via `tc-beckhoff-docs`, hand off to `tc-beckhoff-docs` now. Do not write code against a Beckhoff FB whose inputs/outputs/timing you only know from memory.

## Style

- **Project conventions.** Follow the conventions in the project's `CLAUDE.md` if it specifies any (naming, error pattern, public/private boundaries, etc.). Where the project does not specify, match the existing style of the file you are editing. The skill does not impose a default naming or error-handling convention.
- **Naming is checkable, so do not guess at it.** If the project configures a convention (`tckit_analysis_profile` in its `.editorconfig`), `AnalyseProject` is the authority on what that convention is. Write in the style of the surrounding code, then let the post-write check below correct you rather than arguing from memory about prefixes.
- **Comments.** The doc generator detects both RST line comments (`// :Description:`, `// :param x:`, `// :returns:`) and Beckhoff XML (`(*~ <docu> ~*)`). Match the file's existing style.

## Write procedure

1. If the user has named a specific FB and the change is a clear add (one variable, one method, or one patch with the anchor already stated), call the writer directly. The writer fails cleanly if the target FB is missing, so a defensive `GetPouInterface` "to confirm it exists" is wasted. Only read first when you actually need the existing shape, e.g. to choose a patch anchor or check a signature.
2. **Pick the smallest write that does the job** using the Tool selection table above. Small edit on a method -> `UpdateMethodBodyPatch`. Small edit on the FB-level decl / cyclic body -> `UpdatePouDeclarationPatch` / `UpdatePouImplementationPatch`. Single new variable -> `AddVariable`. Full method-body rewrite -> `UpdateMethodBody`. Full POU declaration / implementation rewrite -> `UpdatePouDeclaration` / `UpdatePouImplementation`. New unit -> `AddPou` / `AddMethod` / `CreateProject`.
3. For patch-based edits, fetch the current item with `GetPouItem` (or `GetPouDeclaration` if only the FB-level VAR block matters) so the anchor you choose is grounded in the real text, not your memory of it.
4. NEVER edit `.TcPOU` or `.plcproj` XML directly. GUIDs and cross-references go through the writer tools.
5. **Check what you wrote.** Call `AnalyseProject(projectPath, objectName: "<the POU you edited>")`. This is offline, needs no XAE, and returns in well under a second, so it costs nothing next to a build. It catches what the compiler will not: a function block instance declared in a method's `VAR` (its state resets every call), floating-point equality, an unused local, `RETAIN` where it cannot retain, and naming that departs from the project's configured convention.

   Fix what it reports, then re-run it on the same POU. Two rules apply:

   - **Never act on a naming finding for an existing symbol without asking.** The suggestion is advisory, and renaming something already referenced is exactly what the rename guard above reserves for the user. Naming findings on the symbols *you just introduced* are yours to fix freely.
   - Stop after two rounds. If a finding survives two attempts, report it to the user rather than continuing to edit.

   Cross-file rules (unread inputs, multi-writer globals, unreachable POUs) are skipped when `objectName` scopes the run, and the result says so in `rules_not_run`. Analysing the whole project is worth it once at the end of a larger change, not after every edit.

6. After the writer returns success, summarise what changed (POU, item, lines). The writer's success response is the confirmation; do not read the change back to "verify" it landed. Whether to build / deploy / test next is driven by the user's request: if they asked for tests to pass, a behaviour to be verified, or the project to build, hand off to `tc-build-test-loop` and run the cycle through to actual results. If they only asked for the edit, stop here.

## Anti-patterns

- Reading a `.TcPOU` file with `Read` and then editing it with `Edit`. The MCP writer tools exist to keep GUIDs and project cross-references consistent; bypassing them silently breaks the project.
- Greping for a method name in the raw XML and trying to patch the XML around it.
- Opening `.plcproj` with `Read` or `Edit` to add or remove `<Compile Include="..."/>` entries. `AddPou` maintains those entries itself.
- Calling a full-body rewrite (`UpdateMethodBody` / `UpdatePouDeclaration` / `UpdatePouImplementation`) for a one-line change. Use the matching `_patch` variant instead.
- Reading the full FB declaration, hand-editing the VAR block, and writing it back. Use `AddVariable` instead.
- Concluding "TcKit isn't working" because a reader tool succeeded but a writer tool failed. On the automation backend, writer tools require XAE open with the solution loaded; reader tools do not. Surface the writer's error to the user rather than reaching for stock-tool edits.
- Re-reading the changed item with `GetPouItem` / `GetPouDeclaration` / `Read` immediately after a successful writer call to confirm it landed. The writer already told you it succeeded. (This is about verifying the *write itself*, not about running the build / test cycle the user asked for — that's a hand-off to `tc-build-test-loop`, not a re-read.)
- Skipping the post-write `AnalyseProject` because the edit "was small". The state-losing declaration and the floating-point comparison it catches both compile, so a clean build is not evidence they are absent.
- Renaming an existing symbol because an analysis finding suggested a name. Suggestions are advisory; a rename on referenced code needs the user's approval.
- Running `AnalyseProject` across the whole project after every single edit. Scope it to the POU you touched; save the full pass for the end of a larger change.

## Next

If the user asked for tests to pass, a behaviour to be verified, or the project to build, the work is not done after the write — hand off to `tc-build-test-loop` for build → deploy → test and report the actual outcome. If the user only asked for the edit, stop here and report what changed.
