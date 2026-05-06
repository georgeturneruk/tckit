# DocsSearcher

**File:** `tckit/ports/docs_searcher.py`
**Purpose:** Fetch vendor documentation pages on demand.

| Method | Returns |
|--------|---------|
| `find_fb(fb_name)` | `FBDoc` |
| `find_library(library_name)` | `LibraryDoc` |
| `search(query, section)` | `SearchResults` |
| `get_page(url)` | `DocPage` |

Always call `find_fb()` before writing code that uses an unfamiliar function block — vendor FBs have input/output conventions and timing rules that are not reliably in training data.

## Why this shape

The alternative is either pre-loading entire library manuals "just in case" — wasting context on pages the agent never needs — or letting the model hallucinate FB signatures from stale training data. Pulling one page at the moment it is needed is the [just-in-time retrieval](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) pattern.
