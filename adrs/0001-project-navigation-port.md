---
adr: 0001
title: Project navigation port (ProjectSearcher)
status: Exploring
created: 2026-05-09
issue:
pr:
---

## Context

TcKit's `ProjectReader` exposes a layered just-in-time read pattern:
`get_structure` for discovery, `get_pou_interface` for API surface,
`get_pou_item` for one method body. This is the project's answer to context
rot. Claude pulls the slice it needs, not the whole file.

Content search across the project has no TcKit-native equivalent. "Where is
FB_Motor instantiated?", "every place that writes `GVL_Params.nMaxRetries`",
"find references to *this* `Execute` method specifically." On a Python
project Claude reaches for `Grep`. On a TwinCAT project the same question
has no TcKit answer.

Two observations sharpen the gap. First, stock `Grep` already works on
`.TcPOU` files: ripgrep returns line-numbered matches over the CDATA-wrapped
ST. The XML envelope adds bounded noise but is not a blocker. Second,
`Grep` + `get_pou_item` together cover most navigation cleanly: Grep finds
the symbol, the filename and line locate the POU and item, `get_pou_item`
retrieves the clean ST body.

Two residual gaps remain:

- **Result noise and missing structure.** Stock `Grep` matches in XML
  attributes and `<Comment>` blocks alongside ST. A TwinCAT-aware search
  could filter to CDATA, optionally to code-not-comments, and return
  `(pou_name, item_name)` directly.
- **Cross-instance disambiguation.** Stock `Grep` cannot bind
  `motor.Execute` to `FB_Motor.Execute` rather than the seven other
  `Execute` methods. Only a symbol table with scope awareness can.

Both are real but narrow. Claude's other TwinCAT workflows (orientation on
a new project, runtime debugging, adding a feature) are bottlenecked on
different things: subsystem grouping, task and library context, IO mapping,
live PLC state. Search addresses the *refactor* row only.

## Decision

