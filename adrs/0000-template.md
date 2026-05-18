---
adr: 0000
title: Template
status: Template       # Exploring | Proposed | Accepted | Implemented | Superseded
created: YYYY-MM-DD
last_reviewed: YYYY-MM-DD
issue:                 # GH issue number (optional)
pr:                    # GH PR number once opened
supersedes:            # ADR number(s) this one replaces (optional)
superseded_by:         # ADR number that replaces this one (optional)
related:               # other ADR numbers (optional, list)
---

## Current state

**Decision (live):** One or two sentences. The decision in its final form,
including any post-implementation deviations. This is what agents read first.

**Where it lives:** Code paths, PR refs, or "not yet implemented" if pre-merge.

**Open questions:** Items that block promotion or matter for downstream work.
Delete the heading if empty.

<!--
Maintenance rules:
- Promoting status forward (Exploring -> Proposed -> Accepted -> Implemented):
  rewrite Current state to match the new reality before changing the status field.
- Status notes reveal a deviation from Decision: update Current state in the
  same edit. Current state is canonical; Decision is history.
- When promoting to Implemented: collapse Alternatives to one-line per option
  and compact Status notes to a single "Implementation outcome" entry plus any
  later course-corrections.
-->

## Context

What is the situation that prompted this decision? Constraints, observations,
pain points. Keep it factual; opinions go in Decision.

## Decision

What we are doing, and the key trade-offs accepted. Be specific enough that
another Claude session can act on it without re-deriving the design. Code
sketches are fine. For Implemented ADRs, Current state above is canonical;
this section preserves the original framing.

## Alternatives considered

What else was on the table, and why this won. One short line each once
status >= Accepted; paragraphs only while the decision is still open.

## Consequences

What this enables, what it costs, what it locks us out of. Honest about both
sides.

## Status notes

Running log of decisions and revisions. New entries at the bottom with date.

- YYYY-MM-DD: Drafted.
