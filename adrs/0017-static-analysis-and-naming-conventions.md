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
structure, 4xxx metrics reserved but unused). Configuration is
`.editorconfig`-shaped and follows the Roslyn schema (`tckit_naming_symbols` /
`tckit_naming_style` / `tckit_naming_rule`, plus
`tckit_analyzer_diagnostic.category-<name>.severity`). Five naming profiles:
**`hybrid` (default)**, `dotnet`, `hungarian`, `infer`, `none`.
Scope rule: only ship rules the TwinCAT compiler does not already catch. Nothing
is auto-fixable in v1.

**Where it lives:** Naming, correctness and structure lanes implemented; PR open.
`TcKit.Core/Analysis` (`StSource` masker, `DeclarationParser`, `StIdentifiers`,
DTOs), `TcKit.Core/Ports/IProjectAnalyser.cs`,
`IProjectReader.GetPouSourceAsync`, `dotnet/src/TcKit.Adapters.Analysis`
(config loader, profiles, `NamingRuleEngine`, `CorrectnessRules`),
`TcKit.Server/Tools/AnalysisTools.cs`. Twelve rules ship: `TCK1001`-`TCK1004`
naming and `TCK1005` redundant type prefix; `TCK2001` FB instance on a call
stack, `TCK2002` REAL equality, `TCK2003` misplaced retention, `TCK2004` unused
local, `TCK2005` unread input; `TCK3001` multi-writer global, `TCK3002`
unreachable POU. Correctness defaults to `warning`, naming and structure to
`suggestion`. All five profiles are live, `infer` included
(`ProfileInference.cs`). `TCK2005`/`TCK3001`/`TCK3002` and `infer` need the whole
solution and are skipped (and reported in `rules_not_run`) when `objectName`
scopes the run. `tc-write-st` runs a scoped pass after each write;
`tc-build-test-loop` runs a project pass before the first build. The same
analyser is exposed as `tckit analyse` for CI, with `--fail-on`, text output in
the compiler location format, and a baseline file so the check can be adopted on
a codebase without fixing everything first.

**Open questions:**
- `prefix_composition` and `recursive_type_prefix` were specified below but are not
  in the shipped schema. `hungarian` enforces type prefixes only, with no scope
  composition (`gbEnable`) and no recursion into `POINTER TO POINTER`. `infer`
  learns a scope prefix and a type prefix separately and composes them, so the
  capability exists there; lifting it into the declarative schema is unfinished.
- Finding location is reported as `(pou, item, line-within-item)`. Whether to also
  compute a real file line in the `.TcPOU` is deferred until a consumer needs it.
- Casing-only corrections are reference-safe (ST identifiers are case-insensitive),
  so they could become the first fixable rules. Deliberately deferred past v1.
- Rules still unbuilt from the original list: pointer dereference with no validity
  check, unbounded loop in a cyclic POU, unchecked array index, missing `SUPER^()`
  in an override. Each needs either scope analysis or a heuristic that could not be
  made precise, so none shipped rather than shipping noisy.
- `TCK3001` sees qualified writes only. Unqualified global access would need
  shadowing analysis; the rule under-reports instead.
- SARIF output would give GitHub code-scanning annotations and PR-inline comments
  for free. `--format text` covers the log case; SARIF is the obvious next format
  and nothing in the shape of `AnalysisResult` blocks it.
- Non-ST implementation languages (LD, FBD, SFC, CFC, IL) are out of scope by
  decision, not oversight. `TCK3002` stands down on a project containing any, and
  no further support is planned.

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
- 2026-08-09: Correctness and structure lanes built (`TCK2001`-`TCK2005`,
  `TCK3001`-`TCK3002`), and the skills wired to call the tool. Notes:
  - The streaming symbol list was replaced by a retained `AnalysedProject`.
    Cross-file rules need every POU's declarations and masked bodies at once,
    which is the thing a per-file validator structurally cannot do.
  - A scoped run (`objectName`) cannot see enough for the cross-file rules, so
    they are skipped and named in `rules_not_run`. Skipping them silently would
    make a partial pass read as clean.
  - An unimplemented stub is exempt from the unused-declaration rules. Running
    over the T1 fixture flagged all three inputs of a deliberately empty TDD
    skeleton, which is noise in exactly the workflow the bench exists to test.
  - Rule scope was cut twice for precision: `TCK2004` covers locals only,
    because TC3 leaves FB `VAR` members externally reachable so an unused one may
    be API; `TCK3001` matches qualified writes only, because unqualified global
    access needs shadowing analysis.
  - Across all four bench fixtures the correctness lane reports one finding, and
    it is genuine. The guards, not the rules, are what took it there.
