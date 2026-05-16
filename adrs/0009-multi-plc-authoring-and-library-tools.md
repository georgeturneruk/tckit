---
adr: 0009
title: Multi-PLC sln authoring + library tools (writer port)
status: Implemented
created: 2026-05-14
issue:
pr: 71
---

## Context

ADR-0007 (bug-hunting bench) needs to author fixtures with a library +
test split: one `.sln` containing two `.plcproj` files, with the test
project referencing the library project. Planning that ADR surfaced three
concrete gaps in the writer surface and bridge harness:

- **No way to add a second `.plcproj` to an existing `.sln`.**
  `ProjectWriter.create_project` and `New-TcProject.ps1` only author
  single-PLC solutions. The underlying `LookupTreeItem("TIPC")` +
  `CreateChild` call would also add a sibling PLC, but no writer-port
  method or MCP tool exposes it.
- **No way to save a PLC project as a library file and install it.** The
  IDE's "PLC -> Save as library and install" is the standard way to
  produce a `.library` artefact and make it available to other projects;
  TcKit has no automation path for either step.
- **No way to add a library reference between PLC projects.** Tells
  TwinCAT to pull a library's compiled binary into a consumer's build.
  No writer method, no bridge route, no XML write path.

Grep of `bridge/harness/` for `Reference`/`AddLibrary`/`InstallLibrary`
confirms zero existing code in this area. Reads do exist: the XML reader
parses library refs out of `.plcproj` into `LibraryRef` dataclasses
(`tckit/utils/tc_file_parser.py:312-351`).

Four automation interface methods cover the gap and are all documented on
Beckhoff infosys:

- `ITcSmTreeItem.CreateChild(name, subType, before, templateOrPath)` on
  the `TIPC` node
  ([infosys 242730891](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html)).
  Already used in `New-TcProject.ps1`.
