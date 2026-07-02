---
name: tc-docs-write
description: Use when writing or updating the TcKit documentation site or the README — anything under docs/content/ or README.md. Triggers on requests like "update the docs", "document this in the docs", "write a docs page for X", "the README needs Y", "add a capability page", "fix the home page", or before editing any file under docs/content/ or README.md. Enforces the user-facing-behaviour update checklist (which surface to update), the voice rules (no ADR references on user-facing surfaces, no history paragraphs, README as voice reference for the home page, no marketing scaffolding), and the "docs are a bug when stale" stance. Do NOT use for ADR writing (that is tc-adr) or for in-code docstrings on ST POUs (that is tc-write-st).
allowed-tools: Read, Edit, Write, Grep, Glob
---

# Writing TcKit docs

The TcKit docs site lives under `docs/content/` (MkDocs). The README at the repo root mirrors a slice of it. Both are user-facing surfaces and follow the same voice rules.

## When to update docs

A change touches user-facing behaviour, and therefore needs a docs update in the same PR, if any of the following are true:

- MCP tool names, arguments, or return shapes changed
- CLI commands or flags changed
- Config keys changed (`config.json`, `~/.tckit/permissions.json`, `.env`)
- Install or setup steps changed
- Ports, adapters, or the one-rule architecture statement changed
- An ADR moved to `Implemented` for a design decision that has now shipped

If none of those apply, the change does not need a docs update. Don't invent one.

## Which surface to update

- **README** — only when the surface shown on the README itself has changed. The README is a slice, not the whole story; many real-behaviour changes affect `docs/content/` only.
- **`docs/content/`** — the default. Find the page that owns the changed surface (capability page for tool/argument changes, getting-started for install, architecture page for the one-rule statement, etc.) and edit there.

If the README and the relevant `docs/content/` page both need to change, both go in the PR.

## Voice rules

The home page (`docs/content/index.md`) takes its voice from the README. When editing the home page, match the README's structure, density, and tone. (The capability and adapter pages under `docs/content/capabilities/` are denser reference material and have their own appropriate voice — don't apply this rule to them.)

For every user-facing page:

- **No ADR references.** ADRs are an internal design store. `docs/content/`, the README, MCP tool descriptions, and CLI help text describe current behaviour, not the history of how it got there. If a passage needs more context than fits inline, link to the PR. Never link to `adrs/`.
- **No history paragraphs.** Don't chronicle what something used to be. "The port had two more methods in an earlier draft" belongs on the ADR or in the commit message, not on a page describing the current shape.
- **No marketing scaffolding on the home page.** Specifically, do not add:
  - problem/solution framing tables that repeat the capabilities list (e.g. "What TcKit solves")
  - redundant capability summaries ("X at a glance" next to a capabilities table)
  - "Design philosophy" or similar sections that duplicate the architecture page
  - feature-pitch bullet lists
  
  State what it does, link where it can be verified, move on.

## Procedure

1. Identify which surface(s) changed (use the "Which surface to update" guide above).
2. Read the page you intend to edit so the change matches the surrounding voice, structure, and link conventions.
3. Edit. Prefer `Edit` over `Write`; never rewrite a page wholesale when a section edit will do.
4. If the change touches the home page, re-read the README first and match its voice.

## Anti-patterns

- Linking from `docs/content/` or the README to `adrs/` to explain "why". The PR or commit message is the right pointer; the docs describe current behaviour.
- Adding a "History" / "Background" / "Previously" section to a user-facing page.
- Padding the home page with capability blurbs that the capabilities table already covers.
- Updating the README for an internals-only change (e.g. a refactor of an adapter that doesn't surface to users).
- Leaving the docs stale because "the diff is the truth" — stale docs are a bug.

## Next

After the docs edit, return to the calling task (commit, PR, etc.). No further handoff.
