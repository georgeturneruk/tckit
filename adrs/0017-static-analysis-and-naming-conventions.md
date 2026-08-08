---
adr: 0017
title: Static analysis and naming conventions
status: Accepted
created: 2026-08-09
last_reviewed: 2026-08-09
issue:
pr:
related: [0001, 0005, 0008, 0015]
---

## Current state

**Decision (live):** Ship an offline static analyser as `TcKit.Adapters.Analysis`
behind an `IProjectAnalyser` port, surfaced as a single `AnalyseProject` MCP tool.
Rules carry `TCK<n>` ids in four bands (1xxx naming, 2xxx correctness, 3xxx
structure, 4xxx metrics). Configuration is `.editorconfig`-shaped and follows the
Roslyn schema (`tckit_naming_symbols` / `tckit_naming_style` / `tckit_naming_rule`,
plus `tckit_analyzer_diagnostic.category-<name>.severity`). Four shipped naming
profiles: **`hybrid` (default)**, `dotnet`, `hungarian`, `infer`, plus `none`.
Scope rule: only ship rules the TwinCAT compiler does not already catch. Nothing
is auto-fixable in v1.

**Where it lives:** Naming lane implemented, PR open.
`TcKit.Core/Analysis` (`StSource` masker, `DeclarationParser`, DTOs),
`TcKit.Core/Ports/IProjectAnalyser.cs`, `IProjectReader.GetPouSourceAsync`,
`dotnet/src/TcKit.Adapters.Analysis` (config loader, profiles, rule engine),
`TcKit.Server/Tools/AnalysisTools.cs`. Four rule ids ship: `TCK1001` objects,
`TCK1002` variables, `TCK1003` members, `TCK1004` struct/enum members.

**Open questions:**
- `infer` is accepted but not implemented; it warns and falls back to `hybrid`.
- `prefix_composition` and `recursive_type_prefix` were specified below but are not
  in the shipped schema. `hungarian` enforces type prefixes only, with no scope
  composition (`gbEnable`) and no recursion into `POINTER TO POINTER`.
- Finding location is reported as `(pou, item, line-within-item)`. Whether to also
  compute a real file line in the `.TcPOU` is deferred until a consumer needs it.
- Casing-only corrections are reference-safe (ST identifiers are case-insensitive),
  so they could become the first fixable rules. Deliberately deferred past v1.
- A `TCK1005` "redundant type prefix" rule would catch what `hybrid` structurally
  cannot: a Hungarian-prefixed method local is already camelCase, so it conforms.
  Gating the rule on prefix/type agreement (`nCount : INT`) makes it precise; the
  same agreement test already gates suggestion stripping.
- Correctness rules (`TCK2xxx`) are specified but land after the naming lane.

## Context

`Build` tells us what does not compile. Nothing tells us what compiles and is
still wrong, and in TwinCAT that gap is unusually wide: an FB instance declared in
a method's VAR block silently loses state every call, `REAL` equality compiles
fine, a GVL written from two tasks is a race, a POU neither instantiated nor
task-bound is dead weight. These are exactly the mistakes a model writing ST makes,
and none of them reach the error list.

The reader (ADR-0002, ADR-0005) already provides the expensive half: solution walk,
multi-PLC `.plcproj` discovery, a symbol index with mtime invalidation, and
`TcFileParser.ParsePouFull`, which returns declaration plus body plus every
method/action/property with bodies. What it does not provide is any understanding
of ST. Every existing extractor is a regex over declaration text; there is no
tokeniser, no VAR-block parser, no symbol table.

Beckhoff sells this capability as TE1200 (a CODESYS Static Analysis rebadge, ~100
rules, `SA00xx` plus a separate `NC01xx` naming namespace). It is licensed, runs
only inside XAE, and is per-PLC-project. It cannot run in CI, cannot run without a
licence, and has no cross-PLC view. `twincat-validator-mcp` is closer in spirit (an
MCP server, 34 checks) but is file-level: no symbol index, no task bindings, and
its naming checks are object prefixes only because it has no type information. A
large part of its check set (GUIDs, LineIds, CDATA formatting) exists because it
edits XML directly; TcKit writes through the Automation Interface, so that entire
category cannot drift for us.

