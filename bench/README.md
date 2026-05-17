# TcKit Benchmark Harness

Measures Claude Code effectiveness on a TwinCAT project with versus
without the TcKit MCP server. Reusable across future capabilities — drop
new task prompts into `tasks/` and they pick up the same runner.

## What's measured

For each (task, config) pair, runs `claude -p` headless with the task
prompt and captures:

- Tool call count and breakdown by name
- Input and output token totals
- Wall-clock duration
- Exit code

`aggregate.py` reads the per-run JSONs back, prints per-pair means with
standard deviation, and computes the vanilla:tckit ratio per task.

## Configs

- `configs/empty.json` — no MCP servers; vanilla Claude Code with
  built-in tools only.
- `configs/tckit.json` — TcKit MCP server enabled via the stdio
  transport.

Both runs get the same built-in tool surface (`Glob`, `Grep`, `Read`,
`Edit`, `Bash`, etc.). The only difference is whether TcKit's MCP tools
are additionally available.

## Tasks

Reader tasks (no project mutation, no bridge needed):

- `tasks/01-orient.md` — project orientation (structural overview).
- `tasks/02-pinpoint-method.md` — pinpoint read of a single method body
  (`FB_TestSuite.AssertEquals_INT`).
- `tasks/03-explain-fb-api.md` — interface-only summary of an FB's
  public API (`FB_TestSuite`).

Writer tasks (mutate the project; require bridge + writable clone):

- `tasks/W1-patch-one-line.md` — change a single comment line on
  `FB_TestSuite.AssertEquals_INT`. Smoke test for the writer setup.

Add a new task by dropping a markdown file in `tasks/`. The placeholder
`${TCUNIT_PATH}` is substituted at runtime.

Parked tasks live under `tasks/_parked/` and are not part of the active
run. See `tasks/_parked/README.md` for the parking rationale.

## Prerequisites

- `claude` CLI on PATH (`claude --version` works).
- `tckit` on PATH (`pip install -e .` from the repo root, or
  `uvx tckit`).
- TcUnit checkout available locally. Pass the path via `--tcunit-path`
  (default `C:/TcUnit`).
