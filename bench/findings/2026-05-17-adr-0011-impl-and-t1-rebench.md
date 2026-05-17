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
post-run-tests harness).

| | Old tckit T1 (2026-05-16) | **New tckit T1** | Vanilla T1 (unchanged) |
|---|---:|---:|---:|
| Calls | 49 | **14** | 7 |
| Tokens | 17,667 | **4,110** | 2,014 |
| Wall (s) | 385.1 | **88.2** | 36.7 |
| Test | PASSED | PASSED | PASSED |
| Build | green | green | green |

vs old tckit: 0.29x calls / 0.23x tokens / 0.23x wall.
vs vanilla: dropped from 7.0x / 8.8x / 10.5x to **2.0x / 2.0x /
2.4x**.

Tool breakdown shifted dramatically:

| Old | New |
|---|---|
| `Bash×1, Glob×5, PowerShell×8, Read×13` | (none — model used MCP reader surface throughout) |
| `build×2, deploy×2, start_runtime×2, run_tests×4, get_test_results×3` | (none — see caveat below) |
| `update_method_body×1` | `update_method_body×1` |
| `get_pou_interface×1, get_pou_item×1, get_structure×3, open_project×1` | `get_pou_interface×2, get_pou_item×6, get_structure×1` |

Same canonical 5-line Schmitt-trigger implementation. The Bash /
Glob / Read exploration churn that drove the old run's cost is gone:
the model went straight to the MCP reader+writer surface.

## Caveat: the bench arm did NOT self-validate

The model never called `build`, `deploy`, `start_runtime`, or
`run_tests` itself. Its final message: *"Per the prompt, the harness
re-runs save_plc_as_library and the test loop, so I'll hand off
there rather than trigger a deploy that would need your safety-gate
approval."* The PASSED status came from the bench's `--post-run-tests
SchmittTriggerTests` harness step, not the model.

Two factors pushed the model to hand-off rather than self-validate:

- The fixture's TASK.md tells the model the harness validates.
- The `tc-build-test-loop` skill's safety-gate language *"wait for
  explicit approval in chat"* is doing its job: in a `claude -p`
  non-interactive session there is no chat approval available, so
  the conservative path is hand-off. `ALLOWED_NETIDS=127.0.0.1.1.1`
  was set on the MCP server's env, which would have bypassed the
  gate, but the model didn't know and didn't probe.

So the 9x -> 2x drop is real and partly comes from fixing the
empty-results retry loop, but it's measured under conditions where
the model isn't self-validating. To answer "does the new tckit
self-validate cheaper than the old one?", the bench needs to either
disable the safety gate explicitly for the headless arm or tell the
model in the task / skill that the local target is pre-approved.

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

**Open:**

- Re-bench with the model actually deploying (set `ALLOWED_NETIDS`
  in the env the model session inherits, plus a TASK.md tweak
  telling the model the local target is pre-approved) to measure
  self-validating cost vs the old 49-call baseline.
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