Two properties of ST shape the naming design. Identifiers are **case-insensitive**,
so `nCount` and `NCOUNT` are one symbol: naming rules are cosmetic to the compiler
and can never be correctness rules. A **leading underscore is legal**, so
`_camelCase` for private fields is available.

## Decision

**Tiering.** Tier A is structural and cross-file, using `ProjectStructure`
metadata only. Tier B needs a comment/string/pragma-aware ST tokeniser plus a
VAR-block declaration parser, both in `TcKit.Core` (shared logic, and ADR-0001's
deferred `find_callers` wants the same machinery). Tier C (CFG, dataflow,
reachability) is explicitly out of scope.

**Selection principle.** A rule ships only if the compiler does not already catch
it. `Build` owns everything else. This is what keeps the analyser from being a
slower duplicate of the error list.

**Ids.** `TCK` prefix, three chars, chosen because `SA` is taken twice over (both
StyleCop and CODESYS) and our findings will sit next to TE1200's `SA00xx`/`NC01xx`
and TwinCAT's own compiler codes in a user's head. Bands: `TCK1xxx` naming,
`TCK2xxx` correctness, `TCK3xxx` structure and dead code, `TCK4xxx` metrics. Per
Roslyn guidance, an id is permanent from first publish: never reused, never
renamed.

