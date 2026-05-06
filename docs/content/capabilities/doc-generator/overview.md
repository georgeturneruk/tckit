# DocGenerator

**File:** `tckit/ports/doc_generator.py`
**Purpose:** Render project documentation directly from structured comments in the ST source.

| Method | Returns |
|--------|---------|
| `generate(project_path, output_path)` | `Result` |
| `get_status()` | `DocStatus` |

## Why this shape

Wikis drift. Code does not — at least, not from itself. Generating docs from comments embedded next to the code they describe means there is one source of truth for "what does this FB do", and an agent reading either the source or the generated docs gets the same answer.