- 2026-08-09: Validated against TcUnit (75 objects), TcUnit-Verifier and TcOpen
  (87 objects). This found five defects that 642 passing unit tests and four
  in-repo fixtures did not, and is now the standard this feature is held to: the
  fixtures are too small and too self-authored to exercise real ST.
  - **`TCK2001` was far too broad.** It flagged every function block on a call
    stack. Declaring a synchronous helper local to one method is a correct and
    very common idiom (TcUnit builds an `FB_XmlControl`, uses it and asserts
    inside a single call), so this produced 18 false positives on TcUnit alone.
    Narrowed to instances that genuinely cannot work on a stack: the standard
    stateful blocks (`TON`, `R_TRIG`, `CTU`…) and function blocks with a
    `Busy`/`Done` handshake. Now zero findings on all three projects.
  - **A pre-existing reader bug.** `TcFileParser.DetectPouType` substring-matched
    the whole declaration, so TcUnit's `PRG_TEST`, which declares
    `WriteProtectedFunctions : FB_WriteProtectedFunctions`, read as a `FUNCTION`.
    That cascaded into 36 wrong findings. Fixed to a line-anchored, word-bounded
    keyword match over masked text. `GetStructure` was wrong about this too.
  - **`FB_init` parameters were being renamed.** `bInitRetains` and `bInCopyCode`
    are matched by name by the compiler, so the advice broke the build.
    `FB_init`/`FB_exit`/`FB_reinit` join `MAIN` as reserved.
  - **`infer` emitted rules its own sample violated.** TcUnit names 182 methods
    like `AssertArrayEquals_BOOL`, which a first-character test reads as
    PascalCase; the resulting rule was then failed by two thirds of the sample it
    came from. Inference now verifies a candidate style against its own evidence,
    retries allowing `_` as a word separator, and emits nothing if it still does
    not hold. Also added the missing constant slot, without which SCREAMING_SNAKE
    constants were judged against the instance-field convention.
  - **Suppression comments were specified but never built.** `TCK2002` correctly
    flags the deliberate exact-equality fast path in TcOpen's `IsNearlyEqual`.
    With no way to record an exception in code, the only choices were disabling
    the rule or ignoring the tool. `// tckit-disable-next-line TCK2002` and the
    trailing `-line` form now work, with comma-separated ids or a bare form.

  Net effect on TcUnit: 1360 findings to 370 under `infer`, with the remainder
  spot-checked as genuine (unused locals appearing exactly once in their file,
  inputs never read, globals with four writers). TcOpen: 302 to 286.

- 2026-08-09: Re-run at scale, after discovering the first clones were truncated
  by Windows path limits: TcOpen was 60 of its 510 POU files, so the earlier pass
  covered 12% of it. Corpus is now TcOpen (510 POUs, 1156 objects),
  TcUnit (79), TcUnit-Verifier (29) and TwinCat-Dynamic-Collections (59), cloned
  with `core.longpaths`.
  - **No crashes and no skipped objects** anywhere in the corpus, and TcOpen's
    1156 objects analyse in 2.7 s. Performance is not a concern at this scale.
  - **`TCK1005` produced mangled suggestions.** TcUnit declares its test subjects
    as `aDINT : ARRAY OF DINT` and `bBool : BOOL`; the leading letter reads as a
    type prefix, but stripping it leaves the type name, which recases to `dINT`.
    124 such findings on TcUnit. A variable whose remainder is its own type name
    is now left alone, with a boundary test so `nIntervalMs : INT` still reports
    (`Int` is followed by a lower-case letter, so it starts a word rather than
    being one). `TCK1005` drops to 0 on TcUnit and 14 on TcOpen, and every
    survivor is the intended case: `iSize` to `size`, `pParent` to `parent`.
  - `NameChecker` also lower-cased only the first character of an all-capitals
    word for camelCase, turning `BOOL` into `bOOL`. Acronyms are now lowered whole.
  - A suspected duplicate-findings bug turned out not to be one: TcOpen copies a
    test-context template into each driver package, so seven identical findings
    were seven distinct files. Checking before "fixing" mattered.
  - `infer` at scale: TcOpen 4859 findings to 1544, TcUnit 1640 to 686,
    TwinCat-Dynamic-Collections 1499 to 445. The remainder is genuine drift from
    each project's own convention.

- 2026-08-09: CI surface added, because the analyser was only reachable through
  MCP and so needed a client to run at all. `tckit analyse` carries the same
  analyser; exit 2 for findings keeps "the code has problems" distinct from
  "the tool fell over", which stays 1 in line with every other verb. Text output
  uses the compiler location format so log parsers annotate it for free.
  The baseline is the part that decides whether this ever gets switched on:
  TcOpen reports 1544 findings, nobody fixes those before enabling a gate, and a
  gate nobody enables never runs. Fingerprints exclude the line number so an
  unrelated edit above a finding does not fail the build. Verified end to end on
  TcOpen: record 136 warnings, re-run clean, remove one baseline entry, exactly
  that finding returns and the exit code goes to 2. The repo's own CI now runs
  the verb over a fixture, which also demonstrates that offline analysis works on
  a Linux runner.

  All POUs across the corpus are ST, so **non-ST implementation languages
  remain untested**. That is a real hole rather than a theoretical one: only ST
  is stored as readable source, so a call made from a Ladder or FBD network is
  invisible and `TCK3002` would report anything called only from one as dead.
  `PouSource.Language` now records the implementation language and `TCK3002`
  stands down on any project containing a non-ST body, reporting why in
  `rules_not_run`. The body-scanning rules fail safe in the other direction:
  they under-report rather than inventing findings. Validating against a real
  Ladder-heavy project is still outstanding.
- 2026-08-09: `infer` and `TCK1005` built, closing the last two gaps.
  - `infer` learns type prefixes per type family and capitalisation plus scope
    prefixes per declaration kind, separately, because a Hungarian project uses
    the same type prefix in every VAR block while still casing an input
    differently from a local. Below three samples or sixty per cent agreement a
    slot yields no rule, and a style with neither a prefix nor a capitalisation
    requirement is dropped rather than emitted as an unfailable rule.
  - `TCK1005` fires only on names that already conform. Emitting it alongside a
    casing finding double-reported one defect with one fix; restricting it to
    conforming names is exactly the gap it was specified to close.
  - `StIdentifiers` needed a second boundary rule. `Mentions` rejects a preceding
    dot so `fb.count` is not a use of a local `count`, but that made every
    library POU consumed as `LibraryPlc.F_Trim` look dead: seven of thirteen
    objects in the T3 fixture. `MentionsQualified` counts the qualified form, and
    `TCK3002` uses it throughout. The fixture now reports zero unreachable
    objects, and its total is 13 findings, all legitimate.
