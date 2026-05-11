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
`get_pou_item` for one method body. This is the project's answer to
context rot. Claude pulls the slice it needs, not the whole file.

Content search across the project has no TcKit-native equivalent.
"Where is FB_Motor instantiated?", "every place that writes
`GVL_Params.nMaxRetries`", "find references to *this* `Execute` method
specifically." On a Python project Claude reaches for `Grep`. On a
TwinCAT project the same question has no TcKit answer.

Two clarifications surfaced in review (2026-05-10) sharpen the problem
before we build for it.

1. **Stock `Grep` already works on `.TcPOU` files.** They are text on
   disk; ripgrep returns line-numbered matches over the CDATA-wrapped
   ST. The XML envelope adds bounded noise but is not a blocker. Grep
   alone does not pollute context, it returns matching lines.

2. **`Grep` + `get_pou_item` together cover most navigation cleanly.**
   Grep finds the symbol; the filename and line locate the POU and
   item; `get_pou_item` retrieves the clean ST body. No whole-file
   dump.

Two residual gaps remain:

- **Result noise and missing structure.** Stock `Grep` matches in XML
  attributes and `<Comment>` blocks alongside ST. A TwinCAT-aware
  search could filter to CDATA, optionally to code-not-comments, and
  return `(pou_name, item_name)` directly instead of asking Claude to
  infer them from filename and line.
- **Cross-instance disambiguation.** Stock `Grep` cannot bind
  `motor.Execute` to `FB_Motor.Execute` rather than the seven other
  `Execute` methods. Only a symbol table with scope awareness can.

Both are real but narrow. When Claude's actual TwinCAT workflows are
listed against where each gets stuck, search addresses the *refactor*
row, and refactor is bounded by the project rule that Claude does not
execute cross-project renames autonomously. The other rows
(orientation on a new project, runtime debugging, adding a feature)
have larger gaps that a search port does not fix: folder/subsystem
grouping, task and library context, IO mapping, live runtime state.

The question this ADR answers therefore shifts from "which
implementation should we build?" to "is search the right next thing
to build?". Option comparison and spike data are preserved below as
durable research; the recommendation reflects the new framing.

## Goals

- Make the residual search-shaped gaps (envelope noise, cross-instance
  disambiguation) addressable when a concrete need emerges, without
  committing to the work before that need is real.
- Preserve the implementation research (Options A through G) so
  whoever picks the work back up does not redo the spikes.
- Fit the existing ports/adapters architecture: adapter-isolated, port
  contract stable, implementation swappable.
- Establish what a future Stage 2 actually means and what would
  trigger promoting it.

## Port shape (provisional)

A new port `ProjectSearcher` in `tckit/ports/searcher.py` with two
methods:

```python
def search(
    self,
    pattern: str,
    *,
    glob: str | None = None,
    pou_type: str | None = None,
    output_mode: str = "content",        # "content" | "files_with_matches" | "count"
    case_insensitive: bool = False,
    multiline: bool = False,
    context_before: int = 0,
    context_after: int = 0,
    where: str = "all",                  # "code" | "comments" | "strings" | "all"
    literal: bool = False,
    head_limit: int = 100,
) -> SearchResult: ...

def find_symbol(
    self,
    name: str,
    *,
    kind: str | None = None,             # "function_block" | "function" | "method" | "property" | "variable" | "type" | None=any
    role: str = "all",                   # "all" | "definitions" | "references"
    scope: str | None = None,            # restrict to within a named POU
    head_limit: int = 100,
) -> SymbolResult: ...
```

`search` mirrors Claude Code's `Grep` so Claude reuses an existing
mental model; `where=` and `pou_type=` are the TwinCAT-specific
additions that come naturally because we have a parser. `find_symbol`
is the semantic surface for queries regex cannot answer accurately.

Output shape per `output_mode` mirrors Grep:

- `content`: list of `SearchMatch(file_path, pou_name, item_name, line, snippet, context_before, context_after)`
- `files_with_matches`: list of `(file_path, pou_name)` tuples
- `count`: list of `(file_path, pou_name, count)` tuples

`find_symbol` returns `SymbolMatch(name, kind, file_path, pou_name, item_name, line, role, signature_snippet)`.

## Tier framing

Capability decomposes into tiers; each tier solves a different
fraction of the problem at a different cost. The real cliff is between
Tier 1 and Tier 2.

