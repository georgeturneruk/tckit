# BuildRunner

**File:** `tckit/ports/builder.py`
**Purpose:** Build, deploy, and runtime control with structured results.

| Method | Returns |
|--------|---------|
| `build(project_path, *, plc_name=None)` | `BuildResult` |
| `deploy(target_ams_id, *, plc_name=None)` | `Result` |
| `start_runtime(target_ams_id)` | `Result` |
| `get_status()` | `BuildStatus` |

`BuildResult` carries each diagnostic as `{file, line, message, severity}` — never a raw log blob. Never deploy without a successful build first.

`build` and `deploy` accept an optional `plc_name` to scope the operation
to a single PLC project on multi-project solutions; `start_runtime` is
target-wide. The bridge follows the standard fallback chain (per-call
name → `PLC_PROJECT_NAME` env → auto-resolve → ambiguous error).

## Why this shape

Tool outputs are context. A 2,000-line build log forces the model to scan, regex, and guess; a list of structured diagnostics lets it act. This is the [high-signal tool result](https://www.anthropic.com/engineering/writing-tools-for-agents) principle — return what the agent will use, not everything the underlying tool emitted.
