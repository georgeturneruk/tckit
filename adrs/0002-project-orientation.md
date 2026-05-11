---
adr: 0002
title: Project orientation — extend get_structure with subsystem context
status: Exploring
created: 2026-05-10
issue:
pr:
---

## Context

When Claude opens a TwinCAT project for the first time, the current
`ProjectReader.get_structure` returns a flat list of POU names, GVL
names, DUT names, and a `tasks: list[str]` field that the existing
adapter does not populate. Three pieces of project-shaping context
that the project files already encode are not surfaced:

- **Folder hierarchy.** `POUs/Axes/`, `POUs/Sequences/`, `POUs/IO/`
  carry subsystem meaning. A flat list flattens that meaning away
  and forces Claude to infer subsystems from name prefixes (which
  works on disciplined projects, fails on messy ones).
- **Task layout.** What runs in which task, at what cycle time, in
  what priority order. Lives in `.tsproj` and `.plcproj`. Critical
  for "what does this project do" and "what is the timing".
- **Library references.** Which Beckhoff or third-party libraries
  the project depends on. Lives in `.plcproj`. Anchors `find_fb`
  searches and explains where unfamiliar FB names come from.

Without these, Claude has to crawl interface declarations to
reconstruct shape that the project file already tells you in one
read. That is wasted context window and slow.

ADR-0001 (`Exploring`) captured the related search question and
parked it. The orientation gap is more load-bearing for the
workflows that actually matter on TwinCAT (understand, debug,
add feature) and is much cheaper to address: pytmc parses
`.tsproj`/`.plcproj` instantly (0.07s on TcUnit, 0.37s on TcOpen
TcoCore per the 0001 spike) and exposes everything we need.

## Goals

- Surface task layout, library refs, and folder grouping in a
  single call so Claude can frame a project's shape without N
  follow-up reads.
- Keep the layered-read pattern (`get_structure` →
  `get_pou_interface` → `get_pou_item`) intact. Orientation is a
  richer `get_structure`, not a new layer.
- Codify the orientation playbook as a skill (`tc-orient-project`)
  so Claude reaches for these new fields consistently rather than
  reverting to its Python-project default of jumping into specific
  files.

## Decision (provisional sketch)

### Port-shape changes

Extend `tckit/ports/types.py`:

```python
@dataclass
class TaskInfo:
    name: str
    cycle_time_us: int | None
    priority: int | None
    programs: list[str]   # POU names invoked in this task

@dataclass
class LibraryRef:
    name: str
    version: str
    placeholder: str | None = None  # e.g. "Tc2_Standard"

@dataclass
class POURef:
    name: str
    pou_type: POUType
    path: str
    folder: str           # NEW: "POUs/Axes" relative to PLC project root

@dataclass
class ProjectStructure:
    project_path: str
    pous: list[POURef]
    gvls: list[str]
    duts: list[str]
    tasks: list[TaskInfo]              # CHANGED: was list[str]
    libraries: list[LibraryRef]        # NEW
```

`ProjectReader.get_structure` signature is unchanged; only its
return-type richness grows. Adapters that cannot populate the new
fields return empty lists.

### New skill

`tc-orient-project` (loads on the first project encounter in a
session). Walks Claude through:

1. `get_structure` → identify subsystems by `folder` grouping;
   surface task list and library refs.
2. Read `MAIN` (or whichever POU is bound to the primary cyclic
   task per `TaskInfo.programs`).
3. For each subsystem, sample one top-level FB at the
   `get_pou_interface` level to learn naming and error-handling
   conventions.
4. Stop. Do not crawl further until the user's request demands it.

The skill is the surface that turns the new fields into a
consistent navigation behaviour.

### Optional sibling: `get_pou_summary`

```python
def get_pou_summary(self, pou_name: str) -> POUSummary: ...
```

Returns declaration plus parsed `// :Description:` text. No method
list, no method bodies. Used for "what is this FB for" without
pulling the full interface. Smaller scope than the core 0002 work;
ship after the core lands if orientation flow demands it.

## Alternatives considered

- **Keep flat `get_structure`, codify orientation in skill alone.**
  The skill could call `get_structure` plus N `get_pou_interface`
  calls to build the same picture. Wastes context and is slow on
  large projects (200+ POUs). Loses on every dimension.
- **Pull orientation into a separate port** (e.g.
  `ProjectInspector`). Architecturally clean but premature; the
  data lives in the same project files `ProjectReader` already
  opens, and a second port doubles wiring for a single read. Reader
  is the right home until a cross-port concern justifies splitting.
- **Surface task and library data via raw `.tsproj`/`.plcproj`
  reads exposed as MCP tools.** Adapter-level concern bleeding into
  the port surface; brittle to vendor-format change. Reject.

## Consequences

**Enables:** meaningful project orientation in two-three calls
instead of twenty-plus; future debug tooling that needs task and
library context; `tc-orient-project` as a stable skill that
codifies "first-touch" navigation.

**Costs:** `ProjectStructure` becomes a richer dataclass (mild MCP
serialisation overhead); adapter implementations need a
`.tsproj`/`.plcproj` parser (pytmc is the obvious choice and
already adapter-isolated).

**Type changes:** `tasks` moves from `list[str]` to
`list[TaskInfo]`; `POURef` gains a `folder` field; `libraries` is
new. TcKit is pre-1.0 with no external consumers to protect, so
make the type changes cleanly without backward-compatibility
shims.

**Locks out:** nothing structural. The new fields are additive
where possible; if a future adapter cannot fill them, returning
empty lists or `None`s degrades the orientation skill gracefully
without breaking the contract.

## Status notes

- 2026-05-10: Drafted as `Exploring` after ADR-0001 framing review
  concluded that search was not the highest-leverage navigation
  investment. Validation steps before promoting to `Proposed`:
    1. Spike pytmc on TcUnit and TcOpen to confirm `TaskInfo`,
       `LibraryRef`, and folder data are extractable as proposed.
    2. Draft the `tc-orient-project` skill flow against a real
       project and confirm the orientation completes in
       under five tool calls on a 50+ POU project.
