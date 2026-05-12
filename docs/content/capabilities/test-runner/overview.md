# TestRunner

**File:** `tckit/ports/test_runner.py`
**Purpose:** Run unit tests on the PLC runtime and return parsed results.

| Method | Returns |
|--------|---------|
| `run_tests(target_ams_id, *, plc_name=None)` | `Result` |
| `wait_complete(target_ams_id, timeout_seconds, *, plc_name=None)` | `Result` |
| `get_results(target_ams_id, *, plc_name=None)` | `TestResults` |
| `get_status()` | `TestStatus` |

`TestResults` is a parsed suite/test tree with pass/fail and assertion messages — not console scrape.

`target_ams_id` and `plc_name` mirror the IDE workflow: you pick both
the target route and the test PLC project before running tests. Both
are explicit on every call so an MCP session can't accidentally run on
the wrong target through implicit state. The TcUnit adapter is stubbed
today; ADR-0006 fills in the bodies against the bridge — the signatures
above are the contract ADR-0006 satisfies.

## Why this shape

The point of a test loop is feedback the model can act on without re-reading everything. A structured result tree lets it locate the one failing assertion and jump straight to that POU item via [ProjectReader](../project-reader/overview.md). Same [tool-design principle](https://www.anthropic.com/engineering/writing-tools-for-agents) as BuildRunner: parsed beats raw.
