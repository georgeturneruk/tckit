---
adr: 0007
title: Bug-hunting bench (closed-loop debugging against TcUnit)
status: Proposed
created: 2026-05-12
last_reviewed: 2026-05-18
issue:
pr:
related: [0005, 0006, 0008, 0009, 0010, 0011, 0012]
---

## Current state

**Decision (live):** One `.sln` per task under `bench/fixtures/bug-hunting/<id>-<slug>/`,
each with a library + TcUnit-tests `.plcproj` split. Per-run authoring goes
through the writer MCP tools (`save_plc_as_library` per ADR-0009; `add_property`
/ `add_dut` per ADR-0012). Bench arms run with `--isolate-cwd` (vanilla and
tckit) + `--inject-skills plugin/skills` (tckit only) so each arm sees only
the surface a real plugin install ships. `--close-during-run` wraps `claude -p`
so XAE's external-mod guard doesn't wedge on reset or raw XML edits.

**Where it lives:** `bench/run.py`, `bench/fixtures/bug-hunting/`. B1
(off-by-one) and T1 (Schmitt-trigger TDD) green end-to-end on N=1. T2-pid
authored, awaiting bench run. B2-B5 fixture test-infra still to author.

**Open questions:**
- B2-B5 author scripts only seed the library FB + consumer; suite + tests +
  MAIN need adding before bench rounds can run on them.
- N=3 sweep on B1 + T1 + T2 once author scripts are regenerable from scratch.
- Whether the `tc-build-test-loop` skill iterates too eagerly on tasks where
  the spec is fully constraining (T1 lesson, partially addressed by ADR-0011
  inline failures; revisit after the T1 re-bench round closes).

## Context

The W-series writer benchmarks (#52, #54, #56) measured TcKit on synthetic
atomic write tasks: change one line, add one variable, add one method.
They confirmed the writer thesis on tokens, calls, and wall-clock, but
they don't say much about whether TcKit helps Claude in a realistic
debugging loop. A real session typically involves running a failing test,
reading the assertion, locating the bug, patching, and re-running until
green. The W series exercises a fragment of that loop in isolation.

The bug-hunting bench measures the loop end-to-end. It depends on four
pieces of infrastructure: multi-project sln support (ADR-0005), a working
TestRunner adapter (ADR-0006), the portable TwinCAT CLAUDE.md template
(ADR-0008), and multi-PLC sln authoring + library tools (ADR-0009). The
first three are `Implemented`; ADR-0009 is the prerequisite that surfaced
during this ADR's planning round (TcKit had no documented way to add a
second `.plcproj`, save it as a library, install it, or add a library
reference).

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
    └── POUs/
        └── FB_*Tests.TcPOU
