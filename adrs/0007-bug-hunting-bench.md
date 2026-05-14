---
adr: 0007
title: Bug-hunting bench (closed-loop debugging against TcUnit)
status: Proposed
created: 2026-05-12
issue:
pr:
---

## Context

The W-series writer benchmarks (#52, #54, #56) measured TcKit on
synthetic atomic write tasks: change one line, add one variable,
add one method. They confirmed the writer thesis on tokens, calls,
and wall-clock, but they don't say much about whether TcKit helps
Claude in a realistic debugging loop. A real session typically
involves running a failing test, reading the assertion, locating
the bug, patching, and re-running until green. The W series
exercises a fragment of that loop in isolation.

The bug-hunting bench measures the loop end-to-end. It depends on
four pieces of infrastructure: multi-project sln support
(ADR-0005), a working TestRunner adapter (ADR-0006), the portable
TwinCAT CLAUDE.md template (ADR-0008), and multi-PLC sln authoring
+ library tools (ADR-0009). The first three are `Implemented`;
ADR-0009 is the prerequisite that surfaced during this ADR's
planning round — TcKit had no documented way to add a second
`.plcproj` to an existing sln, save it as a library, install it,
or add a library reference. The bench fixtures need all four to
exist before they can be authored.

## Decision

### Fixture layout

One `.sln` per task. Each task lives at
`bench/fixtures/bug-hunting/<id>-<slug>/` and contains:

```
B1-off-by-one/
├── CLAUDE.md              (copy of templates/twincat-claude.md)
├── <id>.sln               (one sln, two .plcproj refs)
├── Library/
│   ├── Library.plcproj    (buggy code under test)
│   └── POUs/
│       └── FB_*.TcPOU
└── Tests/
    ├── Tests.plcproj      (TcUnit harness + failing test)
    ├── GVLs/
    │   └── GVL_TcUnit.TcGVL   (TcUnit-ResultExportXmlPath constant)
    └── POUs/
        └── FB_*Tests.TcPOU
```

Tests references Library as a compiled library: each bench run
calls `mcp__tckit__save_plc_as_library` on Library (per ADR-0009)
to produce a fresh `.library` file and install it into the system
repo, then builds Tests against the installed library. This is
what the IDE effectively does for the user when they press Build
on a Source-Only-referenced sln, just done explicitly through
documented automation interface methods. The original draft of
this ADR specified a TwinCAT 4026 "Source-Only" reference, but
that reference type has no publicly documented automation
interface entry point; the compiled-library path produces
equivalent build behaviour for the bench's purpose and uses only
documented methods. The bench's reset reverts the whole task
folder per run; the generated `.library` file is gitignored and
regenerated per build.

### Task set (initial six)

The six tasks cover a graded set of bug categories on realistic
domain code. FB names are illustrative; final names land with the
fixture-implementation PR.

- **B1 off-by-one.** `FB_RollingAverage.Step` with a
  `FOR i := 1 TO Count DO` that should be `FOR i := 0 TO Count-1 DO`.
  Single method body. Test asserts the average of a known input
  vector; the off-by-one shifts the result by one sample.

- **B2 sign / type.** `FB_Counter.GetSignedDelta` returns a `UDINT`
  where it should be `DINT`; subtraction past zero underflows to
  4 billion. Test asserts a small negative result.

- **B3 state-machine wrong transition.** `FB_TrafficLight.Step`
  with a `CASE` statement where `Green -> Yellow` accidentally
  jumps to `Red`. Test runs the cycle for one full period and
  asserts the sequence of state observations.

- **B4 missing bError propagation.** `FB_PipelineStage` wraps an
  inner FB whose `bError` is set on a known input. The outer FB
  never reads `bInnerFB.bError`. Test asserts
  `pipelineStage.bError = TRUE` after the failing input.

- **B5 wrong default initialisation.** `FB_PIDController.VAR`
  initialises `fGain := 0.0` (should be `1.0`); first call returns
  zero. Test asserts a non-zero output for a non-zero error input.

- **T1 TDD.** `FB_SchmittTrigger.Step` is fully declared (signature,
  VAR_INPUT, VAR_OUTPUT, hysteresis parameters) but its method
  body is `;`. Test suite has multiple assertions covering:
  - input below low threshold -> output FALSE,
  - input above high threshold -> output TRUE,
  - input between thresholds -> output holds previous value,
  - sequenced inputs across the hysteresis band asserting the
    correct transition,
  - boundary values exactly at the thresholds.
  No hardcoded return value can satisfy all five assertions. The
  model has to implement Schmitt-trigger logic.

### Prompt shape

Each task's `.md` gives:

- Failing test suite name (TcUnit test-suite FB name).
- Failing test name (TcUnit `TEST(...)` name).
- Assertion failure message and expected/actual values from a
  baseline harness run.
- One sentence of framing: "this test is failing; modify the
  project source to make it pass. Do not change the test code."
- A note that the test project files (any `*Tests.TcPOU`) are
  read-only for grading purposes.

No FB pointer, no diagnosis hint. Vanilla and TcKit get identical
prompts.

### Vanilla open-loop shape

Vanilla's `bench/configs/empty.json` has no MCP server and no
bridge URL exposed to the model. The model reads the source, the
failing test, and the assertion message; makes its edits; the
session ends naturally when the model stops emitting tool calls.
After session termination, the bench harness — not the model —
builds the sln and runs the test suite via the bridge's
`/tcunit-run` and `/results` routes and writes
`.test-result.json`. The bridge is a harness-side resource; the
vanilla session never sees it. Vanilla gets exactly one
validation cycle. It cannot iterate on test results because it
cannot run the tests.

### TcKit closed-loop shape

TcKit's config exposes the MCP server (`tckit.json`). The model
uses `run_tests` + `get_test_results` between edits and stops
when tests pass or when the `tc-build-test-loop` skill's
5-iteration cap is hit. After the session ends, the harness runs
tests one more time to corroborate the model's last reading.
Discrepancy between "model said pass" and "harness saw fail" is
a finding worth surfacing per task.

### Scoring

- **Pass/fail** per (task, config). Headline metric. Aggregate to
  success rate.
- **Iterations to green** for TcKit (vanilla is always 1).
- **Tokens / calls / wall** same as the W series. Apples-to-apples
  comparison restricted to tasks where both arms reached green.
- **Validation:** harness's post-session test run writes
  `.test-result.json` for both configs. For TcKit this
  corroborates the model's last `get_test_results`; for vanilla
  this is the only validation.
- **Secondary signals (optional):** "vanilla got close" indicator
  showing whether vanilla's edits reduced the number of failing
  assertions, kept them constant, or introduced new failures.

### Bench harness changes

`bench/run.py` gains:

- `--task-folder` flag so a task is a directory (with its own
  sln, sources, and CLAUDE.md), not a single `.md` file.
- Pre-build orchestration step: for every Library PLC project in
  the fixture, call `/save-as-library` (per ADR-0009) so the
  library is freshly installed before the consumer build. This
  is the harness-side mirror of the rule documented in the
  `tc-build-test-loop` skill for the TcKit-arm sessions.
- Post-session validation step: build the sln, run tests via the
  bridge's `/tcunit-run` and `/results` routes, write
  `.test-result.json`. This step uses the longer build-timeout
  envelope (`TCKIT_BUILD_TIMEOUT`-class headroom, ~600s) rather
  than the TestRunner adapter's default 180s HTTP envelope; cold
  XAE + first deploy on a fresh fixture routinely exceeds 180s.
- Test-files tamper guard: after each run, `git -C <repo-root>
  diff --name-only -- <task-folder>/Tests/`. If non-empty, the
  per-run JSON gets `tests_modified: true` and the run is graded
  failed regardless of `/results`. The "read-only for grading"
  rule needs enforcement, not just a polite instruction.
- `bench/aggregate.py` gains a `PASS RATE` column and an
  `ITERATIONS` column derived from the per-run JSONs (counting
  `mcp__tckit__run_tests` events in `tool_breakdown` for TcKit,
  1 for vanilla).

The task-folder flag is additive; the existing single-`.md`
flow keeps working for the W series.

### What does not change

- Reset between runs uses the existing `--reset-cmd` flag with a
  per-task reset command. The task folder lives inside the TcKit
  repo (not an independent git root), so the correct reset is
  path-scoped: `git -C <repo-root> checkout HEAD -- <task-folder>`.
  A naive `git -C <task-folder> reset --hard HEAD` would resolve
  to the TcKit repo and reset unrelated paths in the tree.
- cwd isolation: each `claude -p` runs with cwd pinned to the
  task folder, never the TcKit repo.
- Bridge runs in `XAE_MODE=headless` for autonomy.

## Alternatives considered

- **Many bugs in one sln.** Rejected per the W-series lesson:
  atomic tasks beat noisy aggregates at this scale. Mixed-bug
  projects make the "did Claude succeed" judgement fuzzy and
  hide which categories help vs hurt.
- **Synthetic minimal FBs.** Rejected so the model can't
  pattern-match "this is a benchmark fixture" off the FB shape.
  Realistic-looking domain code is harder to reverse-engineer.
- **Let vanilla bash to msbuild and parse XML.** Rejected as
  breaking the cwd isolation that the W series proved load-bearing.
  Vanilla's open-loop shape is honest: it represents what vanilla
  can do without TcKit on a real TwinCAT codebase, which is
  exactly the comparison we're trying to measure.
- **Multiple TDD tasks, no bug-hunting tasks.** Rejected: bug
  hunting and feature implementation are different debugging
  modes, both common in real sessions, both worth measuring.
- **Score by iterations to green, weighted.** Rejected as
  premature optimisation. Pass/fail and raw iteration counts are
  the right primitives for N=1 directional reads. Weighting
  schemes belong in a later round once the bench is calibrated.

## Consequences

**Enables:** the first end-to-end measurement of TcKit's value in
a realistic closed-loop debugging session. Vanilla baseline tells
us what's reachable without the tooling; TcKit numbers tell us how
much the tooling moves the needle.

**Costs:** authoring six realistic FBs and six TcUnit test suites
is real work. Scoped to a single fixture-implementation PR after
this ADR is accepted. Each task takes maybe 30-60 minutes of
authoring once the patterns are settled.

**Risks:**

- The bench is measurably less precise than the W series: N=1
  success rates are coarse, and the win condition is binary. A
  task that vanilla passes 50% of the time will look "won" or
  "lost" depending on the single roll.
- Authoring realistic-looking FBs that hide a specific bug is
  itself a skill. If the bug is too obvious (or too obscure), the
  task is uninformative. The first round of authoring will
  probably need a second pass after seeing how each task plays.
- The 5-iteration cap means TcKit can "give up" without finding
  the bug. The pass/fail metric records this; it doesn't punish
  TcKit beyond the binary outcome.

**Locks out:** nothing. The fixture layout and prompt shape can
evolve; the harness changes are additive; the W-series surface is
unaffected.

## Status notes

- 2026-05-12: Drafted as `Proposed`. Implementation lands as a
  dedicated PR after ADR-0005 and ADR-0006 are implemented.
  Initial fixture-authoring round will likely produce findings
  that loop back into this ADR's status notes (which bugs were
  too obvious, which prompts needed tightening).
