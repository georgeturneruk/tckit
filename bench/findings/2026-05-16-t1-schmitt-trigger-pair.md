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

### 3. The `tests: 0` blind-spot was a bridge config bug, not a fixture problem

Root-caused after the run: the model's empty `get_test_results`
returns weren't because the xUnit publisher was off; they were
because the **bridge was looking in the wrong directory for the
published XML**.

Chain of evidence:

1. The fixture's TcUnit placeholder had no parameter overrides,
   so I added `GVL_Param_TcUnit.xUnitEnablePublish := TRUE` via
   the bridge's canonical `Set-TcPlcProjPlaceholderParameters`
   PowerShell function (close/edit/open dance).
2. After deploy, ADS reads `GVL_Param_TcUnit.xUnitEnablePublish`
   = "True" and `xUnitFilePath` = "%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml"
   on the live runtime — confirming the override is honoured.
3. `/tcunit-run` still returned `xml_published: false`. But the
   XML file *does* exist, at
   `C:\ProgramData\Beckhoff\TwinCAT\3.1\Runtimes\UmRT_Default\3.1\Boot\tcunit_xunit_testresults.xml`,
   containing full per-test detail (test name, pass/fail, failure
   message).
4. The bridge's `_TcUnit.psm1::Get-TcUnitDefaultXmlPath`
   hardcodes `C:\TwinCAT\3.1\Boot\Plc\Port_$Port\` — that path
   is correct for kernel-mode TcRTime runtimes but **wrong for
   UmRT** (user-mode runtimes), whose boot folder lives under
   `%ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes\<RuntimeName>\3.1\Boot\`
   (no `Plc\Port_<port>\` subdirectory). The function's docstring
   actually describes both layouts but the implementation only
   handles the kernel case and falls back to an env-var override
   (`TCKIT_TCUNIT_XML_PATH`) for everything else.
5. `TCKIT_TCUNIT_XML_PATH` wasn't set in the bridge's environment
   this session.

After setting that env var on the bridge (pointing at the actual
UmRT path) and re-running `/results`, the bridge returns:

```json
{
    "summary": { "suites": 1, "tests": 5, "failures": 5 },
    "suites": [{
        "name": "MAIN.suite",
        "tests": [
            { "name": "LatchesHighAboveHighThreshold", "passed": false,
              "failures": [{ "message": "trigger.Step() with fInput := 0.9 should latch HIGH" }] },
            ... (4 more)
        ]
    }]
}
```

The full per-test detail the model needed all along, present in
the published XML the whole time, just at a path the bridge
didn't know to look at.

**This is the real cause of T1 tckit's 9× cost.** The model saw
empty results from `get_test_results`, couldn't confirm whether
its first implementation worked, and iterated through deploy+run
cycles trying to get useful feedback. With the env var set, the
bench would have read the XML on the first cycle, the model would
have seen the failure messages (or all-pass after its first
edit), and converged in something close to vanilla's call/token
budget.

Two follow-ups worth flagging:

- **`TCKIT_TCUNIT_XML_PATH` isn't documented in `config.toml.example`.**
  It's only referenced inside the bridge harness, with no
  user-facing surface. Adding it to the config template would let
  operators set it once during `tckit init`. (Even better: have
  `tckit doctor` autodetect UmRT and offer to set it.)
- **The bridge could auto-detect** rather than relying on an env
  var. Try the kernel path; if not found, glob
  `%ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes\*\3.1\Boot\<filename>`.
  That removes the operator-side knob entirely for the UmRT case,
  which is the default development setup on most TwinCAT installs.

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

## Hacked-around in this round (not done properly)

Worth fixing properly before the next sweep:

- **`author_T1.py` is out of sync with what's on disk.** The
  suite FB, the 5 test methods, and MAIN's body were authored via
  an ad-hoc Python heredoc this session — not added to
  `author_T1.py`. Running `author_T1.py --force` today would wipe
  the test infra and not re-create it. Same gap exists for B2-B5
  whose author scripts also only know about the library + consumer.
  Mirror `author_B1.py`'s shape (SUITE_FB_CODE, TEST_METHOD_CODE,
  MAIN_DECL, MAIN_BODY constants + the corresponding add_pou /
  add_method / update_pou_declaration / update_pou_implementation
  calls) and the fixtures become regenerable from scratch.
- **`xUnitEnablePublish` parameter was spliced in via a direct
  PowerShell call to the internal `Set-TcPlcProjPlaceholderParameters`
  function**, because `add_library_placeholder` errors with
  "Placeholder 'TcUnit' already contained!" when called on an
  existing placeholder. Future fix: either (a) make
  `add_library_placeholder` idempotent on the AddPlaceholder COM
  call so it falls through to the parameter splice on existing
  placeholders, or (b) expose `Set-TcPlcProjPlaceholderParameters`
  as a first-class bridge route + writer method
  (`set_placeholder_parameters`). New fixtures authored from
  scratch don't hit this — `scaffold_fixture` passes parameters on
  the original placeholder add — so it only matters for
  retrofitting old fixtures.
- **T1's `.plcproj` Parameters block was added via a hand-edit
  with the Edit tool** (minimal 6-line diff), not via the
  bridge's close/edit/open dance. Equivalent on-disk shape, but
  the next time XAE saves the project it'll rewrite the whole
  file (BOM, `<None Include="...tmc">`, `<Data>`/`<TypeList>`
  re-indentation in `XmlArchive`). The canonical path would have
  produced that re-serialised form on commit and avoided the
  noise re-appearing later.
- **Tests 2 and 5 were authored once, then immediately rewritten
  via `update_method_body`** because the initial bodies happened
  to pass on the empty seed `Step` (default `BOOL FALSE` matched
  the assertion targets). Should have written them as sequences
  from the start. Cheap to fix in the regenerable `author_T1.py`
  whenever that lands.
- **`TCKIT_TCUNIT_XML_PATH` was set ad-hoc on the bridge launch
  command** (`$env:TCKIT_TCUNIT_XML_PATH = '...'`). The proper
  fixes are listed under finding 3: document the env var in
  `config.toml.example`, and/or auto-detect UmRT runtimes inside
  the bridge so the env var stops being load-bearing.

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
TDD task — but the bulk of that gap is a bridge config bug
(`TCKIT_TCUNIT_XML_PATH` unset, bridge looking at the kernel-runtime
boot folder instead of the UmRT one), which made `get_test_results`
return nothing and pushed the model into multiple deploy+test
cycles to try to find ground truth; with the env var set the
bridge returns the full per-test detail from the published XML
that was there the whole time. The "writer thesis is invisible
on one-line text edits" conclusion still holds, but the *size* of
the gap on T1 should be heavily discounted until we re-bench
under the fixed config.**
