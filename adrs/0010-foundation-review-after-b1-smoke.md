---
adr: 0010
title: Foundation review, writer port and bridge surface after the B1 smoke
status: Implemented
created: 2026-05-15
issue:
pr: 89, 90, 91, 92
---

## Context

Getting the B1 closed-loop smoke green (PR
[#87](https://github.com/georgeturneruk/tckit/pull/87)) turned into a
weeklong tour of foundation cracks. The fixes that shipped were the
minimum to unblock the bench; behind each one is a "we should have
modelled this properly" call we deferred. Still pre-1.0, breaking changes
on the table, so this is the right moment to inventory what we papered
over.

## Decision

This ADR started in `Exploring`. Each themed item below is a piece of
foundation rebuild; specific items land as separate PRs (see Status notes
for the rebuild ordering and the wave-1/wave-5 split).

### A. Writer-port completeness

1. **GVL authoring.** `POUType` deliberately scopes to function blocks,
   functions, programs, and interfaces; GVLs aren't POUs.
   `bench/fixtures/bug-hunting/_author/_common.py:_add_gvl` punches
   through to the bridge's `/pou` route with `PouType: "gvl"`. The right
   move is first-class `ProjectWriter.add_gvl(name, code, plc_name=)` +
   `/gvl` route. The `/pou` route should reject `gvl` once the dedicated
   path exists.
2. **Library parameter overrides.** TcUnit's xUnit publisher is gated on
   `GVL_Param_TcUnit.xUnitEnablePublish` (a `VAR_GLOBAL CONSTANT` on a
   parameter list). The IDE's Library parameters dialog serialises
   overrides into the consumer's `.plcproj` `<PlaceholderReference>`
   block. We don't expose this anywhere, so every TcUnit consumer falls
   back to symbol probes or post-hoc plcproj edits. Extend
   `add_library_placeholder` with an optional
   `parameters: dict[str, dict[str, str]]` (keys grouped under their
   host parameter-list GVL).
3. **POU body shape is implicit.** `update_pou_item` takes a single
   `code` blob; `Update-TcPouItem.ps1` splits it at `END_VAR`. The port
   doesn't expose `declaration` / `implementation` separately, and
   "target the POU's own decl+impl rather than a method body" is encoded
   as the magic convention `item_name == pou_name`. Split into
   `update_pou_declaration` / `update_pou_implementation` /
   `update_method_body` with explicit semantics + matching patch
   variants.
4. **`add_method` parser footgun.** Issue
   [#84](https://github.com/georgeturneruk/tckit/issues/84):
   `Split-TcCode` treats a method body as part of the declaration when
   there's no `END_VAR`. Author scripts include empty `VAR/END_VAR` as a
   workaround. Fix in the bridge splitter so the workaround can come out.
5. **`save_plc_as_library` is not idempotent.** It refuses to overwrite
   an existing `.library`; callers have to delete the stale artefact
   first. Add `overwrite=True` (or unconditional replacement).

### B. Bridge surface, implicit and leaky bits

1. **PLC autostart on `/deploy` is implicit.** `Invoke-TcDeploy.ps1`
   unconditionally calls `BootProjectAutostart = $true` and
   `GenerateBootProject($true)`. Right default for the bench (without it
   the PLC sits loaded-but-stopped and serves no symbols) but consumers
   that want to control autostart have no way out. Surface as a
   `BootAutostart: bool = true` payload field.
2. **HTTP-timeout defaults don't match COM-operation latencies.**
   `XaeComBuilder.deploy` and `start_runtime` defaulted to 60s against
   operations that routinely take 90-300s on a cold target. Audit every
   bridge-talking adapter; a central `bridge_route_timeout(route)`
   mapping in `tckit/utils/bridge_client.py` is sturdier than per-method
   overrides.
3. **`ConvertTo-HashtableDeep` is doing something undiagnosed to
   parameters literally named `Probes`.** Renamed to `ReadSymbols` and
   moved on, but the root cause (PowerShell 5.1 parameter binding plus
   the bridge's request decoder) eats arbitrary keys silently. Worth a
   targeted repro; otherwise the next route to take a string param may
   trip the same wire.
4. **Magic tree paths.** `TIPC^<plc>` exposes `ITcPlcProject`;
   `TIPC^<plc>^<plc> Project` exposes `ITcPlcIECProject`. Distinction
   undocumented in our helpers; got it wrong on first try in
   `Invoke-TcDeploy.ps1`. A `_TcDte.psm1` helper pair
   (`Get-TcPlcSysNode` vs `Get-TcPlcProjectNode`) with the right
   semantics in doc-blocks prevents the next session rediscovering it.

### C. Test runner adapter

1. **No symbol-read primitive.** Reading a PLC symbol by name is
   universally useful; right now it only exists as the `ReadSymbols`
   parameter tacked onto `/tcunit-run`. Land properly: `read_symbol` /
   `read_symbols` port methods, `/symbols` route; keep `ReadSymbols` on
   `/tcunit-run` as a convenience for the suites-finished-then-probe
   pattern.
2. **xUnit XML publisher is off by default and we work around it.**
   With (A.2) in place, `_common.py:finalise_fixture` sets
   `xUnitEnablePublish = TRUE` on the TcUnit placeholder so the
   publisher writes results. `/tcunit-run` then gets useful per-test
   detail from the XML for free.
3. **`xml_path` in TcUnit's GVL is fictional.** `templates/twincat-claude.md`
   told consumers to declare `TcUnit_ResultExportXmlPath`. TcUnit
   doesn't read that name; the actual publisher reads
   `GVL_Param_TcUnit.xUnitFilePath` defaulting to
   `%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml`. The template, the
   fixture's `GVL_TcUnit`, and `Get-TcUnitXmlPath` all go away in favour
   of reading the param-list value (or trusting the default).

### D. Template / convention rot

1. **`TcUnit_ResultExportXmlPath` template lie.** Per C.3, the
   `templates/twincat-claude.md` section needs deleting.
2. **Hardcoded classic-runtime boot paths in author scripts** would have
   been a problem had we not switched the smoke to probes. With C.2 +
   C.3 done, `%TC_BOOTPRJPATH%` gives per-runtime portability for free.

## Alternatives considered

- **Status quo, keep the workarounds, document them, move on.**
  Defensible if TcKit stays an internal bench tool forever. Risky if
  any external consumer ships against the current writer port; every
  workaround becomes a backward-compat obligation.
- **One big bang of fixes.** Single PR adding `add_gvl`, reshaping
  `update_pou_item`, landing library parameters, removing the fictional
  constant. Cohesive in spirit but the diff would be untestable as a
  single chunk. A sequence of small PRs each referring back to this
  ADR is easier.
- **Wait until a second consumer surfaces.** Rejected: the bench itself
  is the second consumer (after the writer-bench), and B1 proved that
  adding a consumer immediately surfaces every gap.

## Consequences

**Enables:** writer port becomes self-describing (no magic conventions),
fictional template surface gone, bench fixtures get a clean authoring
chain that doesn't punch through to bridge internals.

**Costs:** every item above is a small-to-medium PR; the full sequence
is roughly 5-6 PRs. Each breaks one thing the fixtures or skills rely
on; coordination matters.

**Locks out:** nothing. Each piece is independent; stopping at any
point leaves partial state better than today.

## Status notes

- 2026-05-15: Drafted as `Exploring`. The B1 smoke
  ([#87](https://github.com/georgeturneruk/tckit/pull/87)) carries the
  workarounds; this ADR tracks what to do about them.
- 2026-05-15: Rebuild ordering agreed; bundled into 5 PRs along natural
  cohesion lines rather than one PR per item:
    1. **Housekeeping** (E + A.4).
    2. **xUnit cascade** (A.2 + C.2 + C.3 + D.1).
    3. **Bridge surface polish** (B.1 + B.2 + B.3 + B.4).
    4. **New writer + builder primitives** (A.1 + A.5 + C.1).
    5. **Split `update_pou_item`** (A.3, full bench / skill / template
       migration).

  PR 5 lands solo on top of a quiet base because it rewrites the writer
  port surface every other PR extends; PRs 1-4 are mutually independent.
  ADR promoted to `Proposed` with this entry.
- 2026-05-15: Waves 1-4 landed in
  [#89](https://github.com/georgeturneruk/tckit/pull/89) (housekeeping +
  Split-TcCode header-only fix),
  [#90](https://github.com/georgeturneruk/tckit/pull/90) (xUnit cascade,
  promoted this ADR to `Proposed`),
  [#91](https://github.com/georgeturneruk/tckit/pull/91) (bridge surface
  polish),
  [#92](https://github.com/georgeturneruk/tckit/pull/92) (new writer +
  builder primitives).
- 2026-05-15: Wave 5 landed. `update_pou_item` /
  `update_pou_item_patch` split into `update_pou_declaration` /
  `update_pou_implementation` / `update_method_body` plus matching
  patch siblings; new routes `/pou-declaration`, `/pou-implementation`,
  `/method-body` and the three `-patch` variants. `Update-TcPouItem.ps1`
  and `Update-TcPouItemPatch.ps1` deleted; `_add_gvl` helper retired
  from `_common.py`. Closes
  [#40](https://github.com/georgeturneruk/tckit/issues/40) by
  construction. ADR promoted to `Implemented`.
- 2026-05-15: Section A.2 first-pass had two silent-drop bugs caught
  while smoke-testing B1. Wave one round-tripped the placeholder tree
  item's XML through `ProduceXml(false)` -> splice -> `ConsumeXml`; the
  in-memory schema for placeholder parameters is undocumented and
  `ConsumeXml` accepted the input without applying it. Wave two
  bypassed `ConsumeXml` and edited the consumer `.plcproj` directly but
  used a schema (`<ParameterValues>/<Parameter Name=>`) that doesn't
  match what XAE writes; the build accepted the file but the runtime
  ignored the override. The actual on-disk schema, reverse-engineered
  from the IDE's own output, is:

  ```xml
  <Parameters>
    <Parameter ListName="GVL_PARAM_TCUNIT" xmlns="">
      <Key>XUNITENABLEPUBLISH</Key>
      <Value>TRUE</Value>
    </Parameter>
  </Parameters>
  ```

  `<Parameters>` sits in the MSBuild namespace; each `<Parameter>` child
  resets to the empty namespace via `xmlns=""`; `ListName` carries the
  host parameter-list GVL name UPPERCASED; `<Key>` uppercased, `<Value>`
  verbatim; one `<Parameter>` per (ListName, Key).
  `Set-TcPlcProjPlaceholderParameters` writes exactly that.
  - **Lesson:** Placeholder parameters via `ConsumeXml` accept input
    silently without applying it (in-memory schema undocumented). Edit
    `.plcproj` on disk after `AddPlaceholder` instead, using the exact
    schema XAE produces. Pin the splice with a Pester suite so this
    can't silently regress.
  - **Lesson:** Order matters: `AddPlaceholder` -> `Save-TcSolution`
    (flush the basic block) -> close -> splice on disk -> reopen, so
    the in-memory model picks the change up before the next
    `File.SaveAll` can regenerate from a stale tree.
  - **Lesson:** `%TC_BOOTPRJPATH%` expands to different roots on
    kernel-RT vs UmRT bench setups; the hard-coded kernel-RT default
    in `Get-TcUnitDefaultXmlPath` was wrong on UmRT. Per-machine
    override via `TCKIT_TCUNIT_XML_PATH` (documented in `.env.example`).
- 2026-05-17: Section C's TcUnit cascade shipped, but the path-resolution
  side was incomplete. T1 surfaced this as `tests: 0` from
  `get_test_results` plus a 9x cost on the bench (49 vs 7 calls, 17,667
  vs 2,014 tokens) because the model iterated through deploy + run
  cycles trying to find ground truth. The `TCKIT_TCUNIT_XML_PATH`
  override existed but wasn't documented anywhere and the bridge
  defaulted to the kernel-RT path even when only a UmRT runtime was
  installed. The four follow-on fixes in
  [ADR-0011](0011-tcunit-results-path-resolution-and-cold-start-recovery.md)
  close that loop: UmRT auto-detect with mtime tiebreak, `run_tests`
  failure-first inline payload, `add_library_placeholder` idempotency,
  and `save_plc_as_library` cold-start retry. See bench finding
  [2026-05-16-t1-schmitt-trigger-pair](../bench/findings/2026-05-16-t1-schmitt-trigger-pair.md).