| Tier | Capabilities | Estimated effort (one dev) |
|---|---|---|
| 0. Regex over CDATA | Pattern match. False-positives on comments / strings / similarly-named vars | 1-2 weeks |
| 1. Lexer-aware regex | Tokeniser distinguishes code / comments / strings; `where=` filtering | 3-5 weeks total |
| 2. Symbol-table semantic search | Symbol index; find references with scope awareness; no type resolution | 2-3 months total |
| 3. Type-resolving | Distinguishes `motor.Status` on FB_Motor vs FB_Pump; handles ExST | 4-6 months total |
| 4. Full LSP | Completion, rename, diagnostics; production-shaped | 6-12 months total |

For our use case (Claude navigating projects via MCP), Tier 2 is the
right destination. Tier 3 type resolution becomes a future ADR if and
when it bites. Tiers 0 and 1 are checkpoints in the implementation
order, not final designs.

## Implementation candidates explored

### Option A: Build our own (ANTLR4 + Serhioromano's `ST.g4`)

`Serhioromano/vscode-st` (194 stars, MIT, last release Feb 2026) ships
an ANTLR4 grammar (`ST.g4`) for IEC 61131-3 ST. Maintained for TwinCAT
syntax through real users hitting edge cases: `PROPERTY_GET` /
`PROPERTY_SET`, `.TcGVL` extension, Beckhoff conditional pragmas, ExST
type prefixes, XML-wrapped ST. The grammar is the hardest part of
building a parser.

ANTLR4 has a Python target. Generate parser → walk tree to build
symbol table → second walk for cross-references.

**Pros:** Full ownership; single language stack (Python);
adapter-isolated cleanly; future-proof against upstream churn.

**Cons:** Most upfront work (4-8 weeks for tier 2). We own the symbol
table and reference-index code forever. Need to extend the grammar as
TwinCAT idioms expand. ANTLR runtime adds one Python dependency.

### Option B: blark + pytmc (Python parser + project graph)

`klauer/blark` (54 stars, MIT, on PyPI/conda-forge) is a Python ST
parser using Lark/Earley. Author describes it as "a fun side project
[that] isn't at the top of my priority list", but releases continued
through 2025. Parses `.TcPOU/.TcGVL/.TcDUT/.TcIO/.tsproj/.sln`. Provides
`CodeSummary` for code-introspection.

`pcdshub/pytmc` (BSD-3, actively maintained by SLAC for production
EPICS deployment) parses `.tsproj/.plcproj/.tmc/.xti` and builds a
navigable `TwincatItem` tree. POUs, GVLs, DUTs, NC axes, EtherCAT
boxes, type information, library refs.

**Spike data (2026-05-09):**

- blark via `parse_project` on TcUnit (`TcUnit.tsproj`, 388 files):
  **387/388 = 99.7% success in 78s.** Single failure: `GVL_TcUnit.TcGVL`
  uses `STRING(1..GVL_TcUnit.MaxNumberOfTestSuites)`, a parameterised
  STRING type with cross-GVL constant blark's grammar doesn't handle.
- blark via `parse_single_file` on individual `.TcPOU` files: failed.
  Wrong API; concatenates declarations and bodies into one source then
  chokes when the parser hits a body statement out of context. Use
  `parse_project`, not `parse_single_file`.
- blark via `parse_project` on TcOpen TcoCore: cancelled after 5+
  minutes. Could mean larger tree or a pathological file. Failure rate
  not yet measured on TcOpen.
- pytmc on TcUnit `.tsproj`: 0.07s. 50 POUs, 3 GVLs, 11 DUTs. Each POU
  exposes `methods`, `actions`, `declaration`, `implementation`,
  `find_ancestor`, `get_fully_qualified_name`, `get_source_code`.
- pytmc on TcOpen TcoCore `.tsproj`: 0.37s. 126 POUs, 1 GVL, 41 DUTs.
  Plus typed objects for Axis, Encoder, EtherCAT, Box, BoundDataType,
  EnumInfo. Richer than expected.

**Pros:** Fastest to ship (2-4 weeks). Python-native, no subprocess.
pytmc handles `.plcproj` cross-references, library refs, NC axes,
EtherCAT topology out of the box. blark handles 99%+ of real TwinCAT
ST. MIT and BSD-3 licences.

