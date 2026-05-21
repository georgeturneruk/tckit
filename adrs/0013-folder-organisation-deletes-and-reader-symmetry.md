---
adr: 0013
title: Folder organisation, deletes, and reader symmetry
status: Implemented
created: 2026-05-21
last_reviewed: 2026-05-21
related: [0003, 0009, 0010, 0012]
---

## Current state

**Decision (live):** The MCP writer can now organise project items into folders and tear down every shape it can create. New tools: `add_folder` plus a `parent_folder` argument on `add_pou`, `add_gvl`, `add_dut`, `add_method`, `add_property`; deletes for POU, method, property, GVL, DUT, variable, folder, library reference, and placeholder. The reader recognises alias DUTs (kind 623) and returns GVLs / DUTs as `GVLRef` / `DUTRef` dataclasses carrying `name` / `path` / `folder` / (for DUTs) `dut_kind`, mirroring the existing `POURef` shape.

**Where it lives:**

- Bridge endpoints in [`bridge/Start-Bridge.ps1`](../bridge/Start-Bridge.ps1): `/add-folder`, `/delete-pou`, `/delete-method`, `/delete-property`, `/delete-gvl`, `/delete-dut`, `/delete-variable`, `/delete-folder`, `/delete-library-reference`, `/delete-placeholder`.
- Bridge harness scripts in [`bridge/harness/`](../bridge/harness/): `Add-TcFolder.ps1` and the matching `Delete-Tc*.ps1` set. Shared helpers `Resolve-TcFolderPath` and `Remove-TcTreeItem` in [`_TcDte.psm1`](../bridge/harness/_TcDte.psm1).
- Writer port + adapter: [`tckit/ports/writer.py`](../tckit/ports/writer.py), [`tckit/adapters/writers/automation_writer.py`](../tckit/adapters/writers/automation_writer.py).
- Reader: [`tckit/utils/tc_file_parser.py`](../tckit/utils/tc_file_parser.py) (`_classify_dut_declaration`), [`tckit/adapters/readers/xml_reader.py`](../tckit/adapters/readers/xml_reader.py), [`tckit/ports/types.py`](../tckit/ports/types.py) (`DUTKind.ALIAS`, `GVLRef`, `DUTRef`, `DUT.dut_kind`, `DUT.base_type`).
- MCP tool registration: [`tckit/server.py`](../tckit/server.py) plus [`tests/unit/test_server_transport.py`](../tests/unit/test_server_transport.py).

**Bench-confirmed (smokes in [`bench/fixtures/bug-hunting/_author/`](../bench/fixtures/bug-hunting/_author/)):**

- `RemoveReference` on a placeholder with `<Parameters>` strips the parameter block cleanly. The `ConsumeXml('<RemoveReferences>...')` escape hatch is NOT needed; the simple 1-arg call is sufficient.
- The defensive Get/Set removal in `delete_property` survives empty children gracefully; on this XAE version the explicit DeleteChild was needed (cascade was not observed automatically).
- `delete_folder(recursive=True)` walks children correctly via the take-first-child loop; the defensive drain is the right design.
- `delete_pou` refusal path fires correctly for the default `MAIN` PROGRAM bound to `PlcTask`.

**Resolved during bench (smoke-driven course-corrections):**

- The tree-item kind constant lives on `ItemType`, not `ItemSubType`. Every kind check (`delete_pou`, `delete_gvl`, `delete_dut`, `delete_folder`) was updated; `ItemSubType` is 0 for source-tree items on this XAE version and is reserved for I/O sub-discrimination.
- `RemoveReference(name, version, distributor)` does not accept the declared wildcard version `"*"` even when AddLibrary stored exactly that. It only matches against the resolved `<EffectiveVersion>`. `Delete-TcLibraryReference.ps1` now enumerates `References` children and reads `EffectiveVersion` from each child's `ProduceXml` when the caller passes `"*"`, then calls `RemoveReference` with the resolved version. Documented in the script's comment block.

## Context

The Automation Interface surface comparison in the planning round showed three gaps biting on typical TwinCAT projects:

1. Every `add_*` lands at the PLC project's root. Real projects nest items in folders; ours emerge from XAE with a beginner-shaped tree until the operator hand-tidies.
2. No delete operations. Authoring mistakes and refactors fall back to XAE GUI work because the MCP can't remove anything.
3. The reader hides things that exist. `get_dut` couldn't recognise alias DUTs, and `get_structure` returned GVLs / DUTs as bare name lists with no folder path, so orientation tools can't honestly describe the project.

