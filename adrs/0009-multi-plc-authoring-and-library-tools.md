---
adr: 0009
title: Multi-PLC sln authoring + library tools (writer port)
status: Implemented
created: 2026-05-14
last_reviewed: 2026-05-18
issue:
pr: 71
related: [0005, 0007, 0010, 0011]
---

## Current state

**Decision (live):** Four writer-port methods, four bridge handlers, four MCP
tools. Original three:
- `add_plc_project(sln_path, plc_name, project_type="standard")`:
  `LookupTreeItem("TIPC")` + `CreateChild`.
- `save_plc_as_library(plc_name, output_path, install=True, repository="System")`:
  `ITcPlcIECProject.SaveAsLibrary`. Cold-start retry shipped under ADR-0011.
- `add_library_reference(consumer_plc_name, library_name, version="*",
  distributor="Tc3 Project")`: `ITcPlcLibraryManager.AddLibrary`.

Plus `add_library_placeholder(...)` (`AddPlaceholder` + parameter splice;
made idempotent by ADR-0011 fix 3). Stable per-fixture library names
(`B1Library`, `B2Library`) and `bUninstallOldVersion=true` keep the shared
system repo clean on re-runs. On-disk layout: each TwinCAT project gets its
own full `.tsproj` template (the PLC-only `.tspproj` skeleton crashes XAE
on `Solution.Open`); PLC tree-item name distinct from sln/VS-project name
to avoid `RPC_E_CALL_REJECTED` from same-name objects at different tree
levels.

**Where it lives:** `tckit/ports/writer.py`,
`tckit/adapters/writers/automation_writer.py`,
`bridge/harness/{Add-TcPlcProject,Save-TcPlcAsLibrary,Add-TcLibraryReference,Add-TcLibraryPlaceholder}.ps1`.
`tckit/utils/plc_resolver.py` resolves multi-sysmanager nodes.

## Context

ADR-0007 needs to author fixtures with a library + tests split. Three writer
surface gaps surfaced during planning: no way to add a second `.plcproj` to
an existing `.sln`, no way to save a PLC as a library file and install it,
no way to add a library reference between PLCs. Four documented automation
interface methods cover the gap (`CreateChild`, `SaveAsLibrary`,
`InstallLibrary`, `AddLibrary`).

## Decision

Three writer-port methods (`add_plc_project`, `save_plc_as_library`,
`add_library_reference`) + three bridge handlers + three MCP tools. Keyword-
only after positional names per ADR-0005. Skill + template carry the
orchestration rule (re-save the library before rebuilding consumers).

## Alternatives considered

- TwinCAT 4026 Source-Only references: no public automation entry point.
- `SaveAsCompiledLibrary` (encrypted): not exposed by automation.
- `add_plc_project_with_library_reference` combo: bench is one caller; others
  want the primitives separately.
- Cross-sln library references: out of scope for v1.
- `.plcproj` XML synthesis: breaks the never-edit-XML rule.

## Consequences

**Enables:** ADR-0007, any library+application or library+test split.

**Costs:** four port methods / four bridge scripts / four adapter methods /
four MCP tools. Shared system library repo accumulates state; mitigated by
stable names + `bUninstallOldVersion=true`.

**Locks in:** TwinCAT 4026+ for these methods.

**Locks out:** nothing.

## Status notes

- 2026-05-14: Implementation outcome (PR #71). Two parameter defaults remain
  spike-by-implementation (distributor `"Tc3 Project"`, references-tree
  path); covered by an integration test rather than re-derived.
  `add_library_placeholder` added as a follow-on
  (`ITcPlcLibraryManager.AddPlaceholder`); distributor defaults to empty
  string (matches documented API default), explicit for non-system libs
  (`"www.tcunit.org"` for TcUnit, `"Beckhoff Automation GmbH"` for Tc2/Tc3).
- 2026-05-14: Multi-PLC layout reversed in PR #81. Original attempt used one
  PLC-only `.tspproj` with two PLCs stacked under one `<Plc>`; on-disk that
  was a 4-line skeleton without the System Manager `<Instance>` block, and
  XAE segfaulted (`AccessViolationException` in `TwinCAT System Manager.x64.dll`)
  on `Solution.Open`. Switched to one full `.tsproj` per TwinCAT project,
  multiple TwinCAT projects per sln, sibling names suffixed `_Tc` so the
  wrapper name doesn't collide with the PLC's. `_TcDte.psm1` gained
  `Get-TcSysManagers` (plural) + `Get-TcSysManager -PlcName` so the 12
  downstream harness scripts can pick the right sysmanager. `File.SaveAll`
  after structural mutations (Solution.SaveAs alone doesn't flush
  System/Plc/Instance trees).
- 2026-05-14: Renamed default first PLC from `${SlnName}` to `${SlnName}_Plc`.
  Same-name objects at different tree levels (sln node, VS Project, first
  PLC under TIPC) crash TcXaeShell on load with `RPC_E_CALL_REJECTED` /
  `MK_E_UNAVAILABLE`.
- 2026-05-17: `save_plc_as_library` cold-start failure
  (`PlaceholderReference/EffectiveResolution`) covered by ADR-0011 fix 4
  (catch the exception, run `CheckAllObjects`, retry once).
