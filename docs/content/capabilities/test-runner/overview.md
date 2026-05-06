# TestRunner

**File:** `tckit/ports/test_runner.py`
**Purpose:** Run unit tests on the PLC runtime and return parsed results.

| Method | Returns |
|--------|---------|
| `run_tests()` | `Result` |
| `wait_complete(timeout_seconds)` | `Result` |
| `get_results()` | `TestResults` |
| `get_status()` | `TestStatus` |

`TestResults` is a parsed suite/test tree with pass/fail and assertion messages — not console scrape.

## Why this shape

The point of a test loop is feedback the model can act on without re-reading everything. A structured result tree lets it locate the one failing assertion and jump straight to that POU item via [ProjectReader](../project-reader/overview.md). Same [tool-design principle](https://www.anthropic.com/engineering/writing-tools-for-agents) as BuildRunner: parsed beats raw.
