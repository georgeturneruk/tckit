---
adr: 0010
title: Foundation review — writer port and bridge surface after the B1 smoke
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
modelled this properly" call we deferred. We are still pre-1.0 and the
user explicitly OK'd breaking changes, so this is the right moment to
inventory what we papered over and decide which pieces to rebuild
before more consumers grow against them.

The themes group naturally. Each is "we have a thing we wrote around
because the port or the bridge doesn't actually model it"; each has at
least one concrete forward path.

## Decision

This ADR is in `Exploring` status. The shape of the foundation
rebuild is on the table here; specific decisions land as separate
ADRs (or this one gets promoted to `Proposed` with a recommended
direction once we agree).

### A. Writer-port completeness

What's not modelled today but is being asked for by every consumer:

1. **GVL authoring.** `POUType` deliberately scopes to function
   blocks, functions, programs, and interfaces; GVLs aren't POUs.
   [`bench/fixtures/bug-hunting/_author/_common.py:_add_gvl`](../bench/fixtures/bug-hunting/_author/_common.py)
   punches through to the bridge's `/pou` route with
   `PouType: "gvl"` because every fixture needs one. The right move
   is a first-class `ProjectWriter.add_gvl(name, code, plc_name=)` plus
   a matching `/gvl` route. The `/pou` route should reject `gvl` as a
   `PouType` once the dedicated path exists.

2. **Library parameter overrides.** TcUnit's xUnit XML publisher is
   gated on `GVL_Param_TcUnit.xUnitEnablePublish` (a `VAR_GLOBAL
   CONSTANT` on a parameter list). The IDE's "Library parameters"
   dialog serialises overrides as `<Parameter Name="...">value</Parameter>`
   inside the consumer's `.plcproj` `<PlaceholderReference>` block.
   We don't expose this anywhere. Without it, every TcUnit
   consumer falls back to symbol probes or post-hoc plcproj edits.

   Two implementation routes for the bridge:
   - Extend `add_library_placeholder` to take an optional
     `parameters: dict[str, str]` argument; the harness writes them
     via the placeholder tree item's `ConsumeXml`.
   - Add a discrete `add_library_parameter(consumer_plc,
     placeholder, name, value)` so each override is its own call.

   The first reads better for fixture scripts; the second composes
   better for iterative tuning. Either way the underlying COM call
   is the same (`ITcSmTreeItem.ConsumeXml` on the placeholder, per
   [infosys](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242733963.html)).

3. **POU body shape is implicit.** `update_pou_item` takes a single
   `code` blob. The bridge's
   [`Update-TcPouItem.ps1`](../bridge/harness/Update-TcPouItem.ps1)
   splits it at `END_VAR` into declaration and implementation. The
   port doesn't expose `declaration` / `implementation` as
   separate fields, and callers who want only the body have to
   include the declaration too. Worse, "target the POU's own
   decl+impl rather than a method body" is encoded as the magic
   convention `item_name == pou_name` (or empty). Options: split
   `update_pou_item` into `update_pou_declaration` /
   `update_pou_implementation` /
   `update_method_body` with explicit semantics, OR accept
   `declaration` / `implementation` as separate keyword arguments
   on a single method.

