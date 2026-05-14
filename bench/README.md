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