- `ITcPlcIECProject.SaveAsLibrary(path, install)`
  ([infosys 242876683](https://infosys.beckhoff.com/content/1031/tc3_automationinterface/242876683.html)).
- `ITcPlcLibraryManager.InstallLibrary(repo, path, uninstallOld)`
  ([infosys 242733963](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242733963.html)).
- `ITcPlcLibraryManager.AddLibrary(name, version, distributor)`
  ([infosys 242881163](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242881163.html)).
  Version `"*"` means latest.

The TwinCAT 4026 "Source-Only" reference type considered in earlier
rounds is not publicly documented on this surface. The compiled-library
path is equivalent in build behaviour for the bench's purpose and uses
only documented methods.

## Decision

Add three writer-port methods, three bridge harness scripts, three MCP
tools. Update the `tc-build-test-loop` skill and the portable CLAUDE.md
template with the orchestration rule. Ship as one PR.

### Port shape

```python
class ProjectWriter(ABC):
    @abstractmethod
    def add_plc_project(
        self,
        sln_path: str,
        plc_name: str,
        *,
        project_type: Literal["standard", "library"] = "standard",
    ) -> Result: ...

    @abstractmethod
    def save_plc_as_library(
        self,
        plc_name: str,
        output_path: str,
        *,
        install: bool = True,
        repository: str = "System",
    ) -> Result: ...

    @abstractmethod
    def add_library_reference(
        self,
        consumer_plc_name: str,
        library_name: str,
        *,
        version: str = "*",
        distributor: str = "Tc3 Project",
    ) -> Result: ...
```

Keyword-only after the positional names that always matter (per ADR-0005).
`add_plc_project` v1 ships `project_type="standard"` only; the Literal is
in the signature so future expansion doesn't break callers.

### Implementation surface

| Layer | Files |
|---|---|
| Bridge harness | `bridge/harness/Add-TcPlcProject.ps1`, `Save-TcPlcAsLibrary.ps1`, `Add-TcLibraryReference.ps1` |
| Bridge routes | `POST /add-plc-project`, `POST /save-as-library`, `POST /add-library-reference` (wired in `Start-Bridge.ps1`) |
| Adapter | `tckit/adapters/writers/automation_writer.py` (three thin `BridgeClient.post` calls) |
| MCP server | `tckit/server.py` (three tools; docstrings carry the orchestration rule) |

The harness scripts mirror the existing `New-TcProject.ps1` shape:
`Add-TcPlcProject.ps1` does `LookupTreeItem("TIPC")` + `CreateChild`;
`Save-TcPlcAsLibrary.ps1` casts to `ITcPlcIECProject` and calls
`SaveAsLibrary($OutputPath, $Install)`;
`Add-TcLibraryReference.ps1` navigates to the consumer's library manager
under the PLC's `References` child and calls
`AddLibrary($LibraryName, $Version, $Distributor)`.

The orchestration rule ("if you've edited a Library PLC that another PLC
references, call `save_plc_as_library` before rebuilding the consumer")
lives in `.claude/skills/tc-build-test-loop/SKILL.md`,
`templates/twincat-claude.md`, and the bench harness
(`bench/post_session.py` per ADR-0007). Not duplicated here.

### Library repository hygiene

Use a stable, per-fixture library name (`B1Library`, `B2Library`) so
different bench tasks don't collide in the shared system repository, and
pass `bUninstallOldVersion=true` so re-runs replace prior installs
rather than accumulating versions.

## Alternatives considered

- **TwinCAT 4026 Source-Only library references.** The UI exposes a
  Source PLC reference that builds against project source without
  save+install. Rejected for v1: the automation entry point isn't
  publicly documented. Additive later via a new `reference_type` value;
  not a teardown.
- **`SaveAsCompiledLibrary` (encrypted libraries).** Not exposed by the
  automation interface. Not needed.
- **Combined `add_plc_project_with_library_reference`.** Tempting for
  the bench's specific flow, but the bench is one caller. Other
  operators want the primitives separately.
- **Cross-sln library references.** Out of scope for v1; same-sln only.
- **`.plcproj` XML synthesis for library refs.** Documented `AddLibrary`
  works and keeps us inside the "never edit `.plcproj` XML directly"
  rule from `tc-write-st`.

## Consequences

**Enables:** ADR-0007. Authoring a library + application split or a
library + test split inside TcKit, no manual XAE work.

**Costs:** Three port methods, three bridge scripts, three adapter
methods, three MCP tools, one docs page, one integration test, one skill
update, one template update. Shared system library repo accumulates
state; mitigated by stable names + `bUninstallOldVersion=true`.

**Locks in:** TwinCAT 4026+ for these methods.

**Risks:**

- `SaveAsLibrary`'s output distributor string may differ from the assumed
  `"Tc3 Project"`. Phase B confirms and adjusts.
- `ITcPlcLibraryManager` lives under the PLC's `References` tree node,
  exact TIPC path string undocumented. Phase B confirms by walking
  children in an interactive PS session.
- Library names colliding with system or vendor libraries in the same
  repo would resolve `AddLibrary` to the wrong library. Mitigation:
  per-fixture names; docs warn operators.

**Locks out:** nothing structural. Source-Only and cross-sln references
can be added later as additive values or parameters.

## Status notes

- 2026-05-14: Drafted as `Proposed`, triggered by ADR-0007 planning.
- 2026-05-14: Implemented in
  [#71](https://github.com/georgeturneruk/tckit/pull/71). Two parameter
  defaults (distributor `"Tc3 Project"` and the `References` tree-item
  path inside `Add-TcLibraryReference.ps1`) remain spike-by-
  implementation; the integration test in
  `tests/integration/test_multi_plc_library.py` is the validation.
- 2026-05-14: Extended writer port with `add_library_placeholder`,
  wrapping `ITcPlcLibraryManager.AddPlaceholder(placeholder_name,
  default_lib, default_version, default_distributor)`
  ([infosys 242882699](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242882699.html)).
  Surfaced as a gap during ADR-0007 Phase C0: the bench's TcUnit
  reference is conventionally a `<PlaceholderReference>`, not a
  `<LibraryReference>`. Distributor defaults to empty string (matching
  the documented API default); callers must pass it explicitly for
  non-system libraries (`"www.tcunit.org"` for TcUnit,
  `"Beckhoff Automation GmbH"` for Tc2/Tc3).
- 2026-05-14: Renamed default first PLC from `${SlnName}` to
  `${SlnName}_Plc`.
  - **Lesson:** Same-name objects at different tree levels (sln node,
    VS Project, first PLC under TIPC) crash TcXaeShell on load with
    `RPC_E_CALL_REJECTED` / `MK_E_UNAVAILABLE`. Give each tree item a
    distinct name. The harness still accepts an explicit `PlcName`
    override.
- 2026-05-14: Multi-PLC layout reversed in
  [#81](https://github.com/georgeturneruk/tckit/pull/81). The previous
  layout (one PLC-only `.tspproj`, two PLCs stacked under one `<Plc>`)
  authored and built in-memory but crashed `TcXaeShell.exe` on every
  `Solution.Open` from disk (`AccessViolationException` in
  `TwinCAT System Manager.x64.dll` during
  `IVsParentProject.OpenChildren()`). The wizard's
  `File -> New -> TwinCAT XAE Project` path uses a full `.tsproj`
  template, one PLC per TwinCAT project, additional projects added as
  siblings at sln level. We now match that. Port signatures unchanged;
  only on-disk shape and bridge layer differ.
  - **Lesson:** A PLC-only `.tspproj` template (used by
    `Solution.AddFromTemplate`) doesn't persist the System Manager
    `<Instance>` block for additional PLCs; the on-disk file is a 4-line
    skeleton. Use the full `.tsproj` template
    (`Components\Base\PrjTemplate\TwinCAT Project.tsproj`) and place
    each TwinCAT project in its own subdir.
  - **Lesson:** Add additional TwinCAT projects at sln level, suffixed
    `_Tc` so the wrapper name doesn't collide with the PLC's
    (same-name objects at different tree levels crash XAE on save).
  - **Lesson:** In multi-tsproj slns every TwinCAT project exposes its
    own `ITcSysManager`. `_TcDte.psm1` gained `Get-TcSysManagers`
    (plural) and `Get-TcSysManager` takes an optional `-PlcName` to
    pick the sysmanager hosting the named PLC. The 12 downstream
    harness scripts switched to `Resolve-TcPlcName -Dte $dte` followed
    by `Get-TcSysManager -Dte $dte -PlcName $plc`; `Invoke-TcDeploy`
    gained a `-PlcName` for the same reason.
  - **Lesson:** `File.SaveAll` after structural mutations.
    `Solution.SaveAs` alone doesn't flush `<System>`/`<Plc>`/
    `<Instance>` to disk.
