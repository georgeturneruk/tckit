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
two pieces of infrastructure that this design assumes are in
place: multi-project sln support (ADR-0005) and a working
TestRunner adapter (ADR-0006). It also depends on the portable
TwinCAT CLAUDE.md template (ADR-0008) being available so the
bench fixtures can carry a real copy at each sln root.

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

Tests is a linked-library reference to Library, using TwinCAT
4026's "Source-Only" reference so building Tests picks up
Library source changes without a save+install cycle. The bench's
`--reset-cmd` reverts the whole task folder per run.

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

Vanilla's config has no MCP server and no bridge URL. The model
reads the source, the failing test, and the assertion message;
makes its edits; the session ends naturally when the model stops
emitting tool calls. After session termination, the bench harness
builds the sln and runs the test suite via the TestRunner adapter
(ADR-0006) and writes `.test-result.json`. Vanilla gets exactly
one validation cycle. It cannot iterate on test results because
it cannot run the tests.

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
- Post-session validation step: build the sln, run tests via the
  bridge's `/tcunit-run` and `/results` routes, write
  `.test-result.json`.
- `bench/aggregate.py` gains a `PASS RATE` column and an
  `ITERATIONS` column derived from the per-run JSONs (counting
  `run_tests` calls for TcKit, 1 for vanilla).

The task-folder flag is additive; the existing single-`.md`
flow keeps working for the W series.

### What does not change

- Reset between runs uses the existing `--reset-cmd` flag with a
  per-task reset command (`git -C <task-folder> reset --hard HEAD`).
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
