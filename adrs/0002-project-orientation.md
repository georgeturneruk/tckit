---
adr: 0002
title: Project orientation — extend get_structure with subsystem context
status: Implemented
created: 2026-05-10
last_reviewed: 2026-05-18
issue:
pr:
related: [0001, 0004, 0005]
---

## Current state

**Decision (live):** `ProjectReader.get_structure` returns task layout (cycle
in microseconds, priority, bound programs), library refs, and a `folder` per
`POURef`. Implementation uses stdlib `xml.etree.ElementTree` against
`.plcproj` / `.tsproj` / `.TcTTO` (no pytmc dependency); `.TcTTO` is
authoritative for task data when present, `.tsproj` is the fallback.
`get_pou_summary` deferred (not needed by the orientation flow). Once
ADR-0005 landed, the return shape became
`plcs: dict[str, PLCSection]` with `libraries` per-PLC and `tasks` at
solution level.

**Where it lives:** `tckit/adapters/readers/xml_reader.py:get_structure`,
`tckit/ports/types.py` (`TaskInfo`, `LibraryRef`, `POURef`,
`ProjectStructure`). `tc-orient-project` skill at
`.claude/skills/tc-orient-project/SKILL.md`.

## Context

When Claude opens a TwinCAT project for the first time, the original
`get_structure` returned a flat list of POU/GVL/DUT names and an unpopulated
`tasks: list[str]`. Three pieces of project shape were missing: folder
hierarchy (subsystem meaning), task layout (cycle times, priorities,
bindings), library references. Without these, Claude crawled interface
declarations to reconstruct shape the project file already encodes.

ADR-0001 deferred the search question; orientation is the higher-leverage
navigation investment because the data lives in files `ProjectReader`
already opens.

## Decision

Extend `tckit/ports/types.py` with `TaskInfo`, `LibraryRef`, a `folder` field
on `POURef`, and a richer `ProjectStructure`. `ProjectReader.get_structure`
signature unchanged; return-type richness grows. Adapters that cannot populate
the new fields return empty lists.

Ship `tc-orient-project` as a directive skill that loads on first encounter:
one `get_structure`, sample one FB per subsystem, stop. The skill is what
turns the new fields into a consistent navigation behaviour.

`get_pou_summary` (declaration + parsed `:Description:` text) considered as
an optional sibling; gated on real demand.

## Alternatives considered

- Keep flat `get_structure`, codify orientation in the skill alone (loses on
  context cost; N follow-up `get_pou_interface` calls per subsystem).
- New `ProjectInspector` port (premature; data lives in files
  `ProjectReader` already opens).
- Raw `.tsproj`/`.plcproj` MCP tools (adapter detail bleeding into the port).

## Consequences

**Enables:** orientation in two-three calls instead of twenty-plus; future
debug tooling needing task/library context; a stable first-touch skill.

**Costs:** richer dataclass (mild MCP serialisation overhead); adapter needs
a `.tsproj`/`.plcproj` parser. Type changes are breaking on pre-1.0; no
compatibility shims.

**Locks out:** nothing structural.

## Status notes

- 2026-05-11: Implementation outcome.
  - Skipped pytmc; stdlib `xml.etree.ElementTree` matches `XmlReader`'s
    existing dependency posture. `.TcTTO` is the authoritative task source;
    `.tsproj` is the fallback when no `.TcTTO` exists.
  - Bench validation
    (`bench/findings/2026-05-11-adr-0002-post-impl.md`): Task A vanilla
    24 calls / 8.5k tokens -> tckit 5 calls / 4.0k tokens; vanilla also
    improved to 6 calls / 4.9k tokens because the same directive skill loaded
    on the vanilla arm (fallback path uses stock tools). 1.24x token ratio
    held back at the time by issue #42 (reader not cached across MCP
    requests).
  - Subjective-quality review
    (`bench/findings/2026-05-11-subjective-quality-review.md`): TcKit caught
    a multi-PLC task the vanilla arm missed under the same token budget; a
    correctness win the numeric ratio hid.
- 2026-05-15: Issue #42 closed. Reader cache now persists; the bench-validation
  gap noted above no longer applies. ADR-0004 covers the staleness signal.
