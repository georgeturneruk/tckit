---
name: tc-adr
description: Use when reading, writing, promoting, or updating an Architecture Decision Record under adrs/, or when adding/editing a finding under bench/findings/. Triggers on requests like "write an ADR for X", "is there an ADR on Y", "what design decisions exist for Z", "promote this ADR to Accepted", "mark this ADR Implemented", "this session contradicts ADR 0007 — update it", "log a finding for this bench round", or whenever a multi-session design decision or post-hoc bench record needs to be captured or revised. Enforces the layered reading order (adrs/README.md -> Current state -> body), the status lifecycle (Exploring → Proposed → Accepted → Implemented, or Superseded), the template at adrs/0000-template.md, the "only write one if a single session would lose meaningful context" gate, the Current state maintenance rule (deviations flow back into the top block, not just Status notes), the trim-on-promotion rule, and the source-of-truth split (ADR = rationale, GitHub = tracked work, code = behaviour). Do NOT use for routine bug fixes or single-session work that doesn't need a written design record, and do NOT use for editing user-facing docs (that is tc-docs-write).
allowed-tools: Read, Edit, Write, Grep, Glob
---

# Working with TcKit ADRs and findings

ADRs live under `adrs/` as one Markdown file per decision; the template is at
[adrs/0000-template.md](../../../adrs/0000-template.md). Findings live under
`bench/findings/` as date-prefixed records of bench rounds and
post-implementation reviews. Both surfaces share an always-fresh index file
and a layered reading discipline.

## Reading order (this is the centrepiece)

Always go top-down:

1. **Index first.** [`adrs/README.md`](../../../adrs/README.md) and
   [`bench/findings/README.md`](../../../bench/findings/README.md). One-line
   summaries with status. Cheap to load; cheaper than scanning frontmatter
   of every file.
2. **Current state block** of any ADR that intersects the task. After
   frontmatter, before Context. Up to about six lines. This block is
   canonical: it captures the live decision in its post-implementation
   form, including any deviations.
3. **Body** (Context / Decision / Alternatives / Consequences) only when
   you need rationale or the original framing.
4. **Status notes** for the change journal, if you need the trail.

If Current state and the Decision section disagree on a question, **Current
state wins**. The Decision section may be a snapshot of how the design was
originally proposed; deviations flow back into Current state, not into
Decision.

## Status lifecycle

Frontmatter `status` is one of:

- `Exploring`: investigation is in flight; no specific proposal yet. Use
  this for "here are the options we've evaluated and what we know about each".
- `Proposed`: a recommended direction has crystallised and is awaiting
  review.
- `Accepted`: the design has been agreed; implementation may not have
  started.
- `Implemented`: the PR has landed. Fill in `pr:` with the GitHub PR
  number.
- `Superseded`: abandoned or replaced. Add a Status notes entry explaining
  why. Set `superseded_by:` (and where applicable, the replacement's
  `supersedes:`). Do not delete the file.

Promote forwards by editing the `status` field, **rewriting Current state to
match the new reality**, and adding a dated entry to Status notes. Never
skip a step silently; if you go straight from Exploring to Implemented,
the trail is missing.

## When to write a new ADR

Only if a single session would lose meaningful context without it. The test:

- Multi-session design decision? → ADR.
- Routine bug fix? → no ADR; the commit message is enough.
- Single-session refactor with no design crossroads? → no ADR.
- "Future me will need to understand *why* this is shaped like this"? → ADR.

If you're unsure, prefer not writing one. ADRs are for the decisions that
span sessions; the codebase, the PR description, and the commit message
cover the rest.

## Writing a new ADR

1. Pick the next free number from `adrs/`. Use four-digit zero-padded
   (`0011`, `0012`, …).
2. Copy [`adrs/0000-template.md`](../../../adrs/0000-template.md) to
   `adrs/<NNNN>-<slug>.md` and fill in the frontmatter (`adr`, `title`,
   `status`, `created`, `last_reviewed`, `issue`, `pr`, plus `supersedes` /
   `superseded_by` / `related` if applicable).
3. Write the **Current state** block as if the decision is settled now: one
   or two sentences for the decision, a line for "Where it lives", a list
   for "Open questions". For an `Exploring` ADR, the decision line is the
   live direction; for `Proposed`/`Accepted`, the recommended approach; for
   `Implemented`, the post-deviation truth.
4. Fill **Context**, **Decision**, **Alternatives considered**,
   **Consequences**, and a first Status notes entry dated today.
