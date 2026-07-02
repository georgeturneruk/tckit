---
name: tc-read-project
description: Use when inspecting, navigating, or searching a TwinCAT 3 PLC project through TcKit's MCP tools (GetStructure, GetPouInterface, GetPouItem, GetGvl, GetDut) AFTER orientation. Triggers on requests like "show me FB_X", "what's the public API of FB_Motor", "list the methods of FB_TestSuite", "summarise the motor controller POU", "what does ST_Config look like", or any task that requires reading specific PLC code before writing it. Do NOT use for first-touch orientation (use tc-orient-project), and do NOT use for researching Beckhoff library FBs (use tc-beckhoff-docs).
allowed-tools: mcp__tckit__GetStructure, mcp__tckit__GetPouInterface, mcp__tckit__GetPouItem, mcp__tckit__GetGvl, mcp__tckit__GetDut, Read, Grep, Glob
---

# Reading TwinCAT projects through TcKit

First-touch orientation belongs to `tc-orient-project`. This skill picks up once orientation is done and the user asks for something specific.

Always read in layers. Never fetch a full POU when one method suffices.

## Tool selection — read this before calling anything

This skill uses **only the TcKit reader tools** and the stock `Read`/`Grep`/`Glob`. The reader tools are:

- `mcp__tckit__GetStructure` — project map (POUs, GVLs, DUTs, tasks, libraries)
- `mcp__tckit__GetPouInterface` — declarations and method signatures for one POU
- `mcp__tckit__GetPouItem` — declaration + body for one method, action, or property accessor
- `mcp__tckit__GetGvl` — one GVL's declaration
- `mcp__tckit__GetDut` — one DUT's declaration (struct, enum, union, alias)

**Reader tools read XML directly from disk. They do NOT need the Windows bridge service.** They work whenever the TcKit MCP server is reachable, even if no PLC is connected and the bridge is down.

**Do NOT call writer or build tools from this skill.** `mcp__tckit__OpenProject`, `AddPou`, `Build`, `Deploy`, `RunTests`, and similar are out of scope here. They require the Windows bridge service and have no bearing on reading. If you see one of those tools error with "bridge not reachable" or similar, that error tells you nothing about whether the reader tools work — keep using `get_*`.

Common request shapes and the one-call answer:

| Request                                          | Tool                                       |
| ------------------------------------------------ | ------------------------------------------ |
| "What's the API / methods / signatures of FB_X?" | `GetPouInterface(X)`                     |
| "Show me FB_X.Execute's implementation"          | `GetPouItem(X, "Execute")`               |
| "What fields does ST_Config have?"               | `GetDut("ST_Config")`                     |
| "What's in GVL_Params?"                          | `GetGvl("GVL_Params")`                    |
| "Where does FB_X live in the project?"           | check the earlier `GetStructure` payload |

If the request matches one of these, make the matching call first. Do not glob, grep, or read the raw `.TcPOU` XML when a single reader call answers the question.

## Procedure

1. **Map first, only if needed.** If you don't already know the POU/GVL/DUT name, call `GetStructure(project_path)`. Skip if the user named the symbol.
2. **Interface before body.** For any POU you'll touch, call `GetPouInterface(pou_name)` to get declarations and method signatures. Do NOT fetch bodies yet. For API-shape questions (methods, signatures, public surface) this is usually the only call you need.
3. **Single-item bodies.** For each method, action, or property whose logic you actually need, call `GetPouItem(pou_name, item_name)`. One call per item. Stop fetching as soon as you have what you need.
4. **GVLs and DUTs separately.**
   - `GetGvl(gvl_name)` for global variable lists.
   - `GetDut(dut_name)` for structs, enums, unions, and aliases. Do NOT try to read these via `GetPouItem` — they are not POUs.
5. **Don't refresh the map.** `GetStructure` is a one-shot orientation, not a per-turn refresh.
6. **Report with citations.** When summarising, name the POU and item you actually read so the user can verify. Don't paraphrase code you didn't fetch.

## Anti-patterns

- Calling writer or build tools (`OpenProject`, `AddPou`, `Build`, `Deploy`, `RunTests`) to "set up" a read. The reader tools do not need the project opened in XAE; they work directly on the files on disk.
- Concluding "TcKit isn't working" because one writer-side tool failed. The reader tools have a different code path and are unaffected.
- Fetching `GetPouItem` for every method "just in case".
- Calling `GetStructure` at the start of every turn.
- Quoting Beckhoff FB behaviour from memory — that's `tc-beckhoff-docs` territory.
- Using `GetPouItem` for an enum or struct (use `GetDut`).
- Using `Read` or `Grep` on `.TcPOU` / `.TcGVL` / `.TcDUT` files **as a substitute for `GetPouInterface` / `GetPouItem` / `GetGvl` / `GetDut`** when those tools are available. The MCP calls return just the slice you need; the raw XML files contain a lot of envelope noise.

## When TcKit MCP tools are unavailable

If the TcKit *reader* tools (`mcp__tckit__GetStructure` and friends) are not registered in this session, fall back to disciplined stock-tool reads:

1. **Locate** the symbol with `Glob` (e.g. `**/FB_Motor.TcPOU`). One call.
2. **Interface first.** `Read` the file with a `limit` that catches the `<Declaration>` block (typically the first ~80 lines of a POU file). Don't pull the whole file.
3. **Body when needed.** If you need a specific method body, search inside the file with `Grep` for the method name and `Read` with `offset`/`limit` around the match. Resist pulling the whole POU.
4. **GVLs and DUTs.** Read the file in full; these are usually small.

Same layered discipline as with the MCP tools, just done with `Glob`/`Grep`/`Read`. "Unavailable" here means the reader tools genuinely aren't registered, not that some other TcKit tool failed.

The anti-pattern is using raw XML reads *instead of TcKit when TcKit is available*, not using them as a fallback when it isn't.

## Next

If the task moves into writing or modifying code, hand off to `tc-write-st`. If a Beckhoff library FB needs research, hand off to `tc-beckhoff-docs`.
