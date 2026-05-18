---
adr: 0010
title: Foundation review, writer port and bridge surface after the B1 smoke
status: Implemented
created: 2026-05-15
last_reviewed: 2026-05-18
issue:
pr: 89, 90, 91, 92
related: [0003, 0006, 0007, 0009, 0011]
---

## Current state

**Decision (live):** Five-wave rebuild after the B1 smoke surfaced
foundation cracks. All five waves landed (PRs #89-#92 and wave 5).

- **Wave 1 (PR #89, housekeeping + `add_method` parser fix):**
  `Split-TcCode` no longer treats a method body as declaration when there's
  no `END_VAR`. Author scripts dropped their empty-`VAR/END_VAR` workaround.
- **Wave 2 (PR #90, xUnit cascade):** `add_library_placeholder` extended
  with optional `parameters: dict[str, dict[str, str]]`. Direct on-disk
  `.plcproj` splice (post-`AddPlaceholder` -> `Save` -> close -> splice ->
  reopen) using the schema XAE actually writes:
  `<Parameters>` (MSBuild ns) with `<Parameter ListName="GVL_PARAM_TCUNIT"
  xmlns=""><Key>UPPER</Key><Value>verbatim</Value></Parameter>` children.
  `ConsumeXml` rejected silently with an undocumented in-memory schema; do
  not use it. `TcUnit_ResultExportXmlPath` GVL convention retired across
  template / fixtures / skill.
- **Wave 3 (PR #91, bridge surface polish):** `BootAutostart: bool = true`
  payload field on `/deploy`. Central `bridge_route_timeout(route)` mapping
  in `tckit/utils/bridge_client.py` (defaults previously 60s against
  90-300s COM operations). `Probes` param renamed to `ReadSymbols` (PS 5.1
  garbled the original). `_TcDte.psm1` helpers `Get-TcPlcSysNode` vs
  `Get-TcPlcProjectNode` for the two TIPC paths (`TIPC^<plc>` vs
  `TIPC^<plc>^<plc> Project`).
- **Wave 4 (PR #92, new primitives):** `add_gvl(name, code, plc_name=)` +
  `/gvl` route (the `/pou` route rejects `gvl` now). `save_plc_as_library`
  idempotent (`overwrite=True` semantics). `read_symbols` /
  `read_symbols` port methods + `/symbols` route; `ReadSymbols` stays on
  `/tcunit-run` for the suites-finished-then-probe pattern.
- **Wave 5 (separate PR):** `update_pou_item` / `update_pou_item_patch`
  split into `update_pou_declaration` / `update_pou_implementation` /
  `update_method_body` + matching `-patch` variants. New routes
  `/pou-declaration`, `/pou-implementation`, `/method-body` and three
  `-patch` siblings. `Update-TcPouItem.ps1` and `Update-TcPouItemPatch.ps1`
  deleted; `_add_gvl` helper retired from `_common.py`. Closes #40.

ADR-0011 layered on top, closing the UmRT path-resolution side of wave 2.

**Where it lives:** writer/builder adapters at
`tckit/adapters/{writers,builders,test_runners}/automation_*.py`,
bridge handlers under `bridge/harness/`, central timeouts at
`tckit/utils/bridge_client.py:bridge_route_timeout`.

## Context

Getting the B1 closed-loop smoke green (PR #87) was a weeklong tour of
foundation cracks. The fixes that shipped were the minimum to unblock the
bench; behind each was a "we should have modelled this properly" call
deferred. Pre-1.0, breaking changes on the table, right moment to inventory
what was papered over.

## Decision

Group the cracks by theme. Wave-ship along natural cohesion lines rather
than one PR per item. Wave 5 (the `update_pou_item` split) lands solo on a
quiet base because every other wave extends the writer port surface it
rewrites.

### A. Writer-port completeness

1. `add_gvl` (vs punching through `/pou` with `PouType="gvl"`).
2. Library parameter overrides on `add_library_placeholder`.
3. Split `update_pou_item` into declaration/implementation/method-body.
4. `add_method` parser footgun: body treated as declaration when no
   `END_VAR`.
5. `save_plc_as_library` idempotent.

### B. Bridge surface

1. Autostart on `/deploy` surfaced as payload field.
2. HTTP timeouts vs COM-operation latencies; central mapping.
3. `Probes` param garbled by PS 5.1; renamed.
4. Magic tree paths (`TIPC^<plc>` vs `TIPC^<plc>^<plc> Project`);
   `_TcDte.psm1` helpers with doc-block semantics.

### C. Test runner

1. `read_symbol` / `read_symbols` as first-class primitive (was hidden
   inside `/tcunit-run` as `ReadSymbols`).
2. xUnit publisher gated on `GVL_Param_TcUnit.xUnitEnablePublish`;
   `_common.py:finalise_fixture` sets it to TRUE on TcUnit placeholder.
3. `TcUnit_ResultExportXmlPath` template lie retired; actual reads come
   from `GVL_Param_TcUnit.xUnitFilePath` with `%TC_BOOTPRJPATH%` default.

### D. Template

1. `TcUnit_ResultExportXmlPath` deleted from `templates/twincat-claude.md`.
2. Hardcoded classic-runtime boot paths in author scripts replaced by
   `%TC_BOOTPRJPATH%` (per-runtime portability for free).

## Alternatives considered

- Status quo + workarounds: defensible if TcKit stays internal; risky if any
  external consumer ships against the current port (every workaround becomes
  a back-compat obligation).
- One big-bang PR: untestable as a single chunk.
- Wait for a second consumer: the bench is the second consumer.

## Consequences

**Enables:** writer port becomes self-describing (no magic conventions);
fictional template surface gone; bench fixtures get a clean authoring chain
that doesn't punch through to bridge internals.

**Costs:** 5 PRs; each breaks something fixtures or skills relied on,
coordination matters.

**Locks out:** nothing.

## Status notes

- 2026-05-15: Implementation outcome. Waves 1-4 landed (#89-#92), wave 5
  shipped solo on top.
- 2026-05-15: Section A.2 first-pass had two silent-drop bugs caught while
  smoke-testing B1. Round one round-tripped placeholder XML through
  `ProduceXml(false)` -> splice -> `ConsumeXml`; the in-memory schema for
  placeholder parameters is undocumented and `ConsumeXml` accepted the input
  without applying it. Round two bypassed `ConsumeXml` and edited the
  consumer `.plcproj` directly but used a schema
  (`<ParameterValues>/<Parameter Name=>`) that doesn't match what XAE
  writes; build accepted the file but the runtime ignored the override.
  Final schema (reverse-engineered from XAE output) is in Current state
  above; pinned by a Pester suite so it can't silently regress.
  `%TC_BOOTPRJPATH%` expands to different roots on kernel-RT vs UmRT, so
  the hard-coded kernel-RT default in `Get-TcUnitDefaultXmlPath` was wrong
  on UmRT; per-machine override via `TCKIT_TCUNIT_XML_PATH` (documented in
  `.env.example`).
- 2026-05-17: ADR-0011 picked up Section C's TcUnit path-resolution
  remainder (UmRT auto-detect, `tckit doctor` TcUnit section, run_tests
  inline failures, `add_library_placeholder` idempotency,
  `save_plc_as_library` cold-start retry). See bench finding
  `2026-05-16-t1-schmitt-trigger-pair` for the 9x cost spike that triggered
  the follow-on work.
