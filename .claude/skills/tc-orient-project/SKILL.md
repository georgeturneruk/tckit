---
name: tc-orient-project
description: Use on the FIRST encounter with a TwinCAT project in a session, or when the user asks for a "structural overview", "subsystem map", "what's in this project", "introduce me to this project", "give me the lay of the land", "tell me about this codebase", "summarise this project", or any first-touch orientation. Triggers BEFORE tc-read-project; tc-read-project takes over for follow-up reads once orientation is done. Do NOT use mid-task on a project already mapped this session.
allowed-tools: mcp__tckit__GetStructure, mcp__tckit__GetPouInterface
---

# Orienting on a TwinCAT project

First-touch orientation is a fixed-budget activity. The point is to give the user a useful overview, not to inventory every POU. Stay disciplined.

## Procedure

1. **One `GetStructure` call.** Call `GetStructure(project_path)` exactly once. Read the full response. The returned `ProjectStructure` already contains everything needed for the overview:
   - `pous[].folder` groups POUs by subsystem (POUs sharing a folder are part of the same subsystem).
   - `tasks` lists each task with `cycle_time_us`, `priority`, and the bound `programs`.
   - `libraries` lists library references (Beckhoff and third-party).
   - `gvls`, `duts` are flat name lists.
2. **Identify subsystems.** Group `pous` by `folder`. Each non-empty folder is a candidate subsystem. The flat (folder="") group is the project's "loose" code. Report the folder names and how many POUs each contains.
3. **Find the entry point.** From `tasks[].programs`, identify which POU is bound to the primary cyclic task (typically the one with the shortest `cycle_time_us`). Call `GetPouInterface` on that one POU.
4. **Sample one FB per subsystem.** Pick at most 4 subsystems. For each, choose one top-level FB (avoid helpers, internal-looking names, things in a "Functions" or "Utilities" folder). Call `GetPouInterface` on it. The aim is to learn the project's naming and error-handling conventions, not to map every method.
5. **STOP.** Report the orientation. Subsystems, tasks (with cycle times), libraries, entry point, conventions. Do not crawl further. If the user's request demands deeper reading, hand off to `tc-read-project`.

## Budget

- Total tool calls for orientation should be ≤ 6: one `GetStructure` + at most one entry-point read + at most four subsystem-sample reads.
- If you find yourself wanting a fifth `GetPouInterface`, you are no longer orienting; stop and report what you have.

## Anti-patterns

- Reading every POU "to be thorough". The whole point is to stop early.
- Calling `GetPouItem` during orientation. Bodies come later, via `tc-read-project`.
- Using `Read` or `Grep` on `.TcPOU` / `.TcGVL` / `.TcDUT` files **as a substitute for `GetStructure` / `GetPouInterface`** when those tools are available. `GetStructure` already mapped them; raw XML reads in that case waste the context window.
- Calling `GetStructure` again later in the session "to refresh". It is one-shot per session unless the project changes on disk.
- Quoting library behaviour from memory. If a library FB needs explaining, hand off to `tc-beckhoff-docs`.

## When TcKit MCP tools are unavailable

If `mcp__tckit__GetStructure` is not registered in this session (e.g. the MCP server isn't running, or the user has not configured TcKit), fall back to the minimum-cost path:

1. Read the `.plcproj` (item manifest, library refs) and `.tsproj` (tasks) directly. These are small project-shaping XML files; reading them is cheap and gives most of what `GetStructure` would.
2. Identify subsystems from the folder structure of the POU manifest in the `.plcproj`.
3. Avoid reading `.TcPOU` bodies in bulk; sample one or two at the declaration level only, via `Read` on specific files, to learn conventions.
4. Report the orientation. Same budget applies: ≤ 6 reads total.

The anti-pattern is using raw XML reads *instead of TcKit when TcKit is available*, not using them as a fallback when it isn't.

## What a good orientation looks like

> "TcUnit splits into four subsystems by folder:
> - `POUs` (15 FBs) — the test framework core (FB_TestSuite, FB_TestResults, FB_TcUnitRunner).
> - `POUs/Functions` (12 FUNCTIONs) — helpers used by the framework.
> - `POUs/Functions/WRITE_PROTECTED_` (16 FUNCTIONs) — type-specific write-protect wrappers.
> - `DUTs`/`GVLs`/`ITFs` — supporting types and interfaces.
>
> No PLC tasks are defined; this is a library project. Library refs: Tc2_Standard, Tc2_System, Tc2_Utilities, SysFile, SysDir, plus a direct ref to Base Interfaces.
>
> Conventions in FB_TestSuite: PascalCase FB names, `bError` propagation, `// :Description:` doc comments. Inputs are prefixed by type (`sName`, `nValue`)."

That is the shape: subsystems with one-line summaries, tasks if any, libraries, conventions inferred from one or two sampled interfaces. Then stop.

## Next

When orientation is done and the user asks something specific, hand off to:
- `tc-read-project` for deeper reads (method bodies, GVL contents, DUT details).
- `tc-write-st` if the next request is to modify code.
- `tc-beckhoff-docs` if an unfamiliar Beckhoff FB needs research.
