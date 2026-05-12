# 2026-05-12 — Writer-bench harness + W1 smoke

First write-path bench. ADR-0003's patch primitives landed in #52; the bench harness was extended in #53 to cover writer tasks (reset between runs, pre-bench `/open`, `git diff` capture, post-run build via the bridge). W1 is the smoke task: change one comment line on `FB_TestSuite.AssertEquals_INT`. Same target as the reader benches (TcUnit), same model (Opus 4.7), native bridge + tckit MCP server on Windows, N=1.

## What changed since the post-#46 bench

1. **ADR-0003 primitives shipped (#52).** `update_pou_item_patch` (Edit-style anchored replacement), `add_variable` (insert one VAR line into a named scope), `get_pou_declaration` (FB-level VAR sections only). All three exercised here through the bench MCP config; W1 only uses `update_pou_item_patch`.
2. **`tc-write-st` skill tightened (#52).** Tool-selection table, substitution language, anti-pattern callouts mirroring the #48 tightening of `tc-read-project`.
3. **Writer-bench harness landed (#53).** `bench/run.py` gained `--reset-cmd`, `--sln-path`, `--build-after-each`, `--bridge-url`. Pre-bench `POST /open`. Post-run `git diff` capture (always on) and bridge `/build` capture (opt-in). `aggregate.py` surfaces per-pair build success rate in a new column.
4. **Three smoke-derived fixes also in #53.** Bridge body parser (Windows PowerShell 5.1 broke on `ConvertFrom-Json -AsHashtable`, which is 7+ only). `/open` client timeout bumped to match `/build` (cold-spawn of XAE via `XAE_MODE=headless` exceeded the default 60s). Spawned `claude -p` runs with `cwd` pinned to the target project so vanilla can't `Read` the TcKit repo and discover the bridge URL.
5. **W1 task added** (`bench/tasks/W1-patch-one-line.md`). Comment-only edit on `FB_TestSuite.AssertEquals_INT`; single unique anchor; build-safe by construction.

## Setup

- **Target:** `C:/TcKit-bench/TcUnit-writer/` (fresh clone of TcUnit, `origin` removed for push safety).
- **Configs:** `empty` (vanilla Claude Code) and `tckit` (TcKit SSE on `http://localhost:8000/sse`).
- **Bridge:** native PowerShell, `XAE_MODE=headless` (bridge spawned its own TcXaeShell).
- **TcKit MCP server:** native, `python -m tckit.server --transport sse` with `PLC_PROJECT_PATH` set.
- **Model:** Opus 4.7.
- **Runs:** 1 per (task, config) for each iteration of the smoke. **N=1, directional only.**
- **Reset between runs:** `git -C C:/TcKit-bench/TcUnit-writer reset --hard HEAD && git ... clean -fd`.
- **Build verification:** `POST /build` after each run.
- **`cwd` isolation:** every `claude -p` runs from the target project, not the TcKit repo.

## Results (final, post-prompt-trim, cwd-isolated)

| Task                | Config | Tool calls | Total tokens | Wall (s) | Build |
| ------------------- | ------ | ---------- | ------------ | -------- | ----- |
| W1-patch-one-line   | empty  | 5          | 1,082        | 26.5     | OK    |
| W1-patch-one-line   | tckit  | 5          | 891          | 21.8     | OK    |

Pairwise ratios (vanilla / tckit; >1 means TcKit more efficient):

| Task                | Tokens | Wall  | Tool calls |
| ------------------- | ------ | ----- | ---------- |
| W1-patch-one-line   | 1.21x  | 1.21x | 1.00x      |

Diffs were **byte-identical** between configs (one `+`/`-` pair at `FB_TestSuite.TcPOU:2839`, comment line only). Both builds green, no warnings.

## Tool breakdown

**W1 / tckit (5 calls):** `Glob ×1, ToolSearch ×1, mcp__tckit__open_project ×1, mcp__tckit__get_pou_item ×1, mcp__tckit__update_pou_item_patch ×1`. The patch-primitive path the skill points at: orient, open, read the anchor target, patch.

**W1 / empty (5 calls):** `Grep ×2, Read ×1, Edit ×2`. The Edit×2 is the interesting one — see finding #3.

## Findings

### 1. Harness end-to-end is honest now (cwd-isolated, all four siblings produced)

The first smoke attempts produced misleading numbers because the spawned `claude -p` inherited the bench operator's working directory (the TcKit repo). Vanilla read the harness scripts, discovered the bridge URL, and `curl`-ed `/item-patch` directly — landing the same patch as TcKit but via Bash. After pinning `cwd` to the target project, vanilla goes back to `Edit` on the `.TcPOU` XML. The cwd pin is now load-bearing for the writer bench; documented as such in `bench/README.md`.

Two other harness bugs surfaced and are fixed in #53: the Windows PowerShell 5.1 body-parser silently dropped all bridge request params (`SolutionPath required.` on every `/open`); the `/open` client timeout was too short for cold XAE spawn (~2 min on this machine).

### 2. W1 is the wrong task to demonstrate TcKit's writer value (by design)

The smoke ratio of 1.21x on tokens and wall is real but small. That is expected: a comment-only edit on a tracked text region doesn't exercise any of the protections TcKit's writer surface adds. Vanilla can `Edit` the `.TcPOU` XML directly, the GUID stays intact, the `.plcproj` cross-references are not affected, the build is green.

W1's purpose is to confirm the harness, not the writer thesis. The writer thesis lives in W3 (add a method, where vanilla has to invent a fresh GUID and TwinCAT's `<Method Id="...">` tracking starts mattering). W2 (add a VAR_INPUT) is intermediate — vanilla can edit the declaration block, but the model has to identify the right scope and insertion point.

### 3. Vanilla's `Edit ×2` reveals a soft-failure retry pattern

Vanilla's two `Edit` calls had identical inputs. The first succeeded (file changed on disk); the second hit "old_string not found" because the change was already in place. The model handled the second error without further retry, but the wasted call counts against the tool-call metric.

TcKit's `update_pou_item_patch` has hard-fail semantics (errors on 0 or >1 anchor matches), which forces the model to *re-anchor* on a different string rather than re-execute the same operation. The soft-success of stock `Edit` ("0 changes made") is easy for the model to misread as "this didn't take, try again". Worth keeping an eye on as W2/W3 land — if vanilla retries cost more on harder tasks, the gap will widen.

### 4. The prompt-trim worked exactly as predicted

The original W1 prompt asked the model to "confirm the new comment is in place" after the change, which forced TcKit to spend a second `get_pou_item` call verifying its own write. With the verification ask removed (handled at the harness level by `.diff` and `.build.json`), TcKit dropped from 6 → 5 calls and 1,004 → 891 tokens. Vanilla was unaffected because its verification path was already inline-with-Edit, not a separate call. Lesson for W2/W3 prompts: don't ask the model to do work that the harness produces as artefacts.

### 5. `open_project` is probably redundant per-run

TcKit spent 1 of 5 calls on `mcp__tckit__open_project`. The bench harness already POSTs `/open` before the run loop, so the solution is already loaded when `claude -p` starts. The model defensively calls `open_project` anyway, because the tool description doesn't tell it the open is implicit. Bridge-side `Open-TcSolution` is idempotent, so the cost is one round-trip, not a real re-open — but it's a free call to claw back. Two ways to address: tighten the writer-tool descriptions ("project must be open; assume it is"), or make `open_project` a no-op when the requested path is already loaded. Not in scope for the W2/W3 bench round, but flagging.

## What this validates and invalidates

**Validates:**

- **ADR-0003 primitives compose end-to-end** through the MCP -> bridge -> COM -> XAE -> disk path. `update_pou_item_patch` produced an identical-to-vanilla diff and a green build.
- **Harness instrumentation is correct.** All four sibling files produced per run, `aggregate.py` surfaces the new BUILDS column without breaking the existing reader-only result set.
- **cwd isolation is necessary** for the writer bench. Without it the bench measures the wrong thing on vanilla.
- **Build-verification is the right second line.** Both configs produced a valid edit *and* a buildable project, captured automatically in `.build.json`. We have a referee in place for W3.

**Refines:**

- W1's smoke ratio (1.21x) is honest but small. The post-#52 expectation of large writer wins is gated on harder tasks. W3 is where to spend the next round of credits.
- The "vanilla can just `Edit` the XML" path is real and works on comment edits. TcKit's value isn't in protecting against syntax breakage on trivial edits; it's in protecting against structural breakage on POU/method/variable additions where vanilla has to fabricate identifiers.

**Open:**

- W2 and W3 not yet benched. Predictions: W2 ratio similar to W1 (modest); W3 ratio noticeably larger and possibly accompanied by a vanilla build failure when the invented GUID collides with TwinCAT's tracking. Both unmeasured.
- Per-call breakdown of vanilla's retry behaviour. If `Edit` soft-failure prompts retries on harder anchors too, the gap may scale with task difficulty rather than stay flat.
- `open_project` redundancy on the TcKit side — flagged in finding #5, low-effort fix.

## Caveats

- N=1 across one task at one prompt. Token deltas below ~15% should not be treated as a signal, and 1.21x is right at that floor.
- One model (Opus 4.7).
- One project (TcUnit, ~50 POUs).
- W1 is comment-only by design; results do not generalise to write tasks that touch structure or identifiers. That is the explicit reason for staging the W series.
- Bridge runs in `XAE_MODE=headless` here; an operator with XAE already open would skip the ~2-minute cold spawn but the bench numbers themselves are unaffected by it (one-off, pre-loop).

## Suggested next experiments

1. **W2 (add_variable) bench.** Same target, same setup, single-variable add to `FB_TestSuite`'s VAR_INPUT. Tests whether `add_variable` opens a clear gap on a write that still doesn't require GUID handling.
2. **W3 (add_method) bench.** Adding a new method to an existing FB. This is where vanilla has to invent a `<Method Id="...">` GUID; the build verification will tell us whether vanilla's invention is accepted by TwinCAT or rejected. Strongest test of TcKit's writer thesis.
3. **Per-prompt cleanup pass on W2 and W3.** Mirror the W1 prompt-trim discipline: don't ask the model to verify work the harness verifies anyway.
4. **Address `open_project` redundancy** before W3 if it survives W2 (finding #5).
5. **N=3 on the W series** once W2/W3 are stable. The per-call retry pattern needs more than one sample to read confidently.
6. **W4 (add_pou) parked for later.** Touches both `.TcPOU` and `.plcproj`; strongest writer thesis but more cleanup complexity. Revisit if W3 doesn't already show what we expect.

## Interpretation, in one line

**The writer bench harness is honest end-to-end and the ADR-0003 patch primitive composes through to a buildable change on a real project; W1's job was to prove that, and the small ratio it produced is expected on a comment-only edit — the real comparison is W3 and waits on a separate run.**
