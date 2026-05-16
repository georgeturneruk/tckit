# 2026-05-16 — B1 bench harness end-to-end + n=1 pair

First closed-loop bench round on a bug-hunting fixture. The B1 off-by-one
fixture in `bench/fixtures/bug-hunting/B1-off-by-one/` drove the
chain end-to-end with the model in the loop, both arms. **N=1,
directional only.**

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
4. **`POST /close` bridge route + `--close-during-run` flag**. Mirrors
   the close/edit/reopen pattern `Add-TcLibraryPlaceholder` uses for
   library parameter overrides. Required for the vanilla arm: the
   model edits `.TcPOU` XML directly without MCP writer tools, which
   would otherwise trigger XAE's "modified externally" prompt and
   wedge `DTE.Solution` for the rest of the session. The bench
   closes the solution around `claude -p` and re-opens before the
   post-run cycle's save-as-library / build / deploy / test steps.

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

## Results — N=1

Both arms reached green: the runtime test (`MAIN.suite.Tests[1].TestIsFailed`)
read FALSE after the model's edits, the harness rebuilt + redeployed +
re-ran tests cleanly, and the produced diff was byte-identical between
arms on the bug fix itself.

| Task | Config | Calls | Tokens | Wall (s) | Test | Build |
| --- | --- | --- | --- | --- | --- | --- |
| B1-off-by-one | empty | **9** | **2,815** | **51.6** | PASSED | green |
| B1-off-by-one | tckit | **17** | **3,461** | **79.2** | PASSED | green |

Pairwise ratios (vanilla / tckit; >1 means TcKit more efficient):

| Task | Tokens | Wall | Tool calls |
| --- | --- | --- | --- |
| B1-off-by-one | 0.81× | 0.65× | 0.53× |

**Vanilla won on every metric.** That is the expected shape for a
one-line ST edit on a comment-tracked region, where vanilla's `Edit`
on `.TcPOU` XML is a single direct write and TcKit's
`update_method_body` round-trips through the bridge per call. Same
story as the W1 smoke (1.21× tokens / 1.21× wall / 1.00× calls in
TcKit's favour, but tiny absolute deltas) — the writer thesis lives
in tasks where vanilla has to fabricate identifiers (W3-style
`add_method` with GUID handling), not in single-line replacements
where vanilla's text-edit path is direct and TcKit's is the one
paying overhead.

### Tool breakdown

**Vanilla (9 calls):** `Edit×1, Glob×3, Read×2, Skill×1, ToolSearch×2`.
One Edit landed the fix. The bulk of the budget went to orientation
(Glob×3 to find files, Read×2 to read the FB and task) and tool
discovery (ToolSearch×2). Skill was the tc-write-st rules surface.

**TcKit (17 calls):** `Bash×3, Glob×2, Read×2, Skill×1,
mcp__tckit__get_pou_interface×1, mcp__tckit__get_structure×3,
mcp__tckit__open_project×2, mcp__tckit__update_method_body×3`.
Three `update_method_body` calls (some iteration before landing
the fix), three `get_structure` for orientation, an `open_project`
pair, and a `get_pou_interface` read. The MCP path is "richer" but
also wider for a task this small.

### Model's final answer

Vanilla:

> Fixed. The loop in `FB_RollingAverage.Step` was iterating indices
> `1..sampleCount` (i.e. 1..8), missing index 0 and including index 8.
> Samples are written starting at `nextIndex = 0`, so with eight 10s
> stored at indices 0–7 the loop summed indices 1–7 plus a zero at
> index 8: `70/8 = 8`. Changed to `FOR i := 0 TO sampleCount - 1 DO`
> so it sums the actual occupied slots 0..7, giving `80/8 = 10`.

TcKit:

> Fix applied. Changed `FOR i := 1 TO sampleCount` to
> `FOR i := 0 TO sampleCount - 1` in `FB_RollingAverage.Step` so
> the loop sums valid indices `samples[0..7]` rather than
> overshooting to the never-written `samples[8]`. With eight tens
> the sum is now 80, average 10.

Both correct, both terse, both attribute the bug to the same cause.
Diffs are byte-identical on the substantive `Step` body change.

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
  serialiser chokes. Build can't run because SyncLock. Catch-22 on
  the very first call after a cold start, but doesn't reproduce
  once XAE is warm.

The first two are documented in ADR-0010 and ADR-0007 status notes
(under different shapes). The third is new in this round — it
blocked the vanilla arm on the first attempt. Workaround that
empirically worked: do `add_pou` + `add_method` + `build` on a
project that has a missing POU (the fixture fix work this session)
BEFORE the first `save_plc_as_library`. That sequence warms the
placeholder resolver. Once warm, save-as-library works through
the rest of the bench. The eventual vanilla success came after
this warmup had happened from the tckit arm's earlier work.

Bridge-side follow-up worth investigating:
`save_plc_as_library` could detect an unresolved-placeholder state
and trigger a build or placeholder resolution itself before
serialising metadata, closing the catch-22 for cold-spawn callers.
Not blocking the bench since the cold-start window is short and
the n=1 happens once the workspace is warm.

The `--reset-cmd` external-mod wedge is fully solved by the
`/close` + `--close-during-run` mechanism, where applicable. For
N>1 with the tckit arm (where the model uses writer tools, not
raw Edit), the seed-bug-rewrite between iterations may still
need to go through the writer rather than `git checkout` to keep
XAE in sync. One iteration per bench invocation works today.

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

- **B1 doesn't move the writer thesis.** TcKit lost on every
  metric. As predicted, single-line text edits are vanilla's
  comfort zone; TcKit's value is gated on tasks where vanilla has
  to fabricate something (W3-style method add, or a B-task where
  the fix needs a new POU). B2 (`UDINT` → `DINT` return-type
  change) and B5 (default-init value change) are still close to
  text edits; B3-B4 may differentiate more.
- **N=3 sweep** of B1-B5/T1 once the fixture audit lands.
- **B2-B5/T1 fixture audit** — do any of them have the same
  orphan-file or empty-MAIN shape B1 did? Worth checking before
  the sweep so we don't burn credits on broken fixtures.
- **`.diff` sibling captured working-tree-wide changes**, not
  just the project subtree. The reader sees the model's fix plus
  any in-flight harness/README edits in the same diff. The
  `capture_git_diff(args.tcunit_path)` call needs to be scoped
  with `-- <relative-path>` to keep the artefact focused.

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

**B1's closed-loop bench harness ships, both arms went green
end-to-end on N=1, and the writer thesis isn't visible on this
particular task — vanilla edited the `.TcPOU` XML directly in one
`Edit` call and beat TcKit on every metric (0.81× tokens, 0.65×
wall, 0.53× calls), exactly as the W1 round predicted: the writer
thesis lives in tasks where vanilla has to fabricate identifiers,
not single-line replacements.**