**Configuration.** The project's real `.editorconfig`, under
`[*.{TcPOU,TcGVL,TcDUT}]` sections. This buys ancestor-walk discovery and
per-folder overrides (the equivalent of Ruff's `per-file-ignores`) from
`.editorconfig`'s native semantics, with no new file format and no new mechanism,
and the glob keeps .NET tooling out of our sections. The schema is Roslyn's
three-part split, which decouples reusable styles from the symbol groups they
apply to:

```ini
tckit_naming_symbols.<name>.applicable_kinds / .applicable_sections
                           / .applicable_accessibilities / .applicable_types
                           / .required_modifiers
tckit_naming_style.<name>.capitalization / .required_prefix / .required_suffix
                         / .word_separator / .prefix_composition
                         / .recursive_type_prefix
tckit_naming_rule.<name>.symbols / .style / .severity
```

`applicable_sections` is the ST-specific addition (`var_input`, `var_output`,
`var_in_out`, `var`, `var_global`, `var_stat`, `var_temp`, `var_constant`,
`var_inst`) and is what selects variables. `applicable_scopes` (`object` /
`member`) separates instance state from locals, which sections alone cannot do.
`applicable_accessibilities` maps 1:1 to .NET but selects only methods, properties
and actions, since TC3 has no per-variable access modifier. `applicable_types` is
borrowed from typescript-eslint's `types`
selector and is what makes the `hungarian` profile expressible.
`prefix_composition` and `recursive_type_prefix` are lifted from CODESYS (combined
scope+type prefixes, and recursive prefixes for `POINTER TO POINTER` /
`REFERENCE TO ARRAY`); both are inert when no type prefix is configured. Rules sort
most-specific-first automatically, as both Roslyn and typescript-eslint do.

Severity is Roslyn's ladder: `error | warning | suggestion | silent | none`, with
category-level bulk config. Defaults are Clippy-shaped: correctness on, naming at
`suggestion`.

**Naming profiles.** `tckit_analysis_profile = hybrid | dotnet | hungarian | infer
| none`, shipped as resources and overridable per rule.

`hybrid` is the default and draws a principled line: Hungarian does two unrelated
jobs, and only one of them earns its place. Type prefixes on variables (`b`, `n`,
`st`, `a`, `p`) restate what the declaration already says, go stale when types
change, and add noise at every use site. Kind prefixes on objects (`FB_`, `F_`,
`PRG_`, `I_`, `ST_`, `E_`, `GVL_`) encode something TwinCAT genuinely does not
surface: POUs, DUTs and GVLs share one flat namespace, and a bare `Config` gives no
clue whether it is a struct, an enum or an FB. .NET itself kept `I` for interfaces
for the same reason. So: keep kind prefixes on objects, drop type prefixes on
variables, apply .NET casing throughout.

The deciding argument is migration cost, not taste. Object renames are the
expensive ones: they cross project boundaries, appear in the project tree, and the
[tc-write-st](../.claude/skills/tc-write-st/SKILL.md) rename guard makes them
user-approved manual work. Variable violations are overwhelmingly on FB-local and
method-local names with few or zero external references. `hybrid` puts all the
churn where it is cheapest and leaves the costly renames untouched, which makes
adoption on an existing project tractable.

| ST construct | C# analogue | `hybrid` |
|---|---|---|
| `FUNCTION_BLOCK` / `FUNCTION` / `PROGRAM` | class | `FB_Motor`, `F_Clamp`, `PRG_Main` |
| `INTERFACE` | interface | `I_Motor` (consistent with the other kind prefixes) |
| `STRUCT` / `UNION` / enum type | struct / enum | `ST_Config`, `E_State` |
| GVL | static class | `GVL_Parameters` |
| `METHOD` / `ACTION` / `PROPERTY` | method / property | PascalCase |
| `VAR_INPUT` / `VAR_OUTPUT` / `VAR_IN_OUT` | public surface | PascalCase: `Enable`, `ErrorId` |
| FB-level `VAR` / `VAR_STAT` | private field | `_camelCase` |
| method-local `VAR` / `VAR_TEMP` / `VAR_INST` | local | camelCase |
| `VAR CONSTANT` | const | PascalCase, not SCREAMING_SNAKE |
| `VAR_GLOBAL` | public static field | PascalCase |

`dotnet` is `hybrid` with object prefixes dropped too (`Motor`, `IMotor`,
`Config`). `hungarian` is the Beckhoff/CODESYS convention in full. `infer`
tabulates the project's own declarations and reports deviations from its observed
majority rather than from an imposed table, matching case-insensitively and
reporting the observed casing; it is the honest answer for a brownfield project
where no shipped profile fits. That all three house styles fall out of one schema
unchanged is the evidence the schema is right.

The FB-level `VAR` rule keys on **section**, not on declared accessibility, because
TC3 has no per-variable access modifier: `PUBLIC`/`PRIVATE`/`PROTECTED`/`INTERNAL`
apply to methods, properties, actions, FBs and interfaces only, and properties are
the encapsulation mechanism precisely because `VAR` members cannot be hidden. So a
plain `VAR` member is treated as the FB's internal state (`_camelCase`) even though
ST leaves it technically reachable from outside, and `VAR_INPUT`/`VAR_OUTPUT`/
`VAR_IN_OUT` remain the intended public surface. `applicable_accessibilities` still
earns its place in the schema, but it selects methods, properties and actions
rather than variables.

**Nothing is fixable in v1.** Ruff's `unfixable` concept applied hard: a naming fix
on referenced code is precisely the operation the rename guard forbids doing
autonomously. We report; the user decides.

**Out of scope.** Formatting (TcBlack and STweep own it, and XAE rewrites the XML
on save), XML hygiene (our writers cannot produce it), and anything the compiler
already catches.

## Alternatives considered

- **Shell out to iec-checker.** OCaml binary, PLCOpen XML input, LGPL, and blind to
  our symbol index. Mine its PLCOpen guideline catalogue as a rule source instead.
- **Depend on twincat-validator-mcp.** A separate MCP server solving a file-level
  problem; users can run both. Its health-score model (0–100, −25/−5/−1) is
  explicitly rejected: aggregate grades invite gaming and give a model nothing to
  act on.
- **Wrap TE1200.** Licensed, XAE-only, per-project, cannot run in CI.
- **blark as the parser.** The only real ST grammar in reach, but Python; wrong
  runtime after ADR-0015. Useful as a grammar reference for the tokeniser.
- **A dedicated config file** (`.tckit-analysis.json`). Rejected: `.editorconfig`
  gives ancestor-walk discovery and per-folder globs for free.
- **CODESYS's "all rules on by default".** Guaranteed noise on a brownfield
  project; Clippy's category-defaults model is better behaved.
- **`hungarian` as the default.** Rejected on migration cost (see Decision) and on
  the type-prefix critique; it stays a first-class supported profile.

## Consequences

**Enables:** offline, unlicensed, CI-able analysis with a cross-PLC view nobody
else has, since only we hold the symbol index plus task bindings from ADR-0005
("this GVL is written from two POUs on different tasks", "this POU is neither
instantiated nor task-bound"). Tight write-loop feedback: `pouName`-scoped analysis
after an `UpdatePouImplementation` is instant and needs no XAE, so it runs strictly
before `Build`. The tokeniser and declaration parser land in Core, which is most of
what ADR-0001's deferred `find_callers` / `find_instantiations` needs.

**Costs:** a genuinely new layer. The reader supplies the corpus and the plumbing
but no ST understanding, so the tokeniser, the declaration parser, and every rule
are new code with new failure modes. False positives are the real risk: an
analyser that cries wolf gets muted, and worse, an agent will "fix" a bogus finding
and break working code. Mitigation is precision over recall, a bench fixture per
rule, and dropping any rule that cannot be made precise without a full symbol table
rather than shipping it noisy.

Under `hybrid` or `dotnet`, project code will look inconsistent beside Beckhoff
libraries, TcUnit and TcOpen, which are Hungarian throughout. The analyser side is
automatic (we only parse project-authored files, never library symbols) but the
visual mismatch is real and permanent. Dropping `FB_` under `dotnet` also costs a
signal TwinCAT's object browser does not replace.

**Locks out:** nothing structurally. Tier C stays available behind the same port if
dataflow is ever wanted, and the fixable-rule door is open once casing-only fixes
are wanted.

## Status notes

- 2026-08-09: Drafted as `Accepted` after a design session that surveyed the field
  (TE1200/CODESYS, twincat-validator-mcp, iec-checker, blark, TcBlack/STweep) and
  the mainstream models (Roslyn/`.editorconfig`, clang-tidy
  `readability-identifier-naming`, typescript-eslint `naming-convention`, Ruff,
  Clippy). Roslyn's schema chosen as the config model. Default profile moved from
  `beckhoff`/Hungarian to `dotnet` and then to `hybrid` during the session, on the
  argument that kind prefixes and type prefixes are separable and only the former
  earns its keep. Verified against IEC 61131-3 that ST identifiers are
  case-insensitive and may lead with an underscore; both facts feed the profile
  design and the "naming is never a correctness rule" stance.
- 2026-08-09: Corrected pre-merge. The draft keyed `hybrid`'s FB-level `VAR` rule
  on declared accessibility; TC3 has no per-variable access modifier, so it keys on
  section instead. `applicable_accessibilities` stays in the schema but selects
  methods, properties and actions only.
- 2026-08-09: Naming lane built. Deviations from the design above, all found by
  running the analyser over the T3 fixture rather than by unit tests:
  - `applicable_scopes` (`object` / `member`) added to the schema. It is not
    optional: the `VAR` keyword is identical on an FB and in a method, but one is
    instance state (`_camelCase`) and the other a local (`camelCase`).
  - A `FUNCTION`'s variables are collected at member scope. A function has no
    instance surface, so its `VAR_INPUT` is a parameter list; without this every
    `F_*` parameter was wrongly flagged as needing PascalCase.
  - `MAIN` is exempt from the program rule. TwinCAT mandates the name, so the
    finding was advising a rename that breaks the project.
  - Suggestion type-prefix stripping is gated on the prefix agreeing with the
    declared type. `strSuite : FB_StringTests` was being suggested as `_suite`,
    losing a word; `str` only strips when the variable really is a STRING.
  - Object kind prefixes are stripped before recasing, so `dotnet` suggests
    `Motor` for `FB_Motor` rather than `FBMotor`.
  These four fixes took the fixture from 21 findings to 12, all legitimate.
  `prefix_composition` / `recursive_type_prefix` dropped from the shipped schema
  rather than accepted-but-ignored, and `infer` warns instead of silently
  returning an empty rule set.
