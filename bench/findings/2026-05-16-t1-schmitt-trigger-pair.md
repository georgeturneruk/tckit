# 2026-05-16 — T1 Schmitt-trigger TDD pair (n=1, isolated)

Second bug-hunting fixture benched. Same harness as the B1 round
(symmetric `--isolate-cwd`, `--inject-skills plugin/skills` on the
tckit arm, `--close-during-run` on both, 5 runtime probes against
each test). **N=1, directional only.**

Both arms went green end-to-end. The pair is much more revealing
than B1 was — T1 is a TDD task where the model writes a non-trivial
method body to satisfy a five-assertion suite, not a one-line text
swap.

## Fixture authoring

`author_T1.py` had the same gap as B2-B5: it only authored the
library FB (`FB_SchmittTrigger`) with an empty `Step` body, plus
the consumer FB. No test suite, no test methods, no MAIN driver —
the fixture as committed had nothing to verify.

Authored via the writer (so .plcproj cross-refs and GUIDs stay
consistent):

- `FB_SchmittTriggerTests` POU (EXTENDS `TcUnit.FB_TestSuite`).
- Five test methods matching TASK.md's spec:
  1. `LatchesHighAboveHighThreshold` — `fInput := 0.9` → expect TRUE
  2. `LatchesLowBelowLowThreshold` — sequence HIGH then LOW, expect each
  3. `HoldsInHysteresisBand` — latch HIGH, then 0.5, expect held TRUE
  4. `SequencedTransitions` — 0.5/0.9/0.5 sequence asserting band logic
  5. `BoundaryValues` — latch HIGH then 0.7 (boundary), then LOW then 0.3
- MAIN: `suite : FB_SchmittTriggerTests; suite(); TcUnit.RUN();`.

Tests 2 and 5 were initially written naively and happened to *pass*
with the unimplemented empty `Step` because the default `BOOL` return
is `FALSE` and the assertions were comparing against `FALSE`.
Restructured as sequences (latch one way first, then test the
boundary behaviour) so the empty-body seed fails all five.

Smoke validated: empty `Step` → all 5 RED. Reference impl
(`IF fInput > fHighThreshold THEN bState := TRUE; ELSIF ... < ...
THEN bState := FALSE; END_IF; Step := bState;`) → all 5 GREEN.

## Results — N=1

Both arms reached green: all five
`MAIN.suite.Tests[i].TestIsFailed` probes read FALSE after the
model's session, harness rebuilt + redeployed + re-ran cleanly,
diff was substantively identical between arms on the `Step` body.

| Task | Config | Calls | Tokens | Wall (s) | Test | Build |
| --- | --- | --- | --- | --- | --- | --- |
| T1-schmitt-trigger | empty (`--isolate-cwd`) | **7** | **2,014** | **36.7** | PASSED | green |
| T1-schmitt-trigger | tckit (`--isolate-cwd --inject-skills`) | **49** | **17,667** | **385.1** | PASSED | green |

Pairwise ratios (vanilla / tckit; <1 means vanilla more efficient):

| Task | Tokens | Wall | Tool calls |
| --- | --- | --- | --- |
| T1-schmitt-trigger | **0.11×** | **0.10×** | **0.14×** |

Vanilla beat tckit by roughly **9-10×** on every metric. The gap
is dramatically larger than B1's 0.65-0.89× and is the most
striking result of the bench round so far.

## Findings

### 1. The TcKit skill's prescribed iterate-via-runtime loop is what costs tckit on T1

**Tckit (49 calls, 17,667 tokens, 385.1s wall):**
`Bash×1, Glob×5, PowerShell×8, Read×13, ToolSearch×2,
mcp__tckit__build×2, mcp__tckit__deploy×2,
mcp__tckit__get_pou_interface×1, mcp__tckit__get_pou_item×1,
mcp__tckit__get_structure×3, mcp__tckit__get_test_results×3,
mcp__tckit__open_project×1, mcp__tckit__run_tests×4,
mcp__tckit__start_runtime×2, mcp__tckit__update_method_body×1`.

The shape jumps out: a single `update_method_body` (the model
landed the fix in one writer call) but two complete build + deploy
+ start_runtime + run_tests + get_test_results cycles, plus a
third partial cycle (run_tests×4 vs build×2 means the model
re-ran tests against state it had already verified). The
`tc-build-test-loop` skill prescribes exactly this iteration
pattern, and on a task where the model needs to converge through
trial-and-error it would be valuable — but on T1 the model could
read the spec, write correct code, and stop. Vanilla did exactly
that. Tckit, instructed by the skill, ran the runtime loop to
empirically verify even though its first impl was correct.

Wall-clock breakdown is dominated by the deploy + runtime cost
(~60-90s per cycle on the cold target). Two full cycles = ~200s
just in deploy/runtime, plus a third partial = ~300s of the
385s total. The actual model thinking + writing was a small
fraction.

**Vanilla (7 calls, 2,014 tokens, 36.7s wall):**
`Bash×4, Edit×1, Read×2`. Read the TASK and the FB, thought
through the spec, wrote the impl, stopped. No `Skill` and no
`ToolSearch` calls (the `--isolate-cwd` exclusion worked
again). The "I don't have run_tests available, I just have to
write correct code" framing turned out to be a feature, not a
limitation.

### 2. The writer thesis is structurally invisible on T1

The W-series predicted that the writer thesis (one MCP call vs
many vanilla edits) would land when vanilla has to fabricate
identifiers or compose structural cross-refs. T1 doesn't trigger
either — `Step` is a method that already exists, the model just
overwrites its body. Vanilla edits the `.TcPOU` XML in place;
tckit calls `update_method_body`. Both are one write.

