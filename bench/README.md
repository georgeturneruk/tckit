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

- `tasks/01-orient.md` — project orientation (structural overview).
- `tasks/02-pinpoint-method.md` — pinpoint read of a single method body
  (`FB_TestSuite.AssertEquals_INT`).
- `tasks/03-explain-fb-api.md` — interface-only summary of an FB's
  public API (`FB_TestSuite`).

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

## Aggregate

```powershell
python bench/aggregate.py
```

`--filter <substring>` narrows to a subset of result files. The summary
includes per-(task, config) means and per-task ratio lines.

## Output

Each run writes two files into `bench/results/`:

- `<task>__<config>__<timestamp>__run<n>.json` — full event log, metrics, raw
  stdout/stderr. Used by `aggregate.py`.
- `<task>__<config>__<timestamp>__run<n>.md` — small metrics header plus
  Claude's final answer (`final_text`) as Markdown. Useful for eyeballing
  whether the answer is actually any good, and for diffing two configs'
  answers side-by-side without parsing JSON.

`bench/results/` is gitignored — don't commit individual runs.

## Notes on fidelity

- Tool *results* are not captured (Claude Code's stream-json omits tool
  outputs by default). We measure call shape, not content.
- Subjective output quality (e.g. "did the orientation summary actually
  help?") is not scored automatically. Open the `.md` sibling for the
  human-readable answer; diff two configs' `.md` files to compare them.
- Three runs per pair bounds variance loosely. Bump `--runs` if you need
  tighter confidence intervals.
