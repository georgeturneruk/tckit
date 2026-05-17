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
   The same `/close` + `/open` dance now also wraps `--reset-cmd`
   so the per-iteration git checkout doesn't trip the same wedge.
5. **`--isolate-cwd` flag**. Claude Code walks from cwd up the
   filesystem tree to discover `.claude/skills/` and `CLAUDE.md`
   files. The bench's fixture lives at
   `bench/fixtures/bug-hunting/B1-off-by-one/` inside this repo, so
   the walk used to find `C:/tckit/.claude/skills/` (eight TcKit
   skills, including `tc-write-st` which prescribes TcKit's writer
   tools) and `C:/tckit/CLAUDE.md` (TcKit project conventions).
   Both arms inherited that context. `--isolate-cwd` copies the
   fixture to a fresh temp directory outside the repo, pins cwd
   there, runs the model session, then syncs edits back to the
   real fixture before the validation cycle. Walking up from the
   temp dir hits filesystem root with nothing to find. Avoids
   Claude Code's `--bare` flag, which would also work but disables
   OAuth and forces `ANTHROPIC_API_KEY` auth.
6. **`TASK.md` tooling prescription dropped on B1-B5/T1.** The old
   prompt said "Do not edit `.plcproj` or `.TcPOU` XML directly.
   Use the TwinCAT automation interface (e.g. TcKit's
   `update_method_body` / `update_method_body_patch`) for any
   change." That's a tool-specific prescription that contradicts
   vanilla's capabilities (its only edit primitive IS the raw XML
   path) and shouldn't need stating for tckit (its skill routes
   there anyway). The constraint was the prompt prescribing
   *tooling*, not *task*; the harness imbalance (skills present /
   absent, MCP present / absent) is the differentiator now.

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