So the writer SAVINGS on T1 is `Edit (1 call) - update_method_body
(1 call) = 0`. The writer thesis doesn't predict any per-write
overhead on tckit either — they're both "one call to land the
fix". And yet tckit pays 42 EXTRA calls beyond that single write,
all of them coming from the test-loop iteration the skill
prescribes.

This is a real finding for the TcKit project, not just the bench:
**the `tc-build-test-loop` skill is too eager on tasks where the
spec is fully constraining.** It probably needs a "if you're
confident from the spec alone, skip the runtime loop" off-ramp,
or the test-loop should fire only when the *user* asks for it,
not as a default convergence pattern.

### 3. Model-vs-harness discrepancy reproduces (and isn't an artefact)

The tckit arm's `final_text` says its `run_tests` returned
`tests: 0` (no results visible to the model), but the harness's
post-run validation shows all 5 tests GREEN. ADR-0007
specifically flagged this discrepancy class. The model lacked
confidence its impl worked because TcUnit's xUnit XML publisher
isn't enabled by default, so `get_test_results` returned an
empty parsed shape. The harness reads pass/fail from PLC symbols
directly via probes, sidestepping the publisher question — and
that's the authoritative reading.

The model's response was reasonable given its information ("my
test runs returned 0 results, so I'm not sure if the impl
worked"). The wasteful behaviour was the *deploy+run* iteration
that produced those empty results, not the model's caution about
them.

### 4. Both arms wrote essentially identical hysteresis logic

```pascal
IF fInput > fHighThreshold THEN
    bState := TRUE;
ELSIF fInput < fLowThreshold THEN
    bState := FALSE;
END_IF
Step := bState;
```

Five lines, strict inequalities at the boundaries (matching the
`BoundaryValues` test's expectation), `bState` defaults to FALSE
which satisfies `SequencedTransitions`' first step (0.5 from
default).

This is the "shortest correct impl" for a Schmitt trigger given
the spec. The model arrived at it from spec alone on the vanilla
arm; tckit also arrived at it on the first writer call, then
spent the rest of its budget verifying. The correctness ceiling
is the same; the cost-to-arrive-at-correctness is what differs.

### 5. T1 fixture is now usable; B2-B5 still need test infra authored

Same gap. The B2-B5 author scripts only set up the library FB +
consumer, never the suite + tests + MAIN. Whichever next.

## What this validates and invalidates

**Validates:**

- The TDD fixture shape (multi-test suite with multi-assertion
  tests) is exercised end-to-end by the harness, including the
  multi-probe `--test-probe` path on the bench runner.
- Both arms can land a correct method-body implementation on a
  TDD task from the spec alone.
- The `--isolate-cwd` + `--inject-skills plugin/skills`
  symmetric isolation produces clean tool breakdowns on both arms.

**Refines:**

- **The `tc-build-test-loop` skill's iteration discipline is
  net-negative on tasks where the spec is constraining.** The
  W1/B1 lesson that TcKit's value is in *protecting* against
  identifier and cross-ref breakage, not in *teaching* the model
  to iterate, holds even more strongly here. T1 doesn't break
  identifiers (Step already exists) and the spec doesn't need
  iteration. The skill cost ~340s of wall and ~15k tokens to
  re-confirm what was already correct.
- **The writer thesis is bounded by what vanilla *can't* do.**
  On T1, vanilla can do everything (edit XML directly, write
  correct code from spec). TcKit's writer surface neither helps
  nor hurts on the write itself; the skill *layer* is what
  costs.

**Open:**

- **Bigger differentiating tasks**: B3 (state-machine transition
  with multiple states), B4 (bError propagation through wrapped
  FBs) — these may need the model to inspect existing code more
  carefully. If vanilla still wins by 10× there too, that's a
  serious revision to the writer thesis. If tckit closes the gap
  or pulls ahead, the skill loop's value becomes context-dependent
  (worth it on tasks where you can't reason from spec).
- **A `--no-test-loop` skill variant** or an updated
  `tc-build-test-loop` that skips the runtime iteration when the
  model is confident from spec.
- **N=3 sweep** of B1 + T1 to confirm the directional reads
  aren't noise. T1's 10× gap is large enough to be a real signal
  even at N=1, but B1's 0.65-0.89× could move ±20% on re-roll.

## Caveats

- N=1, one model (Opus 4.7), one project, one machine. The 10×
  gap on T1 might be different on a different model that
  ignores the skill's prescribed loop, or on a target runtime
  with faster deploy (the 60-90s deploy cost dominates).
- TcUnit's xUnit XML publisher being off-by-default is what
  produced the model-side "tests: 0" view that drove the
  extra iteration. With the publisher on, the model would see
  proper test results and might stop sooner. Trade-off:
  publisher on adds library-parameter overrides per fixture.
- The five tests in the suite are author-supplied, not
  TASK.md-supplied. The test design choices (sequence ordering,
  assertion granularity, strict-inequality interpretation)
  affect the spec the model has to satisfy. These choices were
  made to match TASK.md's text but they're not the only valid
  way.

## Interpretation, in one line

**Under symmetric hardened isolation, vanilla beat tckit by ~10×
on every metric (0.14× calls, 0.11× tokens, 0.10× wall) on the T1
TDD task because both arms landed the correct hysteresis impl on
their first write, but tckit then followed the `tc-build-test-loop`
skill into a multi-cycle deploy+test verification loop that vanilla
(no skill, no TcKit MCP tools) didn't have the option to do —
showing that the skill's iteration discipline is net-negative on
tasks where the spec is fully constraining, and that the writer
thesis is invisible when both arms can land the fix in one write
regardless of which tool they use.**
