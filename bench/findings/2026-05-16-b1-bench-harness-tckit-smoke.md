# 2026-05-16 — B1 bench harness end-to-end + tckit n=1 directional

First closed-loop bench round on a bug-hunting fixture. The B1 off-by-one
fixture in `bench/fixtures/bug-hunting/B1-off-by-one/` finally drove the
chain end-to-end with the model in the loop on the tckit arm.
**N=1, directional only.** Vanilla didn't land in this session
(environmental XAE wedge, see findings); pair comparison will come in a
follow-up.

## What changed since ADR-0007 was last touched

1. **`bench/run.py` gained the closed-loop orchestration.** Three new
   flags — `--pre-save-as-library`, `--post-run-tests`, `--tests-guard-path`
   (and `--test-probe` for non-B1 probe sets) — plus a `.test-result.json`
   sibling. The pre-save step flushes seeded source through
   `save_plc_as_library` so the consumer build resolves against the
   buggy library; the post-run cycle re-saves, builds, deploys,
   starts the runtime, runs tests, and reads pass/fail from PLC symbols
   (same probes as `smoke_B1.py`).
2. **B1 fixture had two committed bugs.** `MAIN.TcPOU` body in the
   tests project was empty (no `suite : FB_RollingAverageTests;`,
   no `suite(); TcUnit.RUN();`) and `FB_RollingAverageTests.TcPOU`
   was an orphan file on disk with no `<Compile Include>` entry in
   `RollingAverageTests.plcproj`. Both were `author_B1.py` writes
   that landed in XAE but never got committed back. Fixed in this
   round.
3. **Gitignore for `.tmc` and `_CompileInfo_Upload/`** added — they
   appear after every build and were noise in `git status`.

## Setup

- **Bridge:** native, attached to operator's TcXaeShell (`XAE_MODE`
  unset; headless mode flaked repeatedly on this machine, see findings).
- **TcKit MCP server:** native, `python -m tckit.server --transport sse`
  with `PLC_PROJECT_PATH` pointing at the B1 sln.
- **Target:** local UmRT_Default, `TARGET_AMS_ID = 127.0.0.1.1.1`.
- **Model:** Opus 4.7.
- **Runs:** 1.

Pre-flight `smoke_B1.py` ran red → patch → green clean before the
bench, confirming the chain works end-to-end with the fixture fixes
in place.

## Results — tckit arm, N=1

| Metric | Value |
| --- | --- |
| Tool calls | 17 |
| Total tokens | 3,461 |
| Wall clock | 79.2s |
| Test on runtime | **PASSED** (`MAIN.suite.Tests[1].TestIsFailed = False`) |
| Build | Green |
| Diff | One-hunk change on `FB_RollingAverage.Step` |

Diff matches the canonical fix: `FOR i := 1 TO sampleCount` →
`FOR i := 0 TO sampleCount - 1`.

Tool breakdown: `Bash×3, Glob×2, Read×2, Skill×1,
mcp__tckit__get_pou_interface×1, mcp__tckit__get_structure×3,
mcp__tckit__open_project×2, mcp__tckit__update_method_body×3`.

The model used `update_method_body` three times (a couple of
intermediate attempts before landing the fix) and a handful of
read-side calls to orient. No defensive build/deploy from the
model itself; the harness handled all validation.

Model's final answer (`final_text`):

> Fix applied. Changed `FOR i := 1 TO sampleCount` to
> `FOR i := 0 TO sampleCount - 1` in `FB_RollingAverage.Step` so
> the loop sums valid indices `samples[0..7]` rather than
> overshooting to the never-written `samples[8]`. With eight tens
> the sum is now 80, average 10.

## Findings

### 1. The closed-loop bench works as designed

The full chain works: reset (no-op if clean) → pre-save library →
spawn `claude -p` with cwd pinned to fixture → model edits source via
writer → post-run cycle re-saves library, builds tests PLC, deploys,
starts runtime, runs tests, reads probes → `.test-result.json` sibling
records each step. The four-sibling output (`json`, `md`, `diff`,
`test-result.json`) is what ADR-0007 specified, minus the
`aggregate.py` pass-rate column which is deferred.

The model is in the loop — this is materially different from
`smoke_B1.py` which patches deterministically. The bench is now
measuring what TcKit + the model can do on a realistic debugging
task, not what a hard-coded patch sequence can do.

### 2. The tamper guard false-positives on XAE serialiser noise

The first invocation pointed `--tests-guard-path` at the whole
`RollingAverageTests_Tc/` directory. XAE rewrote
`RollingAverageTests.plcproj` and `RollingAverageTests_Tc.tsproj`
during the save/build/deploy cycle (adding `<None Include="...tmc">`,
re-indenting `XmlArchive`, hydrating task config). The diff was
non-empty, so the guard flipped `passed: false` even though the
runtime test passed and the model never touched test code.

Two lessons:

- **Narrow the guard path to `<TestsPlc>/POUs/` only.** XAE writes
  to `.plcproj`/`.tsproj` are management churn, not model edits to
  test code. README runbook updated to the narrower path.
- **The probes are the real signal.** `TestIsFailed = False` is
  what we mean by "the model fixed it"; `passed: false` from the
  guard alone is a flag, not a verdict.

### 3. XAE state recovery is the real fragility

Three distinct XAE wedges hit during this session:

- **`Microsoft Visual Studio Appid Stub SyncLock`** on first build
  in `XAE_MODE=headless`. XAE's build subsystem hadn't initialised
  yet; subsequent calls returned "no Solution object" because the
  failed build broke the COM session.
