---
adr: 0003
title: Patch-style writes for fine edits to POU items
status: Implemented
created: 2026-05-10
last_reviewed: 2026-05-18
issue:
pr:
related: [0010]
---

## Current state

**Decision (live):** Three primitives shipped:
`update_pou_item_patch(pou_name, item_name, old_string, new_string)` (anchor-
based Edit-style replace, fails on 0 or >1 matches),
`add_variable(pou_name, scope, declaration, item_name=None)`,
`get_pou_declaration(pou_name)` (FB-level VAR sections only). All three do
their read-modify-write inside the PowerShell bridge harness via a
`Get-TcItemSource` helper that mirrors `Set-TcItemSource`; the Python
adapter is a thin route caller, keeping the adapter-isolation rule clean.
`add_variable` inserts a new scope block at the conventional ST position
(`VAR_INPUT` before `VAR`, etc.) when the target scope is absent. Later
**split by ADR-0010 wave 5** into `update_pou_declaration` /
`update_pou_implementation` / `update_method_body` plus the matching
`-patch` variants; `update_pou_item` and `update_pou_item_patch` no longer
exist.

**Where it lives:** `tckit/ports/writer.py`, `tckit/adapters/writers/automation_writer.py`,
bridge handlers under `bridge/harness/` (`Update-*Patch.ps1`, `Add-TcVariable.ps1`).

## Context

The read path follows JIT (`get_structure` -> `get_pou_interface` ->
`get_pou_item`), so a "look at one method" question costs ~30 lines of
context. The original write path was coarser: `update_pou_item(pou_name,
item_name, code)` replaced the entire body of one item, costing 6-10x the
context of vanilla `Edit` on a Python file. Bottleneck was the port
surface, not the underlying COM API; the API accepts text but the Claude
-> TcKit conversation can be smaller.

## Decision

Add an anchor-based patch primitive (mirroring Claude Code's own `Edit`)
plus a thin `add_variable` helper and a `get_pou_declaration` read shortcut.
Read-modify-write happens in the adapter (or bridge); Claude only sends the
patch.

## Alternatives considered

- Line-range patches: brittle to whitespace/intervening edits.
- JSON-Patch / AST edits: requires a parser in the write path.
- Many small domain verbs (`rename_variable`, `add_method_param`, ...):
  surface bloat; patch + one helper covers the 80% case.
- Do nothing: 6-10x context multiplier on every small edit compounds fast.

## Consequences

**Enables:** order-of-magnitude reduction in edit context; closer parity
with vanilla `Edit` on Python.

**Costs:** new port surface; adapter does read-modify-write atomically (or
accepts the same interleaving race as today's whole-item writes).

**Risks:** anchor uniqueness can fail (`old_string` appears twice). Adapter
fails explicitly with a useful error so Claude can re-anchor; same shape
as Claude Code's `Edit`, well understood.

**Locks out:** nothing.

## Status notes

- 2026-05-12: Implementation outcome (PR #52). All three primitives shipped
  together. Bridge-side read-modify-write keeps the One Rule clean; the
  bridge already owns the COM handle. `add_variable` inserts before the
  matching scope's `END_VAR`; appends a fresh `<scope> ... END_VAR` block
  if the scope doesn't exist. Patch failure reports the occurrence count.
- 2026-05-12: Writer-bench measurements
  (`bench/findings/2026-05-12-writer-bench-wrap-up.md`): W1 1.21x tokens,
  W2 2.39x, W3 2.43x; W3 calls 2.50x. Convention-aware placement in
  `add_variable` + skill rule against post-write self-verification + W3
  prompt-trim cut residual bench noise.
- 2026-05-15: Superseded internally by ADR-0010 wave 5. `update_pou_item` /
  `update_pou_item_patch` split into `update_pou_declaration` /
  `update_pou_implementation` / `update_method_body` plus the matching
  `-patch` variants. `Update-TcPouItem.ps1` and `Update-TcPouItemPatch.ps1`
  deleted. The ADR's thesis (anchor-based patches at the port level) stands;
  the surface is now finer.