4. **`add_method` parser footgun.** Issue
   [#84](https://github.com/georgeturneruk/tckit/issues/84):
   `Split-TcCode` treats a method body as part of the declaration
   when there's no `END_VAR`. The bench's author scripts all
   include empty `VAR/END_VAR` as a workaround. Fix is upstream of
   this ADR (in the bridge splitter) but worth tracking here so
   the workaround can finally come out.

5. **`save_plc_as_library` is not idempotent.** It refuses to
   overwrite an existing `.library`; callers have to delete the
   stale artefact first ([`smoke_B1.py`](../bench/fixtures/bug-hunting/_author/smoke_B1.py)
   does this; future bench runs need to do the same). Either an
   `overwrite=True` flag or unconditional replacement.

### B. Bridge surface — implicit and leaky bits

1. **PLC autostart on `/deploy` is implicit.** `Invoke-TcDeploy.ps1`
   unconditionally calls `BootProjectAutostart = $true` and
   `GenerateBootProject($true)` before activating. This is the
   right default for the bench (without it the PLC sits
   loaded-but-stopped and serves no ADS symbols) but consumers
   that want to control autostart explicitly have no way out. A
   `BootAutostart: bool = true` payload field would surface it.

2. **HTTP-timeout defaults don't match COM-operation latencies.**
   `XaeComBuilder.deploy` and `start_runtime` were using 60s
   defaults against operations that routinely take 90-300s on a
   cold target. We fixed those two; every adapter that talks to
   the bridge needs auditing. A central `bridge_route_timeout(route)`
   mapping in `tckit/utils/bridge_client.py` would be sturdier
   than per-method overrides.

3. **`ConvertTo-HashtableDeep` is doing something undiagnosed to
   parameters literally named `Probes`.** We renamed to
   `ReadSymbols` and moved on, but the root cause (PowerShell 5.1
   parameter binding plus the bridge's request decoder)
   eats arbitrary keys silently. Worth a small repro and a
   targeted fix; otherwise the next route to take a string param
   may trip the same wire.

4. **Magic tree paths.** `TIPC^<plc>` exposes `ITcPlcProject`;
   `TIPC^<plc>^<plc> Project` exposes `ITcPlcIECProject`. The
   distinction is undocumented in our own helpers and we got it
   wrong on first try in `Invoke-TcDeploy.ps1`. A
   `_TcDte.psm1` helper pair (`Get-TcPlcSysNode` vs
   `Get-TcPlcProjectNode`) with the right semantics in their
   doc-blocks would prevent the next session having to rediscover
   it.

### C. Test runner adapter

1. **No symbol-read primitive.** Reading a PLC symbol by name is a
   universally useful operation; right now it only exists as the
   `ReadSymbols` parameter tacked onto `/tcunit-run`. Originally
   I drafted a `/symbol-read` route + `Read-TcSymbol.ps1`, then
   dropped it because adding a route to the bridge requires a
   restart and we didn't want to disrupt the live session.
   Worth landing properly: a `read_symbol(target_ams_id, path)` /
   `read_symbols(target_ams_id, paths)` port method, a `/symbols`
   route, and the `ReadSymbols` parameter on `/tcunit-run` can
   stay as a convenience for the suites-finished-then-probe pattern.

2. **xUnit XML publisher is off by default and we work around it.**
   With (A.2) in place, `_common.py:finalise_fixture` should set
   `xUnitEnablePublish = TRUE` on the TcUnit placeholder so the
   publisher actually writes results. Then `/tcunit-run` gets
   useful per-test detail from the XML for free and `ReadSymbols`
   becomes a fast-path probe rather than the only path.

3. **`xml_path` in TcUnit's GVL is fictional.** `templates/twincat-claude.md`
   tells consumers to declare `TcUnit_ResultExportXmlPath` as a
   `VAR_GLOBAL CONSTANT`. TcUnit doesn't read that name — its
   actual publisher reads `GVL_Param_TcUnit.xUnitFilePath` which
   defaults to `%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml`
   (cross-platform via TwinCAT env-var expansion). The bridge's
   `Get-TcUnitXmlPath` greps the GVL for `TcUnit_ResultExportXmlPath`
   and returns the canonical default if missing. None of this
   chain serves any function. The template, the fixture's
   `GVL_TcUnit`, and `Get-TcUnitXmlPath` should all go away in
   favour of reading `GVL_Param_TcUnit.xUnitFilePath` from the
   target's address space (or just trusting the default).

### D. Template / convention rot

1. **`TcUnit_ResultExportXmlPath` template lie.** Per C.3 — the
   `templates/twincat-claude.md` section about the TcUnit XML
   path needs deleting.

2. **Hardcoded classic-runtime boot paths in author scripts**
   would have been a problem had we not switched the smoke to
   probes. With C.2 + C.3 done, the `%TC_BOOTPRJPATH%` default
   gives us per-runtime portability for free.

## Alternatives considered

- **Status quo: keep the workarounds, document them, move on.**
  Defensible if we expect TcKit to stay an internal bench tool
  forever. Risky if any external consumer ships against the
  current writer port — every workaround becomes a backward-compat
  obligation.

- **One big bang of fixes.** Single PR that adds `add_gvl`,
  reshapes `update_pou_item`, lands library parameters, removes
  the fictional path constant, etc. Cohesive in spirit but the
  diff would be huge and the review surface untestable as a
  single chunk. Easier to land as a sequence of small PRs each
  referring back to this ADR.

- **Wait until a second consumer surfaces.** Rejected: the bench
  itself is the second consumer (after the writer-bench), and the
  B1 work proved that adding a consumer immediately surfaces
  every gap. Better to fix now than retrofit later.

## Consequences

**Enables:** the foundation rebuild this ADR proposes makes the
writer port self-describing (no more magic conventions), removes
fictional template surface, and gives the bench fixtures a clean
authoring chain that doesn't punch through to bridge internals.

**Costs:** every issue above is a small-to-medium PR. The full
sequence is probably 6-10 PRs. Each one breaks one thing the
fixtures or skills currently rely on; coordination matters.

**Locks out:** nothing. Each piece is independent; we can stop at
any point and the partial state is still better than today.

## Status notes

- 2026-05-15: Drafted as `Exploring`. The B1 smoke shipped
  ([#87](https://github.com/georgeturneruk/tckit/pull/87))
  carries the workarounds; this ADR tracks what to do about them.
  Next move is for a session to pick one issue (probably A.2
  library parameters, since unlocking the XML publisher cascades
  into deleting the fictional path constant from C.3 and D.1) and
  propose a concrete shape. Promote this ADR to `Proposed` once a
  rebuild ordering is agreed.
- 2026-05-15: Rebuild ordering agreed; bundled into five PRs
  along natural cohesion lines rather than one PR per item, since
  CI runs slowly and several items overlap on `_common.py` and
  `tckit/ports/writer.py`. Sequence:
    1. **Housekeeping** (Track E + A.4 — debris cleanup + Split-TcCode
       header-only handling).
    2. **xUnit cascade** (A.2 + C.2 + C.3 + D.1 — library-parameter
       primitive, flip `xUnitEnablePublish`, retire
       `TcUnit_ResultExportXmlPath` chain).
    3. **Bridge surface polish** (B.1 + B.2 + B.3 + B.4 — central
       route timeouts, `_TcDte` tree-path helpers, explicit
       `BootAutostart`, `Probes` repro + fix).
    4. **New writer + builder primitives** (A.1 + A.5 + C.1 —
       `add_gvl`, `save_plc_as_library(overwrite=)`, `read_symbols`).
    5. **Split `update_pou_item`** (A.3 — three explicit methods +
       matching patch variants, full bench / skill / template
       migration).
  PR 5 lands solo on top of a quiet base because it rewrites the
  writer port surface every other PR extends. PR 1 + PR 2 + PR 3 +
  PR 4 are mutually independent and can run in flight in parallel.
  ADR promoted to `Proposed` alongside this note.
- 2026-05-15: Wave 1 landed —
  [#89](https://github.com/georgeturneruk/tckit/pull/89) (housekeeping
  + Split-TcCode header-only fix),
  [#90](https://github.com/georgeturneruk/tckit/pull/90) (xUnit
  cascade — promoted this ADR to `Proposed`),
  [#91](https://github.com/georgeturneruk/tckit/pull/91) (bridge
  surface polish), and
  [#92](https://github.com/georgeturneruk/tckit/pull/92) (new writer
  + builder primitives). End-state acceptance defers to PR 5.
- 2026-05-15: PR 5 landed — `update_pou_item` /
  `update_pou_item_patch` split into
  `update_pou_declaration` / `update_pou_implementation` /
  `update_method_body` (+ matching patch variants). New routes:
  `/pou-declaration`, `/pou-implementation`, `/method-body` and the
  three `-patch` siblings. `Update-TcPouItem.ps1` and
  `Update-TcPouItemPatch.ps1` deleted; `_add_gvl` helper retired
  from `_common.py`. Bench `author_B1.py` now splits the MAIN
  declaration / body across two calls, and `smoke_B1.py` patches
  through `update_method_body_patch`. Skill, template, fixture
  TASK.md prompts and writer docs all updated. Closes
  [#40](https://github.com/georgeturneruk/tckit/issues/40) by
  construction. ADR promoted to `Implemented`.
- 2026-05-15: §A.2 first-pass had two silent-drop bugs caught while
  smoke-testing B1 end-to-end. Wave one (PR #90) round-tripped the
  placeholder tree item's XML through `ProduceXml(false)` → splice
  → `ConsumeXml`; the in-memory schema for placeholder parameters
  is undocumented and `ConsumeXml` accepted the input without
  applying it. Wave two bypassed `ConsumeXml` and edited the
  consumer `.plcproj` directly, but used a schema
  (`<ParameterValues>/<Parameter Name=>`) that doesn't match what
  XAE itself writes — the build accepted the file but the runtime
  ignored the override, so the publisher stayed off and the bench
  fell back to symbol probes for `/tcunit-run`. The actual on-disk
  schema, reverse-engineered from the IDE's own output, is
  `<Parameters>/<Parameter ListName="...">/<Key>/<Value>` with
  `xmlns=""` reset on the inner `<Parameter>` and both ListName
  and Key uppercased; one `<Parameter>` element per
  (ListName, Key) pair. `Set-TcPlcProjPlaceholderParameters` now
  writes exactly that. The Python signature for `add_library_placeholder`
  grew with it from `parameters: dict[str, str]` to
  `dict[str, dict[str, str]]` so callers group keys under their
  host parameter-list GVL — e.g.
  `{"GVL_Param_TcUnit": {"xUnitEnablePublish": "TRUE"}}`. Order
  remains `AddPlaceholder` → `Save-TcSolution` → close → splice
  on disk → reopen so the in-memory model picks the change up
  before the next `File.SaveAll` can regenerate from a stale tree.
  Helpers `Set-TcPlcProjPlaceholderParameters` and
  `Find-TcPlcProjFile` in `_TcDte.psm1`, Pester suite under
  `bridge/tests/` pins the splice. Same change set adds the
  `TCKIT_TCUNIT_XML_PATH` env var (documented in `.env.example`)
  so `Get-TcUnitDefaultXmlPath` can be overridden per machine;
  the kernel-RT default is wrong on UmRT bench setups, where
  `%TC_BOOTPRJPATH%` expands to a different root. Validated
  against B1 end-to-end (red→patch→green plus fresh xUnit XML on
  disk). ADR status stays `Implemented`; the cascade intent is
  unchanged, this is a follow-up bug fix.