5. **Add a row to `adrs/README.md` in the same edit.** Number, status,
   title, one-line summary lifted from Current state. The index is the
   skim surface; it must move with the file.

## Updating an existing ADR

The rule that keeps ADRs honest: **when a session lands on something that
contradicts or extends an ADR, update its Current state block before moving
on, in the same edit as the code or follow-on ADR.**

- Add a dated entry to Status notes describing what changed.
- **Rewrite Current state so the live answer is correct.** If the original
  Decision section no longer matches reality, do not edit the Decision
  section; that's history. Update Current state instead, and let the
  Decision/Current state divergence stand as a record.
- Update the `adrs/README.md` row if the summary changed.
- Update `last_reviewed:` to today's date.
- If the change reverses the original decision, mark the ADR `Superseded`,
  set `superseded_by:`, and link to the replacement (or the PR that walked
  it back) in Status notes.

When a PR lands that implements the ADR's decision:

- Set `status: Implemented` and fill `pr:`.
- Rewrite Current state to capture the post-deviation truth.
- **Trim on promotion.** Collapse Alternatives to one short line per
  option (rejected paths don't earn paragraphs once the decision is
  locked). Compact Status notes to a single "Implementation outcome"
  entry plus any later course-corrections; the dated walk-through of
  intermediate decisions is checkpoint history that git already owns.
- Add a final Status notes entry: `YYYY-MM-DD: Implementation outcome.`
  followed by the deviations and lessons that matter going forward.

## Writing or updating a finding

Findings are post-hoc records of bench rounds, post-implementation reviews,
or any other "what we learned" writeup that an ADR Status notes entry
references. They live under `bench/findings/` named `YYYY-MM-DD-<slug>.md`.

Minimal frontmatter:

```yaml
---
date: YYYY-MM-DD
status: Current   # Current | Superseded | Stale
related_adrs: [N, ...]
superseded_by:    # optional path, e.g. bench/findings/YYYY-MM-DD-...md
---
```

- A finding starts `Current`. Mark it `Superseded` when a later finding or
  ADR explicitly walks the result back (re-bench round with different
  numbers, a fix that invalidates the measurement, etc.). Set
  `superseded_by:` to the replacement.
- Mark `Stale` if the finding predates a major change with no explicit
  successor; the measurements are historical only. Rare.
- Add a row to [`bench/findings/README.md`](../../../bench/findings/README.md)
  in the same edit. Newest first. If superseding an earlier finding, also
  update the "Why Superseded" section with a one-line explanation.

When a finding feeds into an ADR's Current state, the ADR's "Where it
lives" line names the finding by relative path. The findings index already
exposes the reverse direction via `related_adrs`.

## Source-of-truth split

- `adrs/*.md`: design rationale (how something should work, *why* this
  approach won). Current state is the live answer; Decision is history.
- `adrs/README.md`: always-fresh skim list.
- `bench/findings/*.md`: post-hoc records of bench rounds and reviews.
- `bench/findings/README.md`: always-fresh skim list, status-aware.
- GitHub issues / PRs: tracked work (what is open, what shipped).
- Code: implemented behaviour (the truth on what currently exists).
- `CLAUDE.md`: cross-session rules and conventions.

If two artefacts try to own the same thing, one is wrong. In particular:
don't restate code behaviour in an ADR (link to the file), and don't
restate the ADR's rationale in the code (link to the ADR from the PR
description, not from the code).

## Anti-patterns

- Writing an ADR for a single-session bug fix or refactor. The commit
  message is the right home.
- Updating Status notes but not Current state when a session reveals a
  deviation. The next session will then act on a stale top-block.
- Editing the Decision section to "fix" history. Decision is the
  original framing; deviations live in Current state.
- Setting `status: Implemented` before the PR has actually merged.
  `Accepted` is the right status while the PR is open.
- Promoting to Implemented without trimming Alternatives and compacting
  Status notes. The bloat compounds across ADRs and makes the index less
  useful.
- Deleting a Superseded ADR. The history matters; mark it Superseded and
  leave the file.
- Linking to ADRs from user-facing docs (`docs/content/`, README, MCP
  tool descriptions, CLI help). ADRs are internal. See the
  `tc-docs-write` skill for the user-facing voice rule.
- Adding a new ADR or finding without updating the corresponding
  `README.md` index in the same edit.

## Next

After an ADR write or update, return to the calling task (typically the
implementation work the ADR describes). No further handoff.
