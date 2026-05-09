---
adr: 0001
title: Project navigation port (ProjectSearcher)
status: Exploring
created: 2026-05-09
issue:
pr:
---

## Context

TcKit's `ProjectReader` exposes a layered just-in-time read pattern that
matches Claude's Python workflow well: `get_structure` for discovery,
`get_pou_interface` for API surface, `get_pou_item` for one method body.
This covers the "Read with offset/limit" half of how Claude navigates
code.

The other half, content search, has no TcKit equivalent. On a Python
project Claude reaches for `Grep` after `Glob`. On a TwinCAT project the
same question ("where is FB_Motor instantiated?", "every place that
writes `GVL_Params.nMaxRetries`") has no answer that doesn't require
reading every POU body, which collapses the whole point of the
layered-read design.

This is the largest gap measured against Python-project parity. Beyond
pure pattern matching, TwinCAT's structure (XML-wrapped ST,
POU/method/property scopes, library namespaces) means a semantic
find-symbol capability would resolve queries that regex cannot answer
accurately ("find references to *this* `Execute` method on FB_Motor
specifically, not the seven others with the same name").

## Goals

- Match Claude Code's existing tool surface (`Grep`) where it makes
  sense, so Claude can apply the same mental model it uses on Python
  projects.
- Add semantic find-symbol capability that goes beyond what generic Grep
  can do, exploiting the TwinCAT structure we have access to.
- Fit the existing ports/adapters architecture: adapter-isolated, port
  contract stable, implementation swappable.
- Build a strong base: optimise for time-to-learning the right port
  shape now; design so the implementation can be swapped later without
  breaking consumers.

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

Strongly lean: **Option B (blark + pytmc) for Stage 1**, with vendoring,
single adapter, and an explicit migration plan documented for the day
the limits bite. Validation steps before fully committing:

1. Re-run blark on TcOpen with tighter scope to measure the actual
   failure rate (a few hours of work).
2. Build a tiny end-to-end "find references to FB_X" prototype to
   validate the blark + pytmc composition for the load-bearing use
   case (a day or two).

If both succeed, promote this ADR to `Proposed` with a clear Decision
section and proceed with implementation. If either fails, fall back to
Option A (ANTLR + ST.g4) and reassess.

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