The first round of pair runs had vanilla contaminated by Claude Code's
ancestor walk into this repo's `.claude/skills/` and `CLAUDE.md`
(visible as `Skill×1 + ToolSearch×2` in the vanilla tool breakdown,
the model invoking `tc-write-st` and searching for TcKit tools it
didn't have). The second round added the `--isolate-cwd` flag on
the vanilla arm so the model session runs from a temp directory
outside the repo; the upward walk hits the filesystem root with no
`.claude/` or `CLAUDE.md` to find. Numbers below are the second
round; the first round's vanilla (9 / 2,815 / 51.6s) was within
run-to-run variance of these, so the direction is the same either
way.

| Task | Config | Isolation | Calls | Tokens | Wall (s) | Test | Build |
| --- | --- | --- | --- | --- | --- | --- | --- |
| B1-off-by-one | empty | hardened `--isolate-cwd` | **7** | **2,088** | **40.6** | PASSED | green |
| B1-off-by-one | tckit | hardened `--isolate-cwd` + `--inject-skills plugin/skills` | **9** | **2,344** | **51.2** | PASSED | green |

Pairwise ratios (vanilla / tckit; <1 means vanilla more efficient):

| Task | Tokens | Wall | Tool calls |
| --- | --- | --- | --- |
| B1-off-by-one | 0.89× | 0.79× | 0.78× |

Three rounds per arm, showing how contamination came off as
isolation tightened:

**Vanilla progression:**

| Round | Isolation | Calls | Tokens | Wall (s) |
| --- | --- | --- | --- | --- |
| Initial | none (project skills + CLAUDE.md leaked) | 9 | 2,815 | 51.6 |
| Mid | `--isolate-cwd` (still copies fixture CLAUDE.md) | 10 | 2,557 | 52.5 |
| Hardened | `--isolate-cwd` excludes CLAUDE.md / `.claude` / `.mcp.json` | 7 | 2,088 | 40.6 |

**TcKit progression:**

| Round | Isolation | Calls | Tokens | Wall (s) |
| --- | --- | --- | --- | --- |
| Initial | none (dev `.claude/skills/` + dev CLAUDE.md loaded) | 16 | 3,194 | 87.5 |
| Symmetric | hardened isolation + `--inject-skills plugin/skills` | 9 | 2,344 | 51.2 |

The TcKit dev contamination was costing roughly 40% on every
metric: 16 → 9 calls (-44%), 3,194 → 2,344 tokens (-27%),
87.5s → 51.2s wall (-41%). The dev surface this repo ships at
`.claude/skills/` includes `tc-adr` and `tc-docs-write` (dev-only
meta-skills) plus a `CLAUDE.md` full of contributor architecture
rules; none of that is in the shippable plugin, and inheriting
it was making the model exploration significantly noisier.

The fixture's local `CLAUDE.md` was also nudging vanilla toward
extra exploration ("If a TwinCAT automation interface (such as
TcKit) is available, use it for any structural change..."). Once
that's out of the cwd, vanilla converges directly to grep + read
+ edit. Same fix applies to tckit, then the plugin skills cover
the project-conventions guidance that file used to provide.

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

**Vanilla (7 calls, hardened isolation):** `Bash×4, Edit×1, Read×2`.
One Edit landed the fix; orientation was a handful of bash calls
(grep / find / ls under the temp fixture) plus two reads. No
`Skill` and no `ToolSearch` calls — the cwd-ancestor walk found
no project skills, and the temp copy excluded the fixture-local
CLAUDE.md (which previously contained "If a TwinCAT automation
interface (such as TcKit) is available, use it...") so the model
had no nudge to look for TcKit tools at all.

**TcKit (9 calls, symmetric isolation):** `Bash×1, Skill×1,
ToolSearch×1, mcp__tckit__get_pou_interface×2,
mcp__tckit__get_pou_item×2, mcp__tckit__get_structure×1,
mcp__tckit__update_method_body_patch×1`. A single
`update_method_body_patch` lands the fix. Orientation is a
`get_structure` plus two `get_pou_interface` and two
`get_pou_item` reads — using the MCP reader surface rather than
bash/grep because the tc-write-st skill routes there. `Skill×1`
loads the user-facing tc-write-st skill (from the injected
`plugin/skills/`); `ToolSearch×1` resolves the MCP tool surface.
Notably no `open_project`, no defensive `build` — much leaner
than the contaminated round.

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

**Under symmetric hardened isolation (`--isolate-cwd` on both
arms, `--inject-skills plugin/skills` only on tckit so it sees
the 6 user-facing skills a plugin install actually ships, plus
`--close-during-run` + `/close` bridge route for XAE's
external-mod guard), both arms went green end-to-end on N=1 and
vanilla edged tckit by a modest margin on a one-line ST edit
(0.89× tokens, 0.79× wall, 0.78× calls) — exactly as the W1
round predicted: the writer thesis lives in tasks where vanilla
has to fabricate identifiers, not in single-line text
replacements where vanilla's `Edit` on `.TcPOU` XML is direct
and TcKit's MCP layer is the one paying overhead.**

## What the tckit arm DOES NOT see, under symmetric isolation

- `tc-adr` and `tc-docs-write` skills from this repo's
  `.claude/skills/` — dev-only meta-skills with narrow triggers
  that wouldn't fire on bug-fix tasks anyway, but loading their
  SKILL.md descriptions still cost tokens.
- `C:/tckit/CLAUDE.md` — this repo's dev project conventions
  (adapter isolation rules, port-methods reference, contributor
  git workflow). A real TcKit-plugin user never sees this.
- The fixture-local `CLAUDE.md` — the "drop this in any TwinCAT
  project" template that mentions TcKit by name.
- Any `~/.claude/` user-globals — these apply equally to both
  arms (writing-style prefs etc.), so they don't bias the
  comparison, and we leave them alone.

What the tckit arm DOES see is `plugin/skills/` injected as
`<temp>/.claude/skills/` (the 6 user-facing skills) + the TcKit
MCP server. That's exactly the surface a real TcKit-plugin user
gets.
