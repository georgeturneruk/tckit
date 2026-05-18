---
adr: 0005
title: Multi-project sln support
status: Implemented
created: 2026-05-12
last_reviewed: 2026-05-18
issue:
pr:
related: [0004, 0006, 0007, 0009]
---

## Current state

**Decision (live):** Every project-scoped MCP tool takes an optional
keyword-only `plc_name=None`. Default is "use the only PLC in the sln; error
if ambiguous". `PLC_PROJECT_NAME` env is the session-wide default; per-call
`plc_name` wins. **`ProjectStructure` returns `plcs: dict[str, PLCSection]`
in every case** (single-PLC returns a one-entry dict; this is a deviation
from the original Decision section, which kept the flat shape on single-PLC).
`libraries` lives on `PLCSection`; `tasks` stays at the solution level (TwinCAT
tasks are sln-wide). `POURef` gained a required `plc_name` field. TestRunner
methods take `target_ams_id` as the first positional arg (added during
implementation; ADR-0006 mirrors this). Reader staleness is per-`.plcproj`
mtime (ADR-0004's sln-mtime guard extended).

**Where it lives:** `tckit/utils/plc_resolver.py:resolve_plc_name`
(1-2-3-4 fallback for writer/builder/test-runner callers), inline
symbol-aware variant in `tckit/adapters/readers/xml_reader.py`.
`tckit/ports/types.py` for `PLCSection`/`ProjectStructure`/`POURef`.

## Context

TcKit assumed one PLC project per session. The bridge's `Resolve-TcPlcName`
already handled multi-PLC selection via `-PlcName`; the Python adapter layer
and MCP signatures didn't. `XmlReader` picked the shallowest `.plcproj`
and silently ignored the rest. The bug-hunting bench (ADR-0007) needs the
library + tests split that idiomatic TwinCAT projects use, so this was the
gate.

## Decision

Per-tool optional `plc_name`. Env override `PLC_PROJECT_NAME`. Nested
`{plc_name: {symbol_name: Path}}` reader index. Symbol resolution rule:
explicit `plc_name` -> env default -> unique across all PLCs ->
ambiguous-symbol error naming the candidates.

## Alternatives considered

- Stateful "current PLC" MCP call: hidden state, fragile across multi-step flows.
- Implicit guess from POU-name uniqueness: silent-on-collision is worse than
  explicit error.
- Two MCP servers, one per PLC: operator complexity.
- Solution-wide reads, PLC-scoped writes: response shape needs the PLC label
  anyway, so the param is essentially free.

## Consequences

**Enables:** library+test split, ADR-0007, real multi-PLC operator workflows.

**Costs:** every project-scoped tool gains one optional kwarg (99% of
callers ignore it). `ProjectStructure` shape change is breaking on
multi-PLC; pre-1.0 with no external consumers, so no shim.

**Locks out:** nothing.

**Risks:** ambiguous-symbol errors will surface. The error must name which
PLCs contain the symbol so Claude can disambiguate without re-reading.

## Status notes

- 2026-05-12: Implementation outcome. Deviations from the original Decision
  section captured in Current state above (single dict shape everywhere,
  per-plcproj mtime guard, doc generator sectioned by PLC, shared
  resolver, `target_ams_id` on TestRunner). The single-dict shape replaced
  the planned flat-list-for-single-PLC behaviour because it gave the doc
  generator a clean model and removed a branch in every consumer.
