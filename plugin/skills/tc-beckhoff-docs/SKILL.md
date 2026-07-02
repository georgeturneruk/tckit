---
name: tc-beckhoff-docs
description: Use when researching a Beckhoff TwinCAT library, function block, or function via Beckhoff infosys, OR when looking up a Beckhoff hardware product by order number. Triggers on requests like "what does FB_EcCoESdoRead do", "which TF library has SDO read", "what are the inputs of FB_X", "what are the technical specs of EL3004", "look up EPP6228", "how many channels does the EK1100 have", or as a precondition before writing code that uses an unfamiliar Beckhoff FB. Uses TcKit's FindFb, SearchDocs, FindHardware, and GetDocPage MCP tools. Do NOT use for inspecting the user's local project (that is tc-read-project).
allowed-tools: mcp__tckit__FindFb, mcp__tckit__SearchDocs, mcp__tckit__FindHardware, mcp__tckit__GetDocPage
---

# Researching Beckhoff infosys

Beckhoff FB conventions, timing notes, and hardware specs are not reliable in training data, especially for newer TF libraries and hardware. Always ground claims in infosys.

## Procedure

1. **Known FB name → `FindFb`.** If the user (or your prior reasoning) has a specific FB name, call `FindFb(fbName)` first. The result includes inputs, outputs, timing notes, and a description.
2. **Fuzzy query → `SearchDocs`.** When you don't know the exact name, call `SearchDocs(query, section="")`. Use `section` to scope to a known infosys area when you know one.
3. **Hardware order number → `FindHardware`.** For a Beckhoff hardware product (e.g. EL3004, EK1100, EPP6228-0022, EJ1100, CU1128), call `FindHardware(orderNumber)`. It returns the product description, the infosys URL, and the parsed "Technical data" table. The order number may carry a variant suffix (`EPP6228-0022`); it is normalised to the bare order. Covers EtherCAT Terminals (EL/EM/ELM/ED), couplers (EK), EtherCAT Box (EP/ER/EQ), EtherCAT P Box (EPP), plug-in modules (EJ), IO-Link boxes (EPI/ERI), and infrastructure/switches (CU).
4. **Need full content → `GetDocPage`.** If a `FindFb`, `SearchDocs`, or `FindHardware` snippet is insufficient, call `GetDocPage(url)` for the full parsed page. Pages are cached locally — re-calls are cheap.
5. **Always cite.** When reporting findings, include the infosys URL so the user can verify. Do not paraphrase parameter or technical-data tables without referencing the source.
6. **Hand back.** Once you have the inputs/outputs/timing/specs you need, return control to the calling skill (typically `tc-write-st`) with a one-line summary plus the URL.

## Notes

- **Latency.** The first lookup into an uncached infosys section crawls the menu tree (with a polite delay) and can take a few seconds; the result is cached, so repeat lookups are local and fast.
- **`FindHardware` empty `technical_data`.** For the pure catch-all sections `erxxxx` (rugged) and `eqxxxx` (stainless steel), infosys has no per-order datasheet page, so the description and URL resolve but `technical_data` is `[]`. ER/EQ boxes are the EP equivalents in a different housing — if the user needs the table, look up the matching `EP` order number and flag that the housing/protection differs.

## Anti-patterns

- Answering "what does FB_X do" or "what are the specs of <order>" from memory without `FindFb` / `FindHardware`.
- Calling `GetDocPage` before trying `FindFb` / `SearchDocs` / `FindHardware` (they already fetch the page when needed).
- Citing "Beckhoff documentation" without the specific URL.

## Next

If this was a precondition step for a write, hand off to `tc-write-st`. If the task is configuring I/O hardware in the project (adding masters/boxes, scanning the bus, scaffolding I/O), that is `tc-hardware`.