- **`TcXaeShell DTE has no Solution object`** after `git checkout`
  reverted files underneath an open XAE. XAE detects external mods
  and quietly drops `DTE.Solution` until the operator reloads the
  project from inside XAE. Repeat-rate is 100% on this machine.
- **`Failed to set library metadata: XmlAutomationException in path
  TreeItem/IECProjectDef/References/PlaceholderReference/EffectiveResolution`**
  on the first `save_plc_as_library` after a fresh XAE restart.
  EffectiveResolution gets populated by XAE's placeholder resolver
  after a successful build; before that, it's null and the save
  serialiser chokes. Build can't run because SyncLock. Catch-22.

The first two are documented in ADR-0010 and ADR-0007 status notes
(under different shapes). The third is new in this round — it's
what blocked the vanilla arm this session. Workaround that
empirically worked: do `add_pou` + `add_method` + `build` on a
project that has a missing POU (the fixture fix work I did this
session) BEFORE the first `save_plc_as_library`. That sequence
warms the placeholder resolver. Without it, save fails cold.

Two follow-ups worth flagging:

- **Bench harness `--reset-cmd` will keep tripping XAE's
  "modified externally" prompt** if used between runs. For N>1
  sweeps the harness will need to drive the seed-bug-rewrite via
  the writer (going through XAE) rather than `git checkout`,
  or programmatically reload the project per iteration via the
  bridge. Single-run benches are fine.
- **Save-as-library cold-path** is worth a bridge-side
  investigation. If `save_plc_as_library` could trigger a build or
  placeholder resolution itself before serialising metadata, the
  catch-22 disappears. Tracked here, not filed as an issue yet.

### 4. The B1 fixture had two unauthored bugs

Two `author_B1.py` writes never made it into the committed fixture:

- **`MAIN.TcPOU` was empty.** No suite instance, no `TcUnit.RUN()`
  call. The deployed PLC had no test driver, so `AllTestSuitesFinished`
  never flipped and probes timed out at 120s.
- **`FB_RollingAverageTests.TcPOU` was an orphan.** The file
  existed on disk with the right content, but `RollingAverageTests.plcproj`
  had no `<Compile Include>` entry for it. XAE compiled the project
  without the suite type, so `MAIN.suite : FB_RollingAverageTests` had
  nothing to resolve to (visible as a build error when the user opened
  the project in XAE; invisible to the orphan-file-on-disk reader).

Both fixed by re-authoring through the writer; the .plcproj manifest
gained the missing Compile reference and MAIN got its body back.
Lesson: an author script that goes through XAE writes to the .plcproj
and .TcPOU AT THE SAME TIME; committing the result needs to capture
BOTH. A partial commit (only the .TcPOU, not the .plcproj) produces
the orphan-file shape we hit here. Worth verifying B2-B5/T1 don't
have the same issue.

### 5. Token / call shape on a closed-loop task

17 calls / 3,461 tokens / 79s on B1 is meaningfully bigger than W3's
2 calls / 508 tokens / 15s (the writer-only smoke). That's expected
— the model has to read source to find the bug before it can fix
it. The 3x `update_method_body` calls suggest a couple of patch
attempts; one of those is likely the actual fix landing on the
third try, with the first two being either path-finding or
verification edits. The full event log is in
`bench/results/TASK__tckit__20260516T185014Z__run1.json` (gitignored,
local-only).

## What this validates and invalidates

**Validates:**

- The closed-loop bench harness wires together end-to-end through
  TcKit + the bridge + XAE + the runtime. The four-sibling
  output format is honest.
- `update_method_body` (patch-style, post-PR-#93 split) works on
  a real debugging task and lands a buildable fix.
- The B1 fixture, with both unauthored bugs fixed, is a
  legitimate target for this and future B-series bench rounds.

**Refines:**

- The tamper guard needs a narrower path target (POUs subdirectory)
  to avoid XAE serialiser FPs.
- N>1 sweeps need a non-`git checkout` reset strategy to avoid
  the XAE external-mod wedge.

**Open:**

- **Vanilla arm not run** — XAE cold-state save-as-library wedge.
  Workaround exists (manual warmup), but it's a real friction that
  ADR-0007's "vanilla gets the same prompt, harness drives
  validation" model didn't anticipate. The vanilla arm needs to be
  bench-cold-resilient since it can't warm XAE with TcKit writes.
- **N=3 sweep** of B1 (or B1-B5/T1) — gated on vanilla landing
  and the reset strategy fix.
- **B2-B5/T1 fixture audit** — do any of them have the same
  orphan-file or empty-MAIN shape B1 did?

## Caveats

- N=1, one model (Opus 4.7), one project, one task, one machine.
- The tckit arm ran AFTER the fixture fixes landed in this session;
  the model saw the corrected fixture, not the as-of-yesterday
  committed shape.
- The XAE wedge is environmental (this Windows machine + this XAE
  version); a different operator might not hit the cold-state
  save-as-library failure. Bear that in mind when interpreting the
  "vanilla blocked" claim.

## Interpretation, in one line

**B1's closed-loop bench harness ships, the fixture's two unauthored
bugs are fixed, and the tckit arm landed a green N=1 (test passed on
runtime, 17 calls / 3.5k tokens / 79s); vanilla is parked on an XAE
cold-start save-as-library wedge that doesn't reproduce after a few
warmup writes — to be unblocked in a follow-up.**