- 2026-05-14: Planning round surfaced a prerequisite the original
  draft assumed but didn't acknowledge: TcKit has no documented
  way to add a second `.plcproj` to an existing sln, save a PLC
  project as a library, install it, or add a library reference.
  Verified against Beckhoff infosys that `ITcSmTreeItem.CreateChild`,
  `ITcPlcIECProject.SaveAsLibrary`, `ITcPlcLibraryManager.InstallLibrary`,
  and `ITcPlcLibraryManager.AddLibrary` are all documented; the
  Source-Only reference type the original draft specified is not.
  Captured the prerequisite work in ADR-0009 and rewrote the
  fixture-layout section here to use a compiled library reference
  with an explicit save+install step before each build instead.
  Equivalent build behaviour, fully documented API. Also corrected
  the `--reset-cmd` example (was wrong for in-repo fixtures), added
  the test-files tamper guard, fixed the MCP-namespaced tool name
  for iteration counting, noted the longer cold-start timeout, and
  clarified that the bridge is harness-side only (vanilla never
  sees it).
- 2026-05-14: Phase C0 (B1 pilot-fixture authoring) landed. The
  Python authoring script drove the full ADR-0009 chain end-to-end
  against a live 4026 install: `create_project` → `add_plc_project`
  → `add_pou` (library) → `add_method` → `save_plc_as_library` →
  `add_library_reference` → `add_pou` (consumer) → `build` on Tests.
  The consumer build resolved the library reference against the
  installed library and completed clean, validating both ADR-0009
  spike-by-implementation defaults (`distributor="Tc3 Project"` and
  the `TIPC^<plc>^<plc> Project^References` tree path). C0 caught
  six bridge bugs total along the way, fixed in PRs #73 (Out-Null
  on COM call outputs, Create-fallback after close) and #74 (Title
  metadata round-trip in `save_plc_as_library`, File.SaveAll after
  `AddLibrary` to persist the reference to .plcproj, retry/backoff
  in `Get-TcSysManager` for the .Object null race, Create-first then
  fall-back-and-retry pattern in `New-TcProject`). Generated tree
  committed under `bench/fixtures/bug-hunting/B1-off-by-one/`.
