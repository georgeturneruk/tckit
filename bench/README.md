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
- `tasks/02-find-callers.md` — find all callers of
  `FB_TestSuite.AssertEquals_INT` (16 call sites across `TcUnit/` and
  `TcUnit-Verifier/`).

Add a new task by dropping a markdown file in `tasks/`. The placeholder
`${TCUNIT_PATH}` is substituted at runtime.

## Prerequisites

- `claude` CLI on PATH (`claude --version` works).
- `tckit` on PATH (`pip install -e .` from the repo root, or
  `uvx tckit`).
- TcUnit checkout available locally. Pass the path via `--tcunit-path`
  (default `C:/TcUnit`).

## Run

Four invocations cover the two-by-two matrix at three runs each:

```powershell
python bench/run.py --task bench/tasks/01-orient.md       --config bench/configs/tckit.json --runs 3
python bench/run.py --task bench/tasks/01-orient.md       --config bench/configs/empty.json --runs 3
python bench/run.py --task bench/tasks/02-find-callers.md --config bench/configs/tckit.json --runs 3
python bench/run.py --task bench/tasks/02-find-callers.md --config bench/configs/empty.json --runs 3
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

Per-run results land in `bench/results/` as JSON, one file per run.
Aggregate prints to stdout.

`bench/results/` is gitignored — don't commit individual runs.

## Notes on fidelity

- Tool *results* are not captured (Claude Code's stream-json omits tool
  outputs by default). We measure call shape, not content.
- Subjective output quality (e.g. "did the orientation summary actually
  help?") is not scored automatically. Eyeball the `final_text` field
  in the per-run JSON for that.
- Three runs per pair bounds variance loosely. Bump `--runs` if you need
  tighter confidence intervals.
