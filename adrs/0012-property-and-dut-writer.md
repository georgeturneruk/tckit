---
adr: 0012
title: Property and DUT writer additions
status: Implemented
created: 2026-05-18
issue:
pr:
---

## Context

The `ProjectWriter` port covers creating POUs, GVLs, methods, and
variables, but two structural objects had no creation path:

- **Properties.** A property on a function block (a tree item with
  `Get`/`Set` accessor children) could be modified after the fact
  via `update_method_body` (which already accepted property names
  alongside methods and actions) but could not be created from
  scratch. The only paths were hand-authoring `.TcPOU` XML or
  creating the property manually in the IDE.
- **DUTs (struct, enum, union).** Read-only support existed
  (`get_dut`, `DUT` dataclass in `tckit/ports/types.py`), but
  nothing on the writer side. Same workarounds applied.

The gap blocked any bench task that asks the model to build a
non-trivial subsystem from a near-empty fixture. The forthcoming
T2-pid anti-windup bench explicitly requires the model to author
properties (tuning gains with setter validation, plus read-only
state-inspection accessors) and DUTs (a mode enum at minimum, a
state struct optionally). Without these writer tools, vanilla and
tckit arms have asymmetric work (vanilla edits raw XML; tckit
falls back to the same Edit/Write path the skill explicitly
forbids), which invalidates the comparison.

## Decision

Add two MCP writer tools end-to-end:

### `add_property`

- Port (`tckit/ports/writer.py`): abstract method
  `add_property(pou_name, property_name, return_type, *,
  getter_code=None, setter_code=None, plc_name=None) -> Result`.
  At least one accessor required.
- Adapter (`tckit/adapters/writers/automation_writer.py`): POSTs
  to `/property` with `{PouName, PropertyName, ReturnType,
  GetterCode?, SetterCode?, ProjectPath, PlcName?}`. Adapter
  enforces the "at least one accessor" rule locally before the
  network call.
- Bridge handler (`bridge/harness/Add-TcProperty.ps1`): finds the
  parent POU, calls `CreateChild(name, 611, …)` for the property
  parent, sets its declaration to `PROPERTY <name> : <return_type>`,
  then creates a Get child (kind 613) and/or Set child (kind 614)
  underneath, writing the supplied accessor code via the existing
  `Set-TcItemSource` helper.
- Bridge route: new `POST /property` in `bridge/Start-Bridge.ps1`.
- MCP server: new `add_property` tool in `tckit/server.py`,
  registered in `_TOOLS`.
- Skill table: row added to `.claude/skills/tc-write-st/SKILL.md`
  alongside `add_method`.

`Get-TcKind` was extended to recognise `property_get` and
`property_set` so future handlers can reach those kinds by name
without duplicating constants.

### `add_dut`

- Types (`tckit/ports/types.py`): new `DUTKind` StrEnum with
  values `STRUCT`, `ENUM`, `UNION`. The TwinCAT `ALIAS` type
  (`TYPE x : LREAL; END_TYPE`) is intentionally absent in v1 —
  its CreateChild kind constant is not in our SPIKE_NOTES table
  and no current bench needs it. Add when a real use case
  surfaces.
- Port: `add_dut(name, code, *, dut_kind=DUTKind.STRUCT,
  plc_name=None) -> Result`.
- Adapter: POSTs to `/dut` with `{Name, DutKind, Code,
  ProjectPath, PlcName?}`.
- Bridge handler (`bridge/harness/Add-TcDut.ps1`): finds the DUTs
  folder via the new `Get-TcDutsFolder` helper (path
  `TIPC^<plc>^<plc> Project^DUTs`, parallel to the existing
  `Get-TcPousFolder`), calls `CreateChild(name, kind, …)` with
  the appropriate kind (606 struct, 605 enum, 607 union), and
  writes the declaration via `Set-TcItemSource`.
- Bridge route: new `POST /dut` in `bridge/Start-Bridge.ps1`.
- MCP server: new `add_dut` tool, registered in `_TOOLS`.
- Skill table: row added alongside `add_gvl`.

`Get-TcKind` was extended to recognise `struct`, `enum`, and
`union`.

### Shape consistency with the existing surface

Both new tools mirror `add_method` exactly: per-call `plc_name`
wins, env-var fallback, single-PLC auto-resolve. Payload shapes
follow the same `PascalCase` field convention the rest of the
adapter uses (`PouName`, `Code`, etc.) so the bridge parser does
not need a new case. Each new MCP tool is its own function with
its own docstring, so the LLM picks the right verb for the right
object rather than punching everything through a generic
`add_object(kind, …)`.

## Alternatives considered

- **Punch `add_property` through the existing `/method` route by
  passing `ItemType='property'`.** Rejected. The existing handler
  already accepts that value, but properties need two accessor
  children created underneath the property parent — three
  CreateChild calls, not one. Reusing `/method` would either
  silently produce a property with no accessors (broken) or grow
  branchy multi-step logic inside `Add-TcMethod.ps1`. Cleaner to
  give properties their own dedicated handler.
- **Fold everything into a single `add_object(kind, name, code, …)`
  tool.** Rejected. The argument shape differs per object kind:
  properties need a return type and two optional accessor bodies;
  DUTs need a kind discriminator; methods and GVLs are simpler. A
  uniform signature would be a union of all the cases and lose the
  per-argument validation an LLM benefits from.
- **Pre-seed properties and DUTs in bench fixtures, have the model
  fill bodies via `update_method_body`.** Rejected on the planning
  side. The whole point of the T2-pid bench is to exercise
  authoring; pre-seeding the structural objects defeats it.

## Consequences

**Enables.** The T2-pid anti-windup bench and any future bench
where the model must author a subsystem from an empty FB header.
Removes the asymmetry where tckit had no first-class writer path
for two common object kinds while the skill simultaneously
forbade raw XML editing.

**Costs.** Two new handler scripts, two new routes, two new MCP
tools — same surface-area pattern as the existing seven writer
tools, so the marginal complexity is small. The `DUTKind`
discriminator is one new public type.

**Locks out nothing.** ALIAS support, `add_action`,
`add_property_accessor` (adding a GET/SET to an existing
property), and `add_interface_method` are obvious follow-ups but
not blocked by this design — each would be a parallel addition
with the same shape.

## Status notes

- 2026-05-18: Drafted and implemented in the same PR. Both tools
  ship with payload-shape unit tests in
  `tests/unit/test_automation_writer.py`. Bridge handlers
  smoke-tested via the T2-pid fixture-authoring path (Commit D in
  the same PR). The "cyclic-in-method" and
  "polymorphism-arrays" topic rules in the new TwinCAT CLAUDE.md
  template (ADR-0008) are paired with this work because they
  describe the patterns the new writer tools enable.
