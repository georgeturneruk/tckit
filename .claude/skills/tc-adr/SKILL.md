---
name: tc-adr
description: Use when reading, writing, promoting, or updating an Architecture Decision Record under adrs/. Triggers on requests like "write an ADR for X", "is there an ADR on Y", "what design decisions exist for Z", "promote this ADR to Accepted", "mark this ADR Implemented", "this session contradicts ADR 0007 — update it", or whenever a multi-session design decision needs to be captured or revised. Enforces the status lifecycle (Exploring → Proposed → Accepted → Implemented, or Superseded), the template at adrs/0000-template.md, the "only write one if a single session would lose meaningful context" gate, the decisions-flow-back rule (update Status notes when a session changes course), and the source-of-truth split (ADR = rationale, GitHub = tracked work, code = behaviour). Do NOT use for routine bug fixes or single-session work that doesn't need a written design record, and do NOT use for editing user-facing docs (that is tc-docs-write).
allowed-tools: Read, Edit, Write, Grep, Glob
---

# Working with TcKit ADRs

ADRs live under `adrs/` as one Markdown file per decision. The template is at `adrs/0000-template.md`. CLAUDE.md owns the session-start trigger that says "read relevant Exploring/Proposed/Accepted ADRs first"; this skill owns everything else about ADRs.

## Status lifecycle

Frontmatter `status` is one of:

- `Exploring` — investigation is in flight; no specific proposal yet. Use this for "here are the options we've evaluated and what we know about each".
- `Proposed` — a recommended direction has crystallised and is awaiting review.
- `Accepted` — the design has been agreed; implementation may not have started.
- `Implemented` — the PR has landed. Fill in `pr:` with the GitHub PR number.
- `Superseded` — abandoned or replaced. Add a Status notes entry explaining why. Do not delete the file.

Promote forwards by editing the `status` field and adding a dated entry to the Status notes section. Never skip a step silently — if you go straight from `Exploring` to `Implemented`, the trail is missing.

## When to write a new ADR

Only if a single session would lose meaningful context without it. The test:

- Multi-session design decision? → ADR.
- Routine bug fix? → no ADR, commit message is enough.
- Single-session refactor with no design crossroads? → no ADR.
- "Future me will need to understand *why* this is shaped like this"? → ADR.

If you're unsure, prefer not writing one. ADRs are for the decisions that span sessions; the codebase, the PR description, and the commit message cover the rest.

## Writing a new ADR

1. Pick the next free number from `adrs/`. Use four-digit zero-padded (`0011`, `0012`, …).
2. Copy `adrs/0000-template.md` to `adrs/<NNNN>-<slug>.md` and fill in the frontmatter (`adr`, `title`, `status`, `created`, `issue`, `pr`).
3. Fill **Context**, **Decision**, **Alternatives considered**, **Consequences**, and a first Status notes entry dated today.
4. Status starts at `Exploring` (if investigation is still open) or `Proposed` (if a direction is on the table).

## Updating an existing ADR

The rule that keeps ADRs honest: **when a session lands on something that contradicts or extends an ADR, update its Status notes section before moving on**.

- Add a dated entry under Status notes describing what changed and why.
- If the change reverses the original decision, mark the ADR `Superseded` and link to the replacement (if one exists yet) or to the PR that walked it back.
- If the change extends but doesn't contradict, leave the status as-is and just append to Status notes.

When a PR lands that implements the ADR's decision:

- Set `status: Implemented`.
- Fill in `pr:` with the PR number.
- Add a final Status notes entry: `YYYY-MM-DD: Landed in #<PR>.`

## Source-of-truth split

- `adrs/*.md` — design rationale (how something should work, *why* this approach won).
- GitHub issues / PRs — tracked work (what is open, what shipped).
- Code — implemented behaviour (the truth on what currently exists).
- `CLAUDE.md` — cross-session rules and conventions.

If two artefacts try to own the same thing, one is wrong. In particular: don't restate code behaviour in an ADR (link to the file), and don't restate the ADR's rationale in the code (link to the ADR from the PR description, not from the code).

## Anti-patterns

- Writing an ADR for a single-session bug fix or refactor. The commit message is the right home.
- Skipping the Status notes update when a session changes course. The next session will then act on a stale ADR.
- Setting `status: Implemented` before the PR has actually merged. `Accepted` is the right status while the PR is open.
- Deleting a Superseded ADR. The history matters; mark it Superseded and leave the file.
- Linking to ADRs from user-facing docs (`docs/content/`, README, MCP tool descriptions, CLI help). ADRs are internal. See the `tc-docs-write` skill for the user-facing voice rule.

## Next

After an ADR write or update, return to the calling task (typically the implementation work the ADR describes). No further handoff.
