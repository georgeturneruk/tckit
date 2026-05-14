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
- 2026-05-14: Phase C0 (B1 pilot-fixture authoring) kicked off.
  Authoring script + fixture scaffolding (CLAUDE.md copy, TASK.md
  prompt, .gitignore, README) landed; the script drove the bridge
  end-to-end and surfaced two bridge bugs in the ADR-0009 surface
  on the first live run — `New-TcProject.ps1` and
  `Add-TcPlcProject.ps1` were not suppressing the COM-method return
  values from `Solution.Create` / `AddFromTemplate` / `SaveAs`, so
  the harness returned a JSON array instead of an object and
  `to_result` raised `AttributeError: 'list' object has no
  attribute 'get'`. Fixed both scripts to pipe the offending calls
  to `Out-Null` and made `New-TcProject.ps1` defensively close any
  attached solution before `Create`. The committed generated
  fixture tree (`.sln`, `.plcproj`, `.TcPOU`) is gated on an
  operator-driven bridge restart to pick up the new ADR-0009 routes
  + the bug fixes; the script is ready to run once that happens.