- The [TcUnit library](https://github.com/tcunit/TcUnit) installed in
  the system library repository (distributor `www.tcunit.org`). The
  bug-hunting fixtures all reference TcUnit as a placeholder; without
  it installed the consumer PLC builds fail to resolve. Install via
  XAE's "Library Repository → Install..." against the `.library` from
  a TcUnit release.

## Run

Six invocations cover the three-by-two matrix at one run each (raise
`--runs` once a task set is stable enough to invest in tighter CIs):

```powershell
python bench/run.py --task bench/tasks/01-orient.md          --config bench/configs/tckit.json --runs 1
python bench/run.py --task bench/tasks/01-orient.md          --config bench/configs/empty.json --runs 1
python bench/run.py --task bench/tasks/02-pinpoint-method.md --config bench/configs/tckit.json --runs 1
python bench/run.py --task bench/tasks/02-pinpoint-method.md --config bench/configs/empty.json --runs 1
python bench/run.py --task bench/tasks/03-explain-fb-api.md  --config bench/configs/tckit.json --runs 1
python bench/run.py --task bench/tasks/03-explain-fb-api.md  --config bench/configs/empty.json --runs 1
```

Override the TcUnit path with `--tcunit-path C:/some/other/path` if
needed.

## Running writer tasks

Writer tasks mutate the project under `--tcunit-path`. Use a
dedicated, write-only clone — **never the canonical `C:/TcUnit`**.

### One-time setup

Pick a parent folder for everything bench-related (suggested:
`C:/TcKit-bench/`) and clone the target there:

```powershell
mkdir C:/TcKit-bench
git clone https://github.com/tcunit/TcUnit C:/TcKit-bench/TcUnit-writer
git -C C:/TcKit-bench/TcUnit-writer remote remove origin
```

The `remote remove origin` is load-bearing: with no remote, an
accidental `git push` from inside the bench clone has nowhere to
push. The reset command used between runs only manipulates local
state and cannot reach a remote either way.

Then start the Windows bridge service natively (separate terminal):

```powershell
.\bridge\Start-Bridge.ps1
```

XAE Shell should be open with a project loaded, or set
`XAE_MODE=headless` so the bridge spawns its own XAE instance.

The TcKit MCP server has to be running too, otherwise the `tckit`
config in this bench has no server to connect to. In a second
terminal:

```powershell
.\.venv-bench\Scripts\Activate.ps1
$env:PLC_PROJECT_PATH = "C:/TcKit-bench/TcUnit-writer/TcUnit.sln"
python -m tckit.server --transport sse
```

The MCP server listens on `http://localhost:8000/sse` (matching
`bench/configs/tckit.json`) and talks to the bridge internally for
write operations. Reader bench tasks didn't need it because they
go through stdio; SSE is required here because that's what the
bench config specifies.

### Running a writer task

Pass `--sln-path` (used for both pre-bench `/open` and the build
call), `--reset-cmd` (run before each iteration), and
`--build-after-each`:

```powershell
$bench = "C:/TcKit-bench/TcUnit-writer"
$reset = "git -C $bench reset --hard HEAD && git -C $bench clean -fd"

python bench/run.py --task bench/tasks/W1-patch-one-line.md `
    --config bench/configs/tckit.json --runs 1 `
    --tcunit-path $bench `
    --sln-path "$bench/TcUnit.sln" `
    --reset-cmd $reset `
    --build-after-each

python bench/run.py --task bench/tasks/W1-patch-one-line.md `
    --config bench/configs/empty.json --runs 1 `
    --tcunit-path $bench `
    --sln-path "$bench/TcUnit.sln" `
    --reset-cmd $reset `
    --build-after-each
```

The harness:

- Calls `/open` once before the run loop so XAE has the right
  solution attached.
- Runs `--reset-cmd` before every iteration; non-zero exit aborts.
- Captures `git diff` of the project after each run to a
  `<stem>.diff` sibling (no-op outside git).
- POSTs `/build` after each run when `--build-after-each` is set;
  writes the parsed bridge response to a `<stem>.build.json`
  sibling. `aggregate.py` reads these and reports per-pair build
  success rates.

`--bridge-url` overrides the default `http://localhost:8765`.

### Why the spawned session runs from the project directory

`bench/run.py` launches `claude -p` with `cwd` set to
`--tcunit-path`. Without this, the spawned session inherits the
bench operator's working directory (the TcKit repo itself), which
gives the vanilla config access to the bridge URL, harness
contracts, and skill prompts via `Read` and `Grep`. The model
then bypasses the comparison by calling the bridge directly. With
`cwd` pinned, the spawned session sees only the project under
test, which is the apples-to-apples surface the bench is meant
to measure.

## Running bug-hunting tasks (B-series)

### Quick path: `run-pair.ps1`

The full invocation has eight task-specific flags, so `bench/run-pair.ps1`
wraps it for the two fixtures we re-run most:

```powershell
# Both arms of T1, self-validating (model can deploy, dev machine only).
.\bench\run-pair.ps1 -Task T1 -SelfValidate

# Just the tckit arm of B1 with the deploy safety gate engaged.
.\bench\run-pair.ps1 -Task B1 -Arm tckit
```

The bench owns the MCP server lifecycle per run with `PLC_PROJECT_PATH`
pointing at the temp fixture path when `--isolate-cwd` is on. Without
that, the model's MCP writer calls would land in the operator's
long-lived MCP env path while Read sees the temp copy — the model
cannot observe its own writes (see the 2026-05-17 finding). Port 8000
must be free; the script aborts if anything is listening there.

`Get-Help .\bench\run-pair.ps1 -Detailed` for the full parameter list
and the self-validate trade-off. The long-form invocation below is
what the script expands to.

### Long-form

Closed-loop debugging fixtures live under `bench/fixtures/bug-hunting/<id>-<slug>/`
and follow the layout in [ADR-0007](../adrs/0007-bug-hunting-bench.md): one
`.sln` with two PLC projects (a library under test and a tests project that
references the library as a compiled `.library`). The bug-hunting bench
extends `run.py` with three flags so it can:

- save the library PLC as a freshly-installed `.library` before each run
  (so the consumer build resolves against the seeded source);
- after the model session ends, re-save the library against the model's
  edits, then build → deploy → start_runtime → run_tests on the tests
  PLC, and read pass/fail from runtime symbols;
- guard against the model editing test code to make the suite pass.

### Prerequisites (in addition to the writer-bench setup above)

- Target runtime reachable, AMS NetID exported as `TARGET_AMS_ID`
  (e.g. `127.0.0.1.1.1` for a local UmRT_Default).
- TcUnit installed in the System library repository (distributor
  `www.tcunit.org`).
- The bridge runs in `XAE_MODE=headless` or the operator's XAE Shell is
  already attached.
- Reset is path-scoped to the fixture directory, not the whole repo,
  because the fixture lives inside the TcKit working tree.

### B1 invocation

```powershell
$fix  = "C:/tckit/bench/fixtures/bug-hunting/B1-off-by-one"
$repo = "C:/tckit"
$reset = "git -C $repo checkout HEAD -- bench/fixtures/bug-hunting/B1-off-by-one"
$env:TARGET_AMS_ID = "127.0.0.1.1.1"

# tckit arm: --isolate-cwd to drop this repo's dev-side .claude/skills/
# + CLAUDE.md, then --inject-skills plugin/skills to put the 6
# user-facing TcKit skills back. That's what a real TcKit-plugin user
# would actually see (no tc-adr / tc-docs-write meta-skills).
python bench/run.py `
    --task bench/fixtures/bug-hunting/B1-off-by-one/TASK.md `
    --config bench/configs/tckit.json --runs 1 `
    --tcunit-path $fix `
    --sln-path "$fix/B1RollingAverage.sln" `
    --reset-cmd $reset `
    --pre-save-as-library B1RollingAverage_Plc `
    --post-run-tests RollingAverageTests `
    --tests-guard-path bench/fixtures/bug-hunting/B1-off-by-one/RollingAverageTests_Tc/RollingAverageTests/POUs/ `
    --close-during-run `
    --isolate-cwd `
    --inject-skills plugin/skills

# vanilla arm: same isolation, no skills injection — bare stock Claude
# Code plus the project sources. --close-during-run handles XAE's
# external-mod prompt when the model edits raw .TcPOU XML.
python bench/run.py `
    --task bench/fixtures/bug-hunting/B1-off-by-one/TASK.md `
    --config bench/configs/empty.json --runs 1 `
    --tcunit-path $fix `
    --sln-path "$fix/B1RollingAverage.sln" `
    --reset-cmd $reset `
    --pre-save-as-library B1RollingAverage_Plc `
    --post-run-tests RollingAverageTests `
    --tests-guard-path bench/fixtures/bug-hunting/B1-off-by-one/RollingAverageTests_Tc/RollingAverageTests/POUs/ `
    --close-during-run `
    --isolate-cwd
```

### What each flag does

- `--pre-save-as-library <plc-name>` — runs after `--reset-cmd` and before
  `claude -p`. Saves the named library PLC as `<sln-dir>/<plc-name>.library`
  and installs it to the System repo. Non-zero on failure aborts the run.
- `--post-run-tests <plc-name>` — runs after the model session. Drives the
  full validation cycle against the tests PLC: re-save library → build →
  deploy → start_runtime → run_tests (with probes). Writes a
  `.test-result.json` sibling capturing each step and the parsed probes.
  Requires `--sln-path`, `--pre-save-as-library`, and `TARGET_AMS_ID`.
- `--tests-guard-path <repo-relative-path>` — runs `git diff --name-only`
  against this path after each run; any output sets `tests_modified: true`
  and `passed: false` in `.test-result.json`. Per ADR-0007's tamper guard.
- `--test-probe <symbol>` — repeatable. Defaults to `MAIN.suite.NumberOfTests`
  + `MAIN.suite.Tests[1].TestIsFailed` (B1's probe set). Pass-fail is
  derived from any `*.TestIsFailed` probe; a "True" value flips the run
  to failed. Override for fixtures with more than one registered test.
- `--close-during-run` — closes the bridge's loaded solution before
  `claude -p` and re-opens after. Required for the vanilla arm: the
  model edits `.plcproj` and `.TcPOU` XML directly (no MCP writer
  tools available), and an open XAE flags those as "modified
  externally". The pre-save-as-library still runs with the solution
  open; the close fires only around the model session. Mirrors the
  pattern in `Add-TcLibraryPlaceholder.ps1`.
- `--isolate-cwd` — copies the fixture to a fresh temp directory
  outside this repo before each `claude -p` and pins cwd there,
  then syncs the model's edits back to the real fixture before the
  post-run validation cycle. The copy excludes `CLAUDE.md`,
  `.claude/`, `.mcp.json`, and XAE build artefacts. Use on both
  arms so neither inherits this repo's dev-side `.claude/skills/`
  or `CLAUDE.md` via Claude Code's cwd-ancestor walk. Pair with
  `--inject-skills` on the tckit arm to give it the user-facing
  plugin surface afterwards. Avoids `--bare`, which would also
  work but forces `ANTHROPIC_API_KEY` auth (no OAuth/keychain).
- `--inject-skills <dir>` — after `--isolate-cwd` has prepared the
  temp fixture, copies the contents of `<dir>` (each subdirectory
  is one skill) into `<temp>/.claude/skills/`. Use on the tckit
  arm with `plugin/skills` so the model sees the 6 user-facing
  TcKit skills, not this repo's dev-only `tc-adr` /
  `tc-docs-write`. Requires `--isolate-cwd`.

### Pre-flight smoke (optional but recommended)

Before the first bench run on a new machine, sanity-check the closed-loop
infrastructure with the deterministic smoke runner (no model in the loop):

```powershell
python bench/fixtures/bug-hunting/_author/smoke_B1.py
```

It drives red → patch → green and exits 0 on success. Reset the fixture
afterwards (`git -C $repo checkout HEAD -- bench/fixtures/bug-hunting/B1-off-by-one`)
so the bench run starts on the seeded bug.

## Aggregate

```powershell
python bench/aggregate.py
```

`--filter <substring>` narrows to a subset of result files. The summary
includes per-(task, config) means and per-task ratio lines.

## Output

Each run writes up to four files into `bench/results/`:

- `<task>__<config>__<timestamp>__run<n>.json` — full event log, metrics,
  raw stdout/stderr. Used by `aggregate.py`.
- `<task>__<config>__<timestamp>__run<n>.md` — small metrics header plus
  Claude's final answer (`final_text`) as Markdown. Useful for eyeballing
  whether the answer is actually any good, and for diffing two configs'
  answers side-by-side without parsing JSON.
- `<task>__<config>__<timestamp>__run<n>.diff` — `git diff` of the project
  tree after the run. Written when `--tcunit-path` is a git working tree.
  Most useful for writer tasks; empty (or absent) for reader tasks.
- `<task>__<config>__<timestamp>__run<n>.build.json` — parsed bridge
  `/build` response. Only written when `--build-after-each` is set.
  `aggregate.py` reads these to surface per-pair build success rates.

`bench/results/` is gitignored — don't commit individual runs.

## Notes on fidelity

- Tool *results* are not captured (Claude Code's stream-json omits tool
  outputs by default). We measure call shape, not content.
- Subjective output quality (e.g. "did the orientation summary actually
  help?") is not scored automatically. Open the `.md` sibling for the
  human-readable answer; diff two configs' `.md` files to compare them.
- Three runs per pair bounds variance loosely. Bump `--runs` if you need
  tighter confidence intervals.