- 2026-05-14: Phase C0 redone after a deeper bug surfaced.
  The original layout (one PLC-only `.tspproj`, two PLCs stacked
  under one `<Plc>`) authored and built cleanly in memory but
  segfaulted `TcXaeShell.exe` on every `Solution.Open` from disk
  (`AccessViolationException` in `TwinCAT System Manager.x64.dll`
  during `IVsParentProject.OpenChildren()`). Root cause was that
  the PLC-only template doesn't persist the System Manager
  `<Instance>` block for the second PLC; the on-disk `.tspproj`
  ended up as a 4-line skeleton. The bench needs to load fixtures
  from disk on every run, so this was a hard blocker. Fix
  ([#81](https://github.com/georgeturneruk/tckit/pull/81)): one
  full `.tsproj` per PLC, multiple TwinCAT projects per sln,
  `File.SaveAll` after every structural write. Wrapping TwinCAT
  project gets a `_Tc` suffix so its name doesn't collide with the
  PLC's. Details in
  [ADR-0009 status notes](0009-multi-plc-authoring-and-library-tools.md).
  Two more PRs around the same change:
  [#80](https://github.com/georgeturneruk/tckit/pull/80) hardens
  the bridge (COM retries, lazy-load source trees, stale `.~u`
  cleanup); [#82](https://github.com/georgeturneruk/tckit/pull/82)
  inserts empty `VAR/END_VAR` into B3-B5 Step methods to work
  around an `Add-TcMethod` parser gap
  ([#84](https://github.com/georgeturneruk/tckit/issues/84)). All
  six fixtures (B1-T1) re-authored on disk in the new layout and
  build clean against a fresh XAE, verified at the end of the
  session.
- 2026-05-14: **What still needs doing for Phase C0:** the TcUnit
  suite POUs (`FB_*Tests EXTENDS TcUnit.FB_TestSuite` plus a MAIN
  that instantiates the suite and calls TcUnit's run macro) are
  not authored yet. The fixture trees have `FB_<Subject>` (the
  buggy code), `FB_<Subject>Consumer` (forces the library reference
  to actually compile), `GVL_TcUnit` (the
  `TcUnit_ResultExportXmlPath` constant), `MAIN` (template
  placeholder), and the TcUnit placeholder reference. The MAIN
  bodies need replacing with a suite instantiation + run, and a
  suite FB needs adding per fixture. Once that lands, `/tcunit-run`
  has something to exercise and the runtime smoke can produce the
  expected `failures >= 1` on each buggy fixture.
- 2026-05-14: Known harness issues found while doing this work,
  filed for follow-up but not blocking Phase C0 completion:
  - [#84](https://github.com/georgeturneruk/tckit/issues/84):
    `Split-TcCode` silently treats a method body as declaration when
    there's no `END_VAR`. Current workaround is empty `VAR/END_VAR`
    in author scripts; proper fix is to detect a `METHOD`/`FUNCTION`
    signature line and split there.
  - [#85](https://github.com/georgeturneruk/tckit/issues/85):
    Authoring chain intermittently flakes mid-run with
    `DTE.Solution null` or `StubSyncLock`. Manual retry clears it;
    bench/run.py for Phase C1 should be robust against it.