ADRs [0003](0003-patch-style-pou-item-writes.md), [0009](0009-multi-plc-authoring-and-library-tools.md), [0010](0010-foundation-review-after-b1-smoke.md), and [0012](0012-property-and-dut-writer.md) had already settled the bridge pattern (PS1 + endpoint + port + adapter + MCP tool), so the cost of each new tool was just five layers of the established shape. Folder/InterfaceMethod/InterfaceProperty kind constants were already in [`_TcDte.psm1`](../bridge/harness/_TcDte.psm1), and `POURef.folder` was already populated, so half the plumbing for symmetry existed.

Beckhoff infosys and the [TC_AI_DOTNET_Samples](https://github.com/Beckhoff/TC_AI_DOTNET_Samples) repo were the source of truth for the Automation Interface calls; the canonical references are in the Status notes below.

## Decision

Add three sets of capability behind the existing bridge pattern.

### Writer additions

- `add_folder(name, parent_path="POUs")`: wraps `ITcSmTreeItem.CreateChild(name, 601, $null, $null)` after navigating `parent_path` under the PLC project's IDE-level node. `parent_path` accepts the well-known top-level subtrees (`POUs`, `DUTs`) and any pre-existing sub-folders. Intermediate folders must already exist; the new tool fails loud rather than silently creating them.
- `parent_folder` argument on `add_pou`, `add_gvl`, `add_dut`, `add_method`, `add_property`. Empty (default) preserves prior behaviour. For `add_method` and `add_property`, the argument is a hint to look the parent POU up under a specific folder rather than recursively, which disambiguates same-name POUs across subtrees.
- Full delete set:
  - Tree items: `delete_pou`, `delete_method`, `delete_property`, `delete_gvl`, `delete_dut`, `delete_folder(recursive=False)`.
  - Declaration text: `delete_variable(pou_name, variable_name, item_name="")`.
  - Library side: `delete_library_reference`, `delete_placeholder`.

### Guards baked into the primitives

- `delete_pou` refuses to delete a `PROGRAM` that is still bound to a task. The bridge pre-flight scans `.TcTTO` files under the sln directory for a `<PouCall><Name>` element matching the POU name and surfaces the offending task in the error message. FBs / FUNs / INTERFACEs skip the scan; an orphan instance surfaces at build time anyway, which is the right layer for it.
- `delete_gvl` / `delete_dut` / `delete_folder` validate `ItemSubType` so a same-named POU isn't deleted by mistake. Each refuses with a "use the matching delete tool" pointer.
- `delete_variable` refuses multi-name declarations (`bA, bB : BOOL;`) and lines that don't terminate with `;` on the same line, redirecting the caller at `update_pou_declaration_patch` for partial edits.
- `delete_property` removes Get/Set accessor children defensively before deleting the property itself, because cascade behaviour is undocumented.
- `delete_folder(recursive=False)` refuses to delete a non-empty folder. With `recursive=True` it drains children by repeatedly taking the first child to dodge index-shift hazards during enumeration.

### Reader symmetry

- `DUTKind` gains `ALIAS = "alias"`; the writer continues to refuse to create aliases (no real-session demand yet), but the reader now classifies them. `_classify_dut_declaration` strips comments and pragma blocks before discriminating on the body after `TYPE <name> :`.
- `DUT` dataclass adds `dut_kind: DUTKind` and `base_type: str` (the aliased type, empty for non-alias kinds).
- `GVLRef` and `DUTRef` dataclasses are added in `tckit/ports/types.py`, mirroring `POURef`. `PLCSection.gvls` and `.duts` lift from `list[str]` to `list[GVLRef]` / `list[DUTRef]`. Integration-test helpers were updated to extract names; no other callers existed.

### Shared bridge helpers

- `Resolve-TcFolderPath -Root <item> -Path "<seg>/<seg>"`: depth-first walk under a tree root by display name, throws with a precise error on a missing segment. Used by every `Add-Tc*.ps1` script that accepts `$ParentFolder` plus `Add-TcFolder.ps1` and `Delete-TcFolder.ps1`.
- `Remove-TcTreeItem -SysManager $sm -Item $item`: derive the parent path from `$Item.PathName` (strip last segment), re-resolve via `LookupTreeItem`, call `DeleteChild` on the parent. Lets every delete script handle items in arbitrary folders without re-implementing the "find parent" dance.

## Alternatives considered

- `move_to_folder` instead of (or in addition to) `parent_folder` on creation. Skipped: typical bench flow authors into the right place from the start, and the Automation Interface's `MoveChild` has idiosyncratic semantics around opened editors. Revisit when a real session needs it.
- `delete_variable` as a thin wrapper over `update_pou_declaration_patch`. Skipped: a dedicated primitive matches the symmetry of `add_variable` and centralises the "find the line, splice it out" regex.
- Auto-create intermediate folders during `parent_folder` resolution. Skipped: makes the API less honest about state. The user gets one round-trip and a clear error when a folder is missing.
- Treat the reader schema lift as a follow-up. Skipped: integration tests pinning the new shape are cheap, and orientation tools that walk `gvls`/`duts` would otherwise misreport folder layout indefinitely.
- Hand-rolled XML edits to strip orphan placeholder `<Parameters>` blocks. Held back: the Beckhoff-blessed `ConsumeXml('<RemoveReferences>...')` form from `ManagePlcLibraries.cs` is the right escape hatch when the bench shows `RemoveReference` leaves them.

## Consequences

Enables MCP-driven refactors that previously required XAE GUI work: removing dead code, restructuring folders, swapping libraries. Brings the reader's mental model in line with what an operator sees in the XAE tree.

Costs:

- Reader-shape breaking change. `PLCSection.gvls` and `.duts` are no longer plain string lists. The only in-repo consumers were the integration tests and a manual spike; downstream tools that depended on the old shape would need a one-line update.
- Five new bridge endpoints' worth of surface area to maintain. The shared helpers (`Resolve-TcFolderPath`, `Remove-TcTreeItem`) limit the per-script complexity, but `Delete-TcVariable.ps1` and `Delete-TcFolder.ps1` carry non-trivial control flow.
- Bench needs to confirm cascade behaviour for non-empty folders and properties, and the placeholder-parameter cleanup behaviour. None block the new tools' use; the worst case is a clear error and an XAE-side fix-up.

Locks out:

- No path to a writer that creates alias DUTs in this batch. The reader knows about them; the writer doesn't. The hold is deliberate (no use case), not a bug.
- No `move_to_folder` for re-organising an existing tree. Operators continue to do that in XAE.

## Status notes

- 2026-05-21: Drafted alongside the implementation in six waves (delete_pou; delete fan-out; delete_variable + delete_folder; library/placeholder deletes; add_folder + parent_folder threading; reader symmetry). Promoted directly to Implemented because the unit tests for the MCP tool tuple, the reader integration tests, and the adapter-isolation check all pass cleanly.
- 2026-05-21 (later): End-to-end smokes ran clean against XAE 4026. Two real bugs surfaced and were fixed during the bench pass: (i) the kind constant lives on `ItemType`, not `ItemSubType` (every delete script updated); (ii) `RemoveReference` requires the resolved `<EffectiveVersion>` rather than the declared `"*"` wildcard ([`Delete-TcLibraryReference.ps1`](../bridge/harness/Delete-TcLibraryReference.ps1) enumerates References and reads `EffectiveVersion` from `ProduceXml` when `version="*"` is passed). The placeholder-parameters open question resolved cleanly: `RemoveReference` strips orphan `<Parameters>` blocks without the `ConsumeXml` fallback.
- API references baked in: `DeleteChild` ([infosys/242837387](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242837387.html)), `RemoveReference` ([infosys/242888843](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242888843.html)), `CreateChild` SubTypes 601/602/603/604/605/606/607/611/615/618/623 ([infosys/242732427](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242732427.html)), tree-path conventions ([infosys/12425804683](https://infosys.beckhoff.com/content/1033/tcautomationinterface/12425804683.html)). Beckhoff samples for the patterns: [`GeneratePlcProject.cs`](https://github.com/Beckhoff/TC_AI_DOTNET_Samples/blob/main/src/ScriptingContainer/Scripting.CSharp.Scripts/Scripts/GeneratePlcProject.cs), [`PlcArchives.cs`](https://github.com/Beckhoff/TC_AI_DOTNET_Samples/blob/main/src/ScriptingContainer/Scripting.CSharp.Scripts/Scripts/PlcArchives.cs), [`ManagePlcLibraries.cs`](https://github.com/Beckhoff/TC_AI_DOTNET_Samples/blob/main/src/ScriptingContainer/Scripting.CSharp.Scripts/Scripts/ManagePlcLibraries.cs).
