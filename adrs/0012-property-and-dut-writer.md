---
adr: 0012
title: Property and DUT writer additions
status: Implemented
created: 2026-05-18
last_reviewed: 2026-05-18
issue:
pr:
related: [0007, 0008]
---

## Current state

**Decision (live):** Two MCP writer tools, each via its own bridge route +
handler, mirroring `add_method` shape.

- `add_property(pou_name, property_name, return_type, *, getter_code=None,
  setter_code=None, plc_name=None)`: `Add-TcProperty.ps1` finds the parent
  POU, `CreateChild(name, 611, ...)` for the property parent (kind 611),
  then Get child (613) and/or Set child (614). Adapter enforces "at least
  one accessor" before the network call.
- `add_dut(name, code, *, dut_kind=DUTKind.STRUCT, plc_name=None)`:
  `Add-TcDut.ps1` resolves the DUTs folder
  (`TIPC^<plc>^<plc> Project^DUTs`), `CreateChild(name, kind, ...)` with
  606 (struct), 605 (enum), 607 (union). ALIAS deliberately absent in v1;
  add when a real use case surfaces.

`Get-TcKind` extended to recognise `property_get`, `property_set`, `struct`,
`enum`, `union`.

**Where it lives:** `tckit/ports/writer.py`,
`tckit/adapters/writers/automation_writer.py` (`add_property`, `add_dut`),
`bridge/harness/Add-TcProperty.ps1`, `bridge/harness/Add-TcDut.ps1`,
`bridge/Start-Bridge.ps1` (`POST /property`, `POST /dut`).
`tckit/ports/types.py:DUTKind` (`STRUCT | ENUM | UNION`).

## Context

The writer port covered POUs, GVLs, methods, variables, but properties
(property parent + Get/Set accessor children) and DUTs (struct/enum/union)
had no creation path. T2-pid (ADR-0007) needs properties (tuning gains with
setter validation, read-only state accessors) and at least an enum DUT;
without these tools, vanilla and tckit arms have asymmetric work and the
comparison breaks.

## Decision

Two MCP writer tools, each with its own dedicated bridge route + handler.
Mirror `add_method` shape (per-call `plc_name` wins, env-var fallback,
single-PLC auto-resolve). Each tool gets its own function with its own
docstring so the LLM picks the right verb per object kind.

## Alternatives considered

- Punch `add_property` through `/method` with `ItemType='property'`:
  properties need three CreateChild calls, not one; either silently broken
  or branchy.
- Single `add_object(kind, name, code, ...)`: argument shape differs per
  kind; LLM loses per-argument validation.
- Pre-seed properties/DUTs in fixtures: T2-pid's whole point is to exercise
  authoring.

## Consequences

**Enables:** T2-pid and any bench task where the model must author a
subsystem from an empty FB header. Removes the asymmetry where tckit had
no first-class writer path while the skill forbade raw XML editing.

**Costs:** two handler scripts / two routes / two MCP tools, same shape
as the existing seven writer tools. One new public type (`DUTKind`).

**Locks out nothing.** ALIAS, `add_action`, `add_property_accessor` (add a
GET/SET to an existing property), and `add_interface_method` are
parallel additions with the same shape.

## Status notes

- 2026-05-18: Implementation outcome. Both tools ship with payload-shape
  unit tests in `tests/unit/test_automation_writer.py`. Bridge handlers
  smoke-tested via the T2-pid fixture-authoring path (Commit D in the same
  PR). The cyclic-in-method and polymorphism-arrays topic rules in the
  TwinCAT CLAUDE.md template (ADR-0008) are paired with this work because
  they describe the patterns the new writer tools enable.