**Defer.** The residual search gaps are real but bounded; the higher-leverage
navigation investment is orientation (ADR-0002). When a concrete need for
search emerges from real sessions (repeated brittle disambiguation, or "who
calls X" queries that stock `Grep` cannot answer cleanly), reopen this ADR
with a *narrow* surface:

- `find_callers(pou_name, item_name)` and `find_instantiations(fb_name)`
  only, on the blark + pytmc foundation characterised below.
- Skip the general `search()` and broader `find_symbol(kind=...)` surface
  unless a separate need surfaces.

Two validation gates before promoting:

1. Re-run blark on TcOpen with tighter scope to measure actual failure rate.
2. Build a tiny "find references to FB_X" prototype to validate the
   blark + pytmc composition end-to-end.

If both succeed, scope a Decision to the two narrow methods. If either
fails, fall back to ANTLR + `ST.g4` (option A below) and reassess.

## Port shape (provisional)

A new port `ProjectSearcher` in `tckit/ports/searcher.py` with two methods:

```python
def search(
    self,
    pattern: str,
    *,
    glob: str | None = None,
    pou_type: str | None = None,
    output_mode: str = "content",
    case_insensitive: bool = False,
    multiline: bool = False,
    context_before: int = 0,
    context_after: int = 0,
    where: str = "all",
    literal: bool = False,
    head_limit: int = 100,
) -> SearchResult: ...

def find_symbol(
    self,
    name: str,
    *,
    kind: str | None = None,
    role: str = "all",
    scope: str | None = None,
    head_limit: int = 100,
) -> SymbolResult: ...
```

`search` mirrors Claude Code's `Grep` so Claude reuses an existing mental
model; `where=` and `pou_type=` are the TwinCAT-specific additions.
`find_symbol` is the semantic surface for queries regex cannot answer.

## Tier framing

Capability decomposes into tiers. The real cliff is between Tier 1 and
Tier 2.

| Tier | Capabilities | Effort (one dev) |
|---|---|---|
| 0. Regex over CDATA | Pattern match; false positives on comments / strings | 1-2 weeks |
| 1. Lexer-aware regex | Tokeniser distinguishes code / comments / strings; `where=` filtering | 3-5 weeks |
| 2. Symbol-table semantic | Symbol index; find references with scope awareness; no type resolution | 2-3 months |
| 3. Type-resolving | Distinguishes `motor.Status` on FB_Motor vs FB_Pump; handles ExST | 4-6 months |
| 4. Full LSP | Completion, rename, diagnostics; production-shaped | 6-12 months |

Tier 2 is the right destination. Tier 3 type resolution becomes a future
ADR if and when it bites.

## Alternatives considered

- **Option A: ANTLR4 + Serhioromano's `ST.g4`.** Maintained MIT grammar
  (194 stars, Feb 2026); ANTLR has a Python target. Most upfront work
  (4-8 weeks for tier 2) but full ownership and single language stack.
  Fallback if blark + pytmc spike fails.
- **Option B: blark + pytmc.** Python ST parser (blark) + project graph
  (pytmc, BSD-3, actively maintained by SLAC). Spike data (2026-05-09):
  blark `parse_project` hit 387/388 (99.7%) on TcUnit in 78s; pytmc
  parsed TcUnit's `.tsproj` in 0.07s and TcOpen TcoCore in 0.37s. blark
  on TcOpen cancelled after 5 min, scope unverified. Fastest to ship
  (2-4 weeks); preferred for the narrow surface above.
- **Option C: truST Platform (Rust LSP).** 189 stars MIT/Apache-2; real
  LSP server with `vendor_profile = "twincat"`. Doesn't read `.TcPOU`
  natively, pre-1.0 churn, bundles a Rust binary. Skip unless we want
  more than search.
- **Option D: ControlForge (TypeScript LSP).** 17 stars MIT; no explicit
  TwinCAT support, Node runtime, bus factor of one. Skip.
- **Option E: PLC-lang/rusty (Rust compiler).** LGPLv3 compiler frontend;
  no deployable LSP binary today, LLVM toolchain dependency. Skip.
- **Option F: iec-checker (OCaml).** Static analyser, LGPL-3.0;
  orthogonal to search. Possible future diagnostics backend, not a
  foundation.
- **Option G: tree-sitter grammars for ST.** Both available repos are
  toy-stage and don't target TwinCAT XML envelopes. Skip.

`agenticcontrolio/twincat-validator-mcp` is a complementary MCP server
(validation, auto-fix); worth studying for integration patterns but not a
search alternative.

## Consequences

**Enables:** keeping the search investment proportional to the actual gap.
The orientation work (ADR-0002) addresses the higher-leverage navigation
problem without committing to a multi-week parser build.

**Costs:** Claude continues to use stock `Grep` for content search and
infers POU / item names from filename + line. Cross-instance
disambiguation queries stay manual.

**Locks out:** nothing. The port shape and tier framing are preserved so
whoever picks the work back up doesn't redo the spikes.

## Status notes

- 2026-05-09: Drafted as `Exploring`. `Exploring` introduced as a status
  preceding `Proposed` for ADRs that capture investigation before a
  specific proposal.
- 2026-05-09: blark + pytmc spike performed against TcOpen and TcUnit
  (numbers in option B above). truST source-code reality check passed.
- 2026-05-10: Recommendation revised after framing review. Stock `Grep` +
  `get_pou_item` covers most navigation cleanly; residual gaps are
  narrower than the original framing. Orientation split out into
  ADR-0002. This ADR moves from "Stage 1 = blark + pytmc" to "defer until
  a concrete narrow need emerges, then start with `find_callers` /
  `find_instantiations` only".
- 2026-05-11: Bench harness's `02-find-callers` task parked
  (`bench/tasks/_parked/02-find-callers.md`). The bench cannot
  discriminate TcKit-on from TcKit-off on find-callers workflows until
  this ADR promotes to Implemented; both configs default to `Grep`.
  Reinstate the task when a `find_callers` / `find_instantiations`
  surface lands.
