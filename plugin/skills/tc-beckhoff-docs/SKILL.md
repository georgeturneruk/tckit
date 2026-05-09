---
name: tc-beckhoff-docs
description: Use when researching a Beckhoff TwinCAT library, function block, or function via Beckhoff infosys. Triggers on requests like "what does FB_EcCoESdoRead do", "which TF library has SDO read", "what are the inputs of FB_X", or as a precondition before writing code that uses an unfamiliar Beckhoff FB. Uses TcKit's find_fb, search_docs, and get_doc_page MCP tools. Do NOT use for inspecting the user's local project (that is tc-read-project).
allowed-tools: mcp__tckit__find_fb, mcp__tckit__search_docs, mcp__tckit__get_doc_page
---

# Researching Beckhoff infosys

Beckhoff FB conventions and timing notes are not reliable in training data, especially for newer TF libraries. Always ground claims in infosys.

## Procedure

1. **Known name → `find_fb`.** If the user (or your prior reasoning) has a specific FB name, call `find_fb(fb_name)` first. The result includes inputs, outputs, timing notes, and a description.
2. **Fuzzy query → `search_docs`.** When you don't know the exact name, call `search_docs(query, section="")`. Use `section` to scope to a known infosys area when you know one.
3. **Need full content → `get_doc_page`.** If a `find_fb` or `search_docs` snippet is insufficient, call `get_doc_page(url)` for the full parsed page. Pages are cached locally — re-calls are cheap.
4. **Always cite.** When reporting findings, include the infosys URL so the user can verify. Do not paraphrase parameter tables without referencing the source.
5. **Hand back.** Once you have the inputs/outputs/timing you need, return control to the calling skill (typically `tc-write-st`) with a one-line summary plus the URL.

## Anti-patterns

- Answering "what does FB_X do" from memory without `find_fb`.
- Calling `get_doc_page` before trying `find_fb` or `search_docs` (they already fetch the page when needed).
- Citing "Beckhoff documentation" without the specific URL.

## Next

If this was a precondition step for a write, hand off to `tc-write-st`.
