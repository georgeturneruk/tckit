---
date: 2026-05-17
status: Superseded
related_adrs: [0011, 0007]
superseded_by: bench/findings/2026-05-18-t1-friction-fixes-and-skill-nudges.md
---

# 2026-05-17 — ADR-0011 fixes landed, T1 re-benched (n=1)

Implemented the six fixes from
[ADR-0011](../../adrs/0011-tcunit-results-path-resolution-and-cold-start-recovery.md)
on branch `feat/tcunit-self-validation`, then re-ran the T1
Schmitt-trigger TDD bench tckit arm against the local UmRT to see
how the headline 9x gap moves.

## What landed

1. **UmRT XML auto-detect** in `Get-TcUnitDefaultXmlPath` — env override
   then kernel-RT then UmRT glob with mtime tiebreak.
2. **`run_tests` returns failures inline** — the bridge parses the
   xUnit XML on the same call as the run, returns `summary` plus a
   flat `failures` list (passing tests omitted to keep payload
   bounded on large green suites). MCP signature gains
   `wait_for_results=True`.
3. **`add_library_placeholder` idempotency** — file-only probe
   skips the COM `AddPlaceholder` call when the placeholder is
   already present, then runs the parameter splice normally.
4. **`save_plc_as_library` cold-start retry** — catches the
   `PlaceholderReference/EffectiveResolution` exception, runs
   `CheckAllObjects` to trigger placeholder resolution, retries once.
5. **`tckit doctor` TcUnit section** — surfaces auto-detect outcome
   (OK / WARN on multiple UmRT candidates / FAIL on none) via a new
   `POST /tcunit-xml-resolve` bridge route.
6. **`set_placeholder_parameters` MCP route** — dedicated verb for
   "the placeholder exists, only update its parameters" (cleaner
   than overloading `add_library_placeholder` for retrofits).

Plus the `tc-build-test-loop` skill dropped its prescribed
`get_test_results` call on the happy path (run_tests is enough now).

## Direct verification

Against the running UmRT_Default on this machine, before the bench:

- `/tcunit-xml-resolve` found the UmRT path automatically with no
  env var set.
- `tckit doctor` reported `[OK] TcUnit results path - UmRT path
  resolves: ...UmRT_Default...`.
- `add_library_placeholder` on the existing B1 `TcUnit` placeholder
  returned `already_present: true` and spliced parameters in
  canonical XAE shape.
- `set_placeholder_parameters` replaced an existing key value
  in-place (TRUE -> FALSE) and appended a new key without
  duplicating, refused cleanly on a missing placeholder.
- `run_tests` against the buggy B1 fixture returned `failures:
  [{ suite_name: "MAIN.suite", test_name: "AverageOfConstantStream",
  message: "Average of eight 10s should be 10" }]` inline — no
  follow-up `/results` call needed.
- `save_plc_as_library` cold-start retry path executes
  (`cold_start_warmup: false` field present on the warm path); the
  exception itself did not trigger after a fresh XAE kill on this
  machine, so the retry branch wasn't exercised. Recorded as a known
  manual-repro limitation rather than a bench result.

## T1 re-bench result, N=1

Same invocation shape as the 2026-05-16 round (`--isolate-cwd
--inject-skills plugin/skills --close-during-run` plus the
post-run-tests harness) but with the additional bench-side fix
below.

Three rounds of measurement before the result stuck:

| Round | Calls | Tokens | Wall (s) | Notes |
|---|---:|---:|---:|---|
| Old tckit (2026-05-16) | 49 | 17,667 | 385.1 | UmRT XML missing, empty `get_test_results` drove a retry loop |
| After ADR-0011, hand-off mode | 14 | 4,110 | 88.2 | Empty-results loop gone; model handed off to harness, did not self-validate |
| After ADR-0011, self-validate attempt | 48 | 15,188 | 427.2 | `--isolate-cwd` + MCP path mismatch surfaced; model couldn't see its own writes, fell back to raw Edit + four full validation cycles |
| **After bench MCP-lifecycle fix** | **11** | **2,349** | **68.3** | Clean. Model used MCP reader+writer, single `update_method_body` |
| Vanilla (reference) | 7 | 1,904 | 42.0 | |

