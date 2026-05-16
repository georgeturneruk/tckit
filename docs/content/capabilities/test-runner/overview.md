# TestRunner

**File:** `tckit/ports/test_runner.py`
**Purpose:** Run unit tests on the PLC runtime and return parsed results.

| Method | Returns |
|--------|---------|
| `run_tests(target_ams_id, *, plc_name=None)` | `Result` |
| `get_results(target_ams_id, *, plc_name=None)` | `TestResults` |

`run_tests` blocks until the suites finish or the bridge's timeout fires.
`get_results` returns the parsed XML the run wrote — call it after
`run_tests` has succeeded.

`target_ams_id` and `plc_name` mirror the IDE workflow: you pick both
the target route and the test PLC project before running tests. Both
are explicit on every call so an MCP session can't accidentally run on
the wrong target through implicit state.

`TestResults` is a parsed suite/test tree with pass/fail and assertion
detail (expected / actual / line) — not a console scrape.

## Why this shape

The point of a test loop is feedback the model can act on without
re-reading everything. A structured result tree lets it locate the one
failing assertion and jump straight to that POU item via
[ProjectReader](../project-reader/overview.md). Same
[tool-design principle](https://www.anthropic.com/engineering/writing-tools-for-agents)
as BuildRunner: parsed beats raw.
