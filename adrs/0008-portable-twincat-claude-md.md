---
adr: 0008
title: Portable TwinCAT CLAUDE.md template
status: Implemented
created: 2026-05-12
last_reviewed: 2026-05-18
issue:
pr:
related: [0007, 0012]
---

## Current state

**Decision (live):** `tckit/templates/twincat-claude.md` ships as package data,
acting as a linker file that includes topic files from `tckit/templates/twincat/`
(naming, comments, multi-plc-libraries, cyclic-in-method, polymorphism-arrays,
tcunit-tests). `tckit init --with-claude-md` is the supported install;
`tckit doctor` nudges when a `.sln` exists with no sibling `CLAUDE.md`. The
template captures only project-style choices a downstream session needs;
universal safety rules (safety-name guard, rename guard, never-edit-XML-directly)
stay in the `tc-write-st` skill. Error-handling style is a per-project
choice. Bench fixture authoring (`_common.scaffold_fixture`) drops the template
into newly-authored fixtures.

**Where it lives:** `tckit/templates/twincat-claude.md` plus
`tckit/templates/twincat/*.md`. Skill at `.claude/skills/tc-write-st/SKILL.md`
(unchanged surface; the template is the user-editable side).

## Context

`tc-write-st/SKILL.md` encodes TwinCAT conventions that only load when
Claude Code is inside the TcKit repo. The bench fixtures (ADR-0007) run with
cwd pinned to a task folder outside the TcKit repo; downstream operators on
real codebases would also miss them. Project-local `CLAUDE.md` is Claude
Code's standard delivery mechanism for cross-project conventions.

## Decision

Publish a portable `CLAUDE.md` template in TcKit. Operators drop it at the
root of any TwinCAT project; bench fixtures carry a copy as a side-effect of
the authoring path.

## Alternatives considered

- Docs page only (needs operators to discover it).
- New `tc-conventions` skill (skills are TcKit-installed; defeats the point).
- Embed in TcKit's CLAUDE.md and rely on inheritance (CLAUDE.md loads from
  the project root, not from imports).

## Consequences

**Enables:** TwinCAT conventions travel with any project that copies the
template; bench fixtures get convention-aware sessions without depending on
the TcKit-repo skill loading.

**Costs:** convention text duplicated between the skill and the template.
Manual sync. Mitigated by counterpart-comments; CI gate is overkill given
how rarely conventions change.

**Locks out:** nothing.

## Status notes

- 2026-05-18: Implementation outcome. Template shipped as real package data
  in `tckit/templates/twincat-claude.md` + topic files at
  `tckit/templates/twincat/`. The skill stays untouched: house-style rules
  live in the user-editable template, not in tckit-internal procedure.
  Cyclic-in-method and polymorphism-arrays topics were added alongside the
  older naming/comments/multi-PLC/tcunit-tests content because the T2-pid
  fixture (ADR-0012) exercises both rules through `add_property` and
  `I_Pid`-via-interface tests. Initial draft (~50 lines) trimmed the
  type-prefixed variable convention in favour of plain camelCase; existing
  type-prefixed code in the repo extends per the "match surrounding style"
  rule.