vs old tckit: **0.22x calls, 0.13x tokens, 0.18x wall.**
vs vanilla: **1.57x calls, 1.23x tokens, 1.63x wall** (down from
7.0x / 8.8x / 10.5x).

Final tool breakdown: `Skill×2, ToolSearch×1,
mcp__tckit__get_pou_interface×2, mcp__tckit__get_pou_item×4,
mcp__tckit__get_structure×1, mcp__tckit__update_method_body×1`. No
Bash/Glob/Read exploration churn, no test-loop iteration, single
canonical writer call. Same 5-line Schmitt-trigger logic on the
diff as every prior round.

## What needed fixing on the bench side

The first ADR-0011 re-bench (14 calls, hand-off mode) looked
impressive but the model never called build/deploy/start_runtime/
run_tests itself — its final message was *"the harness re-runs ...
so I'll hand off there rather than trigger a deploy that would
need your safety-gate approval."* Trying to remeasure with
`SAFETY_CONFIRMATIONS=false` exposed a more interesting bug: the
`--isolate-cwd` mechanism copies the fixture to a temp dir and pins
`claude -p`'s cwd there, but the long-lived MCP server's
`PLC_PROJECT_PATH` still pointed at the real fixture. The model's
`update_method_body` calls went to the real fixture via the bridge
while the model's `Read` calls saw the temp copy — the model could
not observe its own writes and (reasonably) concluded the writer
was broken. It fell back to raw `Edit` on the temp copy, retried
build/deploy/test four times, and eventually found the right
sequence (manual `save_plc_as_library` after editing) to get the
tests green. 48 calls of fighting structural staleness, not a real
self-validation cost.

The fix is in `bench/run.py`: the bench now manages the MCP server
lifecycle per run, spawning a fresh `python -m tckit.server` with
`PLC_PROJECT_PATH` pointing at the temp fixture during the model
session, then killing it before sync-back. `run-pair.ps1`
drops `-StartMcp` (the bench owns lifecycle now) and refuses if
port 8000 is occupied. The bench also extended its sync-back
exclude list to cover `.vs/`, `*.suo`, `_Boot/` etc, which now
appear inside the temp fixture because the bench's own DTE attach
creates them there.

With those fixes the 48-call self-validate run collapsed to 11
calls. The model still hands off rather than self-validating in
this measurement, because the TASK.md tells it the harness handles
validation and the skill's safety-gate language reinforces deferral
in a non-interactive context. But the cost is now genuine writer-
surface cost, not staleness fight.

## What this validates and invalidates

**Validates:**

- All six ADR-0011 fixes work end-to-end against the live UmRT.
- The writer-thesis path (model uses MCP reader + writer surface)
  works cleanly when the bridge actually returns useful data.
- The cost-of-empty-results retry loop is gone: the model no longer
  bashes around with grep / find / read trying to find ground
  truth.

**Refines:**

- "Self-validation" depends on the model being able to act on the
  safety gate. In an interactive session that's fine; in `claude -p`
  it isn't. The skill's caution is correct but needs a per-context
  escape hatch the bench can use without disabling safety globally.
- `--isolate-cwd` is structurally incompatible with a shared MCP
  server if the MCP env points at the real fixture: the model reads
  from temp, writes go to real, and the model can't see its own
  edits. Bench now spawns MCP per-run with the temp path; documented
  in this finding's "What needed fixing" section.

**Open:**

- Get the model to actually self-validate inside the bench (TASK.md
  tweak or skill-level "if you have safety-gate approval, run the
  test loop yourself" off-ramp). Once that lands, the 11-call number
  will rise toward genuine self-validating cost; useful comparison
  point to the 49-call original.
- N=3 sweep once the self-validating shape is stable.

## Caveats

- N=1, one model (Opus 4.7), one machine.
- The B1 end-to-end verification (build + deploy + run_tests with
  inline failures returning the off-by-one message) was done
  manually via curl, not through a bench harness run. Adapter unit
  tests + Pester cover the parser shape; the live curl call
  confirms the chain.
- Fix 4 cold-start retry didn't trigger on this machine in this
  state. The catch-block exists and the field is emitted on the
  warm path; whether the catch fires when the exception happens in
  the wild is unverified here.
