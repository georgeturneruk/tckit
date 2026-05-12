---
adr: 0008
title: Portable TwinCAT CLAUDE.md template
status: Implemented
created: 2026-05-12
issue:
pr:
---

## Context

The TcKit repo carries a `.claude/skills/tc-write-st/SKILL.md`
that encodes naming conventions, comment style, the bError
propagation pattern, the safety-name guard, the rename guard, and
the "never edit `.TcPOU` XML directly" rule. The skill loads when
Claude Code is working inside the TcKit repo. Outside TcKit, in
any downstream TwinCAT project, the skill is invisible.

Two cases where this hurts:

- The bug-hunting bench fixtures (ADR-0007) live in their own
  task folders, each containing a self-contained sln. The
  spawned `claude -p` runs with cwd pinned to the task folder
  (cwd isolation is load-bearing per the W-series findings), so
  the TcKit-repo skill does not load.
- Any operator with a real TwinCAT codebase who wants Claude
  Code to respect TwinCAT conventions has to either install the
  TcKit plugin or duplicate the conventions by hand in their
  project's CLAUDE.md.

Claude Code reads a project-local `CLAUDE.md` automatically at
session start (per the standard convention). That is the right
delivery mechanism for cross-project conventions, because it
travels with the project and doesn't require any TcKit-specific
installation.

## Decision

Publish a portable `templates/twincat-claude.md` in the TcKit
repo. Operators drop it at the root of any TwinCAT project as
`CLAUDE.md` so Claude Code reads it on session start. The bench
fixtures (ADR-0007) carry a copy at each task-folder root, which
also exercises the template during benching.

### Content scope

Bare minimum for the first cut: the rules a downstream Claude
session needs to know in a fresh TwinCAT project, nothing more.
The template does not assume TcKit's MCP tools are present; it
reads as project conventions, not as a workflow:

- Naming: `FB_`, `PRG_`, `GVL_`, `E_`, `ST_`, `I_` POU prefixes;
  PascalCase methods; **camelCase variables with no type prefix**
  (e.g. `enableMotor`, `targetSpeed`); match existing style when
  editing.
- Comment style: RST line comments (`// :Description:`) preferred;
  Beckhoff XML `(*~ <docu> ~*)` accepted; match the file's
  existing style.
- bError / nErrorId propagation with a short code sketch.
- Safety-name guard: if any name in the change touches `Safety`,
  `SIL`, `TÜV`/`TUV`, `Emergency`, `EStop`, `SafetyDoor`, stop
  and ask before any write.
- Rename guard: never execute a cross-project rename
  autonomously; report references and ask.
- "Do not edit `.TcPOU` or `.plcproj` XML by hand if a TwinCAT
  automation interface is available."

The template lands ~50 lines of markdown. No TcUnit pointer, no
TcKit pointer, no verbose explanations. Project-specific
guidance (which FBs to look at, how to run things, etc.) is left
as a marker so each operator adds their own. Expand the template
later as concrete needs surface; do not pre-load it with content
nobody has asked for.

### Sync with the skill

`tc-write-st/SKILL.md` and `templates/twincat-claude.md` carry
the same convention text. Risk: they drift. Mitigation:

- Both files carry a comment block at the top noting the
  counterpart and asking the editor to keep them in sync.
- No CI check enforces this. The drift cost is low (conventions
  rarely change), and a CI gate would force coupled edits where
  often only one side moves. Manual sync is the right level of
  rigour.

### Mirror in the bench fixtures

Each bug-hunting task folder gets a literal copy of the template
as its `CLAUDE.md`. This means:

- The bench session sees the conventions as if it were working
  on any operator's TwinCAT project.
- Changes to the template propagate to the bench fixtures via a
  one-line copy script in the fixture-implementation PR (or a
  small `scripts/sync-twincat-claude.py` if the count grows).

## Alternatives considered

- **Docs page only.** Rejected: Claude Code reads CLAUDE.md
  automatically and project conventions need that automaticity.
  A docs page that operators have to know about and reference
  doesn't change behaviour by default.
- **A new tc-conventions skill.** Rejected: skills are
  TcKit-specific. Operators who haven't installed the TcKit
  plugin shouldn't have to in order to get TwinCAT conventions
  in their project's Claude sessions.
- **Embed the conventions in TcKit's CLAUDE.md and rely on
  Claude Code's CLAUDE.md inheritance.** Rejected: CLAUDE.md
  loads from the project root, not from imported packages. A
  downstream project's session does not see TcKit's CLAUDE.md.

## Consequences

**Enables:** TwinCAT conventions travel with any project that
copies the template. The bench fixtures get convention-aware
sessions without depending on the TcKit-repo skill loading.
Operators with realistic codebases get a drop-in starting point
for their own CLAUDE.md.

**Costs:** one file's worth of convention text duplicated
between the skill and the template. Manual sync.

**Locks out:** nothing. The skill and the template can diverge
deliberately if TcKit-specific advice (which MCP tool to call
for which operation) belongs in the skill but not in the template.

**Risks:** drift between the two files. Mitigation is the
counterpart-comment in both; if drift becomes a real problem,
add a `scripts/check-twincat-claude-sync.py` to CI.

## Status notes

- 2026-05-12: Drafted as `Proposed`, promoted to `Implemented` in
  the same PR. The template file ships alongside this ADR; no
  separate implementation step. Bench-fixture mirrors arrive
  with the ADR-0007 implementation PR.
- 2026-05-12: Trimmed to a bare-minimum first cut (~50 lines).
  Variable naming convention dropped the type-prefix style
  (`bEnable`, `nCount`, `fGain`...) in favour of plain camelCase
  (`enableMotor`, `count`, `gain`). The `tc-write-st` skill was
  updated to match. Existing code in the repo (bench fixtures
  and earlier writer-bench tasks) keeps its type-prefixed names;
  the "match existing style" rule covers extension of legacy
  files. Expand the template only when concrete operator needs
  surface, not speculatively.