**Cons:** blark is "a fun side project" per its author. pytmc's
primary use is EPICS-record generation, not navigation; some APIs may
be stable but undocumented. TcOpen blark performance unverified.
Performance overall (200ms/file) is workable for indexing, slow for
incremental responses. blark grammar gaps on TwinCAT-specific quirks
(parameterised STRINGs, vendor pragmas) will be a long tail.

### Option C: truST Platform (Rust LSP)

`johannesPettersson80/trust-platform` (189 stars, MIT/Apache-2 dual,
pushed today). Rust workspace with dedicated `trust-syntax`,
`trust-hir`, `trust-lsp`, `trust-ide` crates. Real LSP server
(verified 2026-05-09: `parser.rs` is 18 KB Rust source, grammar split
across `declarations.rs` / `expressions.rs` / `pou.rs` / `statements.rs`).
Standalone `trust-lsp` binary distributable. Explicit
`vendor_profile = "twincat"` in `trust-lsp.toml`.

**Pros:** Substantial engineering investment. Rust single binary, no
Node runtime needed. Active maintenance. Explicit TwinCAT awareness.
Could give us much more than search (diagnostics, formatting,
eventual rename-safety).

**Cons:** "TwinCAT-adjacent" by their own admission, not TwinCAT-native;
their migration doc says "truST is strongest on authoring and
interchange, not on reproducing every vendor project package
behaviour". Doesn't read `.TcPOU` natively (works on `.st` files); we'd
need to extract ST from CDATA and translate coordinates back. Pre-1.0
with daily churn. We'd bundle a Rust binary in our Python distribution.
JSON-RPC subprocess + LSP client adds complexity.

### Option D: ControlForge (TypeScript LSP)

`ControlForge-Systems/controlforge-structured-text` (17 stars, MIT,
last push 2026-03). VS Code extension with real LSP server
architecture. IEC 61131-3 OOP coverage including TwinCAT's PROPERTY
construct.

**Pros:** Real LSP; MIT; includes the substantive components (workspace
indexer, providers).