```

Tests references Library as a compiled library: each bench run calls
`mcp__tckit__save_plc_as_library` on Library (per ADR-0009) to produce a
fresh `.library` file and install it into the system repo, then builds
Tests against the installed library. This is what the IDE effectively
does for the user when they press Build on a Source-Only-referenced sln,
just done explicitly through documented automation interface methods.
(The original draft of this ADR specified a TwinCAT 4026 "Source-Only"
reference, but that reference type has no publicly documented automation
entry point; the compiled-library path produces equivalent build behaviour
using only documented methods.) The generated `.library` file is
gitignored and regenerated per build.

### Task set (initial six)

Six tasks cover a graded set of bug categories on realistic domain code.
FB names are illustrative.

- **B1 off-by-one.** `FB_RollingAverage.Step` with a
  `FOR i := 1 TO Count DO` that should be `FOR i := 0 TO Count-1 DO`.
- **B2 sign / type.** `FB_Counter.GetSignedDelta` returns a `UDINT` where
  it should be `DINT`; subtraction past zero underflows to 4 billion.
- **B3 state-machine wrong transition.** `FB_TrafficLight.Step` with a
  `CASE` where `Green -> Yellow` accidentally jumps to `Red`.
- **B4 missing bError propagation.** `FB_PipelineStage` wraps an inner FB
  whose `bError` is set on a known input; the outer FB never reads
  `bInnerFB.bError`.
- **B5 wrong default initialisation.** `FB_PIDController.VAR` initialises
  `fGain := 0.0` (should be `1.0`); first call returns zero.
- **T1 TDD.** `FB_SchmittTrigger.Step` is fully declared but its method
  body is `;`. The suite has five assertions across thresholds, hysteresis
  hold, transitions, and boundary values. No hardcoded return value
  satisfies all five.

### Prompt shape

Each task's `.md` gives the failing test suite name, the failing test
name, the assertion message and expected/actual values, one sentence of
framing ("modify the project source to make this test pass; do not change
the test code"), and a note that `*Tests.TcPOU` files are read-only for
grading. No FB pointer, no diagnosis hint. Vanilla and TcKit get identical
prompts.

### Bench arms

Vanilla's `bench/configs/empty.json` exposes no MCP server. The model
reads the source and the assertion, makes edits, the session ends when it
stops emitting tool calls; the harness then builds and runs tests via the
bridge's `/tcunit-run` and `/results` routes (the bridge is harness-side
only, the vanilla session never sees it). Vanilla gets exactly one
validation cycle.

TcKit's config exposes the MCP server. The model uses `run_tests` +
`get_test_results` between edits and stops when tests pass or when the
`tc-build-test-loop` skill's 5-iteration cap is hit. The harness runs
tests one more time post-session to corroborate the model's last reading;
discrepancy between "model said pass" and "harness saw fail" is a finding
worth surfacing.

### Scoring

- **Pass/fail** per (task, config). Headline metric.
- **Iterations to green** for TcKit (vanilla is always 1).
- **Tokens / calls / wall** same as the W series; apples-to-apples
  restricted to tasks where both arms reached green.
- **Validation:** harness's post-session run writes `.test-result.json`.
- **Secondary signal:** "vanilla got close" indicator (reduced / unchanged
  / increased failing assertions vs baseline).

### Bench harness changes

`bench/run.py` gains:

- `--task-folder` flag so a task is a directory (sln + sources +
  CLAUDE.md), not a single `.md` file.
- Pre-build orchestration: for every Library PLC project, call
  `/save-as-library` (per ADR-0009) before the consumer build.
- Post-session validation: build the sln, run tests via `/tcunit-run` and
  `/results`, write `.test-result.json`. Uses the longer build-timeout
  envelope (~600s); cold XAE + first deploy routinely exceeds the
  TestRunner adapter's 180s HTTP default.
- Tamper guard: `git -C <repo-root> diff --name-only -- <task-folder>/Tests/`
  after each run. Non-empty -> `tests_modified: true` and the run grades
  failed regardless of `/results`.
- `bench/aggregate.py` gains `PASS RATE` and `ITERATIONS` columns
  (counting `mcp__tckit__run_tests` events in `tool_breakdown` for TcKit,
  1 for vanilla).

The task-folder flag is additive; the single-`.md` flow keeps working for
the W series.

### What does not change

- Reset uses `--reset-cmd` with a per-task, *path-scoped* command:
  `git -C <repo-root> checkout HEAD -- <task-folder>`. The task folder
  lives inside the TcKit repo; a naive `git -C <task-folder> reset --hard`
  would resolve to the TcKit repo and reset unrelated paths.
- cwd isolation: each `claude -p` runs with cwd pinned to the task
  folder, never the TcKit repo.
- Bridge runs in `XAE_MODE=headless` for autonomy.

## Alternatives considered

- **Many bugs in one sln.** Rejected per the W-series lesson: atomic
  tasks beat noisy aggregates at this scale.
- **Synthetic minimal FBs.** Rejected so the model can't pattern-match
  "this is a benchmark fixture" off the FB shape.
- **Let vanilla bash to msbuild and parse XML.** Rejected as breaking
  the cwd isolation the W series proved load-bearing.
- **Multiple TDD tasks, no bug-hunting tasks.** Rejected: bug hunting
  and feature implementation are different debugging modes, both common
  in real sessions, both worth measuring.
- **Score by weighted iterations to green.** Rejected as premature
  optimisation. Pass/fail and raw counts are the right primitives for
  N=1 directional reads.

## Consequences

**Enables:** the first end-to-end measurement of TcKit's value in a
realistic closed-loop debugging session.

**Costs:** authoring six realistic FBs and six TcUnit suites is real
work; scoped to a single fixture-implementation PR after this ADR is
accepted. ~30-60 minutes per task once patterns settle.

**Risks:**

- N=1 success rates are coarse; a task vanilla passes 50% of the time
  reads "won" or "lost" on the single roll.
- Authoring realistic-looking FBs that hide a specific bug is itself a
  skill. The first round probably needs a second pass after seeing how
  each task plays.
- The 5-iteration cap means TcKit can "give up" without finding the bug.
  The pass/fail metric records this; it doesn't punish TcKit beyond the
  binary outcome.

**Locks out:** nothing. The fixture layout and prompt shape can evolve;
harness changes are additive; the W-series surface is unaffected.

## Status notes

- 2026-05-12: Drafted as `Proposed`. Implementation lands as a dedicated
  PR after ADR-0005 and ADR-0006 are implemented.
- 2026-05-14: Planning round surfaced ADR-0009 as a prerequisite (TcKit
  had no documented way to add a second `.plcproj`, save as library,
  install, or add a library reference). Fixture layout rewritten to use
  a compiled-library + explicit save+install step. Reset semantics,
  tamper guard, namespaced tool name, and cold-start timeout corrected
  in the same pass. Clarified that the bridge is harness-side only
  (vanilla never sees it).
- 2026-05-14: Phase C0 (B1 pilot-fixture authoring) landed in PRs #71-#74
  and was reworked in #80-#82 after a layout bug surfaced. The full
  multi-tsproj layout reversal is in ADR-0009's status notes.
  - **Lesson:** A PLC-only `.tspproj` template doesn't persist the
    System Manager `<Instance>` block for additional PLCs. The on-disk
    file ends up as a 4-line skeleton; in-memory looks fine, but XAE
    segfaults on `Solution.Open` (`AccessViolationException` in
    `TwinCAT System Manager.x64.dll`). Use one full `.tsproj` per PLC,
    multiple TwinCAT projects per sln.
  - **Lesson:** `Add-TcMethod` silently treats a method body as part of
    the declaration when there's no `END_VAR`. Tracked in
    [#84](https://github.com/georgeturneruk/tckit/issues/84); workaround
    is empty `VAR/END_VAR` in author scripts. Proper fix is to detect
    a `METHOD`/`FUNCTION` signature line and split there.
- 2026-05-14: B1 closed-loop smoke green end-to-end on a live 4026
  install (UmRT_Default, `TARGET_AMS_ID = 127.0.0.1.1.1`). Five real
  bridge bugs fixed in the same change set.
  - **Lesson:** TcUnit's runner lives at `GVL_TcUnit.TcUnitRunner` (not
    `TcUnit.GVL_TcUnit.TcUnitRunner`); the ADS symbol tree drops the
    placeholder prefix even though source references include it. The
    finished flag is `AllTestSuitesFinished` and the global counter
    TcUnit exposes is `NumberOfInitializedTestSuites`.
  - **Lesson:** `ActivateConfiguration()` leaves the PLC
    loaded-but-stopped until manual Login + Start; a stopped PLC serves
    no symbols on 851 (every read returns "Target doesn't provide
    symbolic information"). Need `BootProjectAutostart = $true` +
    `GenerateBootProject($true)` on `TIPC^<plc>` before activate,
    matching the IDE's "Autostart boot project" tick. `Invoke-TcDeploy.ps1`
    now does this unconditionally.
  - **Lesson:** Deploy + start_runtime are build-class latencies on a
    cold target (ActivateConfiguration + bootapp regen can hit 90-300s).
    Use `build_timeout()` (~600s default), not the 60s HTTP default.
  - **Lesson:** Adapter methods that talk to the bridge must send
    `ProjectPath` in their payload. The bridge's deploy/start_runtime
    handlers reject empty `ProjectPath` with a clear error;
    `XaeComBuilder.deploy` and `start_runtime` originally skipped it.
    Use `_with_target_and_plc` consistently.
  - **Lesson:** PowerShell 5.1's advanced-function machinery silently
    garbles a splatted parameter literally named `Probes`. Renamed to
    `ReadSymbols`; root cause undiagnosed (tracked under ADR-0010
    section B.3). When adding a new bridge route param, avoid one-word
    names that collide with PS built-in vocabulary.
- 2026-05-15: xUnit publisher enabled via library parameters; full
  schema retelling is in ADR-0010 section A.2.