**Cons:** Smaller community (17 stars vs truST's 189). No explicit
TwinCAT support beyond standard IEC 61131-3. TypeScript means Node
runtime dependency. Same coordinate-translation tax as truST. Bus
factor of one author.

### Option E: PLC-lang/rusty (Rust compiler with LSP scaffold)

326 stars, LGPLv3, very active. Compiler frontend for ST with LLVM
backend. Has `language-server` and `lsp` topic tags, internal LSP
module, but no published, ready-to-install standalone language server
binary.

**Pros:** Most credible foundation for an eventual ST LSP. Active
maintenance.

**Cons:** No deployable LSP binary today. LGPLv3 licence makes
integration constrained. Compiler-grade dependency (LLVM toolchain).
Standard IEC 61131-3 dialect, not TwinCAT-specific OO out of the box.

### Option F: iec-checker (OCaml static analyser)

`jubnzv/iec-checker`, 101 stars, OCaml, LGPL-3.0, recently active.
Accepts ST and PLCopen XML. Implements PLCopen Software Construction
Guideline checks. JSON output.

**Pros:** Proven static-analysis pipeline. Could be a future
diagnostics backend (separate concern from search).

**Cons:** OCaml runtime dependency. LGPL constrained for our use.
Static analysis is orthogonal to search; useful as a supplement, not a
foundation.

### Option G: Tree-sitter grammars for ST

`tmatijevich/tree-sitter-structured-text` (3 stars, MIT, last commit
~4 years ago) and `retrofit-st/tree-sitter-structured-text` (early
WIP).

**Verdict:** Both toy-stage. Neither targets TwinCAT XML envelopes.
Skip.

## Related work

- **`agenticcontrolio/twincat-validator-mcp`** — Existing TwinCAT MCP
  server. Validation and auto-fix for `.TcPOU/.TcIO/.TcDUT/.TcGVL`,
  21 IEC 61131-3 OOP checks, deterministic auto-fixes. Different
  niche from navigation. Worth studying for MCP integration patterns
  and `.twincat-validator.json`-style config conventions. Complement,
  not competitor.
- **PLCopen XML / IEC 61131-10** — Standardised inter-vendor exchange
  format. Not what TwinCAT uses natively. Could be a future
  normalisation/export target.
- **Anthropic Issue #24249** — Open feature request to expose
  host-IDE LSP capabilities as Claude Code tools natively. Suggests
  the wider ecosystem direction is LSP integration; relevant context
  for whichever foundation we pick.

## Open questions

1. **Implementation choice.** Lean is Option B (blark + pytmc) for the
   fastest path to validating the port surface, but the TcOpen
   verification is incomplete and the blark/pytmc composition for
   `find_symbol` end-to-end is unverified. Need either: (a) more spike
   work to close those gaps before committing, or (b) commit to Stage
   1 with eyes open about the residual risk.
2. **Vendor blark, or pin as a regular dependency?** Vendoring removes
   upstream surprise risk but adds ~1 MB to the repo and we own
   maintenance. Pinning is lighter but exposes us to blark's release
   cadence (or lack of it).
3. **One adapter or two?** Could be a single
   `blark_pytmc_searcher` adapter, or separate adapters for the symbol
   layer (blark) and the project-graph layer (pytmc). Two is cleaner
   architecturally; one is less wiring.
4. **Performance tier needed.** 200ms/file via blark is fine for
   one-time indexing, slow for responsive incremental queries. Do we
   need to plan for caching/incremental updates from day one?
5. **Stage 2 trigger.** What's the signal that Stage 1 isn't enough
   and we should escalate to ANTLR-from-scratch or truST integration?

## Provisional recommendation

**Defer.** The two narrow gaps that survive scrutiny (envelope noise,
cross-instance disambiguation) are real but bounded. Claude's actual
TwinCAT workflows (orientation, debugging, feature add) are
bottlenecked on different things — subsystem framing from project
files, task and library context, IO mapping, live PLC state — not on
search.

The next investment should target those. See **ADR-0002** for the
orientation track (extending `get_structure` with task, library, and
folder grouping; pairing it with a `tc-orient-project` skill).

If a concrete need for search emerges from real Claude sessions
(repeated brittle disambiguation, or "who calls X" queries that stock
`Grep` cannot answer cleanly), return here. When that happens, the
work should start narrow:

- `find_callers(pou_name, item_name)` and `find_instantiations(fb_name)`
  only, on the blark + pytmc foundation already characterised in
  Options A and B.
- Skip the general `search()` surface and the broader
  `find_symbol(kind=...)` taxonomy unless a separate need for them
  appears.

Validation gates if the work is picked up:

1. Re-run blark on TcOpen with tighter scope to measure actual
   failure rate (a few hours of work).
2. Build a tiny end-to-end "find references to FB_X" prototype to
   validate the blark + pytmc composition (a day or two).

If both succeed, promote with a Decision section scoped to the two
narrow methods. If either fails, fall back to Option A (ANTLR + ST.g4)
and reassess. The full `search()` and general `find_symbol(kind=...)`
surface as drafted above remain on the shelf, not the next step.

## Status notes

- 2026-05-09: Drafted as `Exploring`. Status `Exploring` introduced as a
  refinement to the ADR convention defined in `CLAUDE.md`; the original
  convention listed `Proposed | Accepted | Implemented | Superseded`
  only, and `Exploring` precedes `Proposed` for ADRs that capture
  investigation before a specific proposal. Convention update in the
  same PR.
- 2026-05-09: Spike performed on blark + pytmc against TcOpen and
  TcUnit. blark via `parse_project` parses TcUnit at 387/388 (99.7%)
  in 78s; one failure is parameterised STRING with cross-GVL constant.
  TcOpen blark run cancelled after 5 min, scope possibly too wide.
  pytmc parses both projects instantly (0.07s for TcUnit, 0.37s for
  TcOpen TcoCore). truST source-code reality check passed; the source
  is real, not docs-only as one external research summary had claimed.
- 2026-05-10: Recommendation revised after framing review. Stock
  `Grep` on `.TcPOU` files plus `get_pou_item` was confirmed to cover
  most navigation without context pollution; the residual gaps are
  narrower than the original framing suggested. The orientation
  problem (subsystem grouping, task layout, library refs) is the
  higher-leverage navigation investment and is split out into
  ADR-0002. This ADR remains `Exploring` with research preserved;
  the recommendation has moved from "Stage 1 = blark + pytmc" to
  "defer until a concrete narrow need emerges, then start with
  `find_callers` and `find_instantiations` only".
- 2026-05-11: Bench harness's `02-find-callers` task parked
  (`bench/tasks/_parked/02-find-callers.md`). The bench cannot
  discriminate TcKit-on from TcKit-off on find-callers workflows until
  this ADR is promoted to Implemented; both configs default to `Grep`.
  Re-instate the task when a `find_callers` / `find_instantiations`
  surface lands.
