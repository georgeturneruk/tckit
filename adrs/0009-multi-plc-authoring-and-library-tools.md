---
adr: 0009
title: Multi-PLC sln authoring + library tools (writer port)
status: Implemented
created: 2026-05-14
issue:
pr: 71
---

## Context

ADR-0007 (bug-hunting bench) needs to author fixtures with a
library + test split: one `.sln` containing two `.plcproj` files,
with the test project referencing the library project. Planning
that ADR surfaced a gap: TcKit's writer surface today cannot
build any of those pieces through documented automation interface
calls.

Three concrete gaps in the writer port and the bridge harness:

- **No way to add a second `.plcproj` to an existing `.sln`.**
  `ProjectWriter.create_project` and its bridge route
  `bridge/harness/New-TcProject.ps1` only author single-PLC
  solutions. They use `LookupTreeItem("TIPC")` + `CreateChild`
  on a fresh sln; that exact call would also add a sibling PLC
  project to an already-open sln, but no writer-port method or
  MCP tool currently exposes it.
- **No way to save a PLC project as a library file and install
  it.** The IDE's "PLC → Save as library and install" command
  is the standard way to produce a `.library` artefact and make
  it available to other projects in the same sln (or any other
  sln on the machine). TcKit has no automation path for either
  step.
- **No way to add a library reference between PLC projects.** A
  consumer PLC project's reference list is what tells TwinCAT to
  pull a library's compiled binary into the consumer's build.
  TcKit has no `add_library_reference` writer method, no bridge
  route, and no XML write path.

Grep of `bridge/harness/` for `Reference`/`AddLibrary`/`InstallLibrary`
confirms zero existing code in this area. Reads do exist: the
XML reader parses library refs out of `.plcproj` files into
`LibraryRef` dataclasses (`tckit/utils/tc_file_parser.py:312-351`),
but write paths are absent.

ADR-0007 is the immediate driver, but the same gaps block any
operator who wants to author a library + application split inside
TcKit instead of by hand in XAE.

Verification against Beckhoff infosys confirmed that the four
automation interface methods needed are all documented:

- `ITcSmTreeItem.CreateChild(name, subType, before, templateOrPath)`
  on the `TIPC` node ([infosys 242730891](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html),
  [242835851](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242835851.html)).
  Already used in `bridge/harness/New-TcProject.ps1:67-68`. The
  existing `bridge/harness/_TcDte.psm1` helpers `Resolve-TcPlcName`
  and `Get-TcPlcProjectNode` already iterate multiple PLC project
  nodes by index.
- `ITcPlcIECProject.SaveAsLibrary(path, install)`
  ([infosys 242876683](https://infosys.beckhoff.com/content/1031/tc3_automationinterface/242876683.html)).
  Saves a PLC project as a `.library` file; when `install` is
  true, the library is also installed into the system repository
  in the same call.
- `ITcPlcLibraryManager.InstallLibrary(repo, path, uninstallOld)`
  ([infosys 242733963](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242733963.html)).
  Standalone install path for cases where the library file
  already exists on disk.
- `ITcPlcLibraryManager.AddLibrary(name, version, distributor)`
  ([infosys 242881163](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242881163.html)).
  Adds a library reference to a consumer PLC project. Version
  `"*"` means latest available.

The TwinCAT 4026 "Source-Only" reference type considered in
earlier rounds is not publicly documented on this surface. The
compiled-library path is equivalent in build behaviour for the
bench's purpose and uses only documented methods.

## Decision

Add three writer-port methods, three bridge harness scripts, and
three MCP tools. Update the `tc-build-test-loop` skill and the
portable CLAUDE.md template with the orchestration rule. Ship as
one PR.

### Port additions

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

Keyword-only arguments (per ADR-0005's signature shape) for
everything except the two positional names that always matter.

`add_plc_project` v1 implements only `project_type="standard"`;
calling with `"library"` returns a clear `Result(success=False,
error="project_type='library' not yet supported")`. The Literal
is in the signature so a later expansion doesn't break callers.

`save_plc_as_library` wraps `ITcPlcIECProject.SaveAsLibrary(path,
install)`. When `install=True`, the library is saved to
`output_path` and installed in the named repository in the same
COM call. `repository="System"` matches the standard TwinCAT
installed-libraries repo; the Phase B implementation confirms the
exact string the 4026 API accepts and adjusts the default if
needed.

`add_library_reference` wraps `ITcPlcLibraryManager.AddLibrary` on
the consumer PLC's library manager. `version="*"` means latest
available — fine for the bench. `distributor="Tc3 Project"` is
the standard string for libraries produced from a PLC project via
`SaveAsLibrary`; the Phase B implementation confirms and adjusts.

### Bridge layer

Three new harness scripts under `bridge/harness/`:

- `Add-TcPlcProject.ps1` — locates the open sln via the existing
  `Get-TcDte`/`Get-TcSysManager` helpers, calls
  `LookupTreeItem("TIPC")`, then `CreateChild($PlcName, 0, $null,
  'Standard PLC Template.plcproj')`. Mirrors the existing
  `New-TcProject.ps1` shape but on an already-open sln rather
  than a fresh one.
- `Save-TcPlcAsLibrary.ps1` — locates the consumer PLC project
  node via `Get-TcPlcProjectNode`, gets the `ITcPlcIECProject`
  interface via the `IECProject` cast, calls
  `SaveAsLibrary($OutputPath, $Install)`.
- `Add-TcLibraryReference.ps1` — locates the consumer PLC
  project node, navigates to its library manager (the
  `References` child item under the PLC project), calls
  `AddLibrary($LibraryName, $Version, $Distributor)`.

Three new routes wired in `bridge/Start-Bridge.ps1`:

- `POST /add-plc-project`
- `POST /save-as-library`
- `POST /add-library-reference`

### Adapter

`tckit/adapters/writers/automation_writer.py` gains three new
methods, each a thin `BridgeClient.post(...)` call. One-rule-clean
— adapters only depend on ports + stdlib + the existing bridge
HTTP plumbing.

### MCP server

`tckit/server.py` exposes the three new tools at the MCP layer.
Tool docstrings include the orchestration rule (call
`save_plc_as_library` before rebuilding a consumer that depends
on a changed library).

### Build orchestration (one level up, not in this ADR)

ADR-0009 ships only the primitives. The orchestration rule —
"if you've edited a Library PLC project that another PLC project
references, call `save_plc_as_library` on it before rebuilding
the consumer" — lives in three places:

- `.claude/skills/tc-build-test-loop/SKILL.md`: a short
  "Multi-PLC builds with library refs" section.
- `templates/twincat-claude.md`: a one-liner of the same advice,
  for projects outside TcKit's loaded-skill scope.
- The bench harness (`bench/post_session.py` per ADR-0007):
  calls `save_plc_as_library` on every Library PLC project in the
  fixture before `/build` + `/tcunit-run`.

If a user happens to use a Source-Only reference in their own
IDE work, that flow is independent: TwinCAT's build dependency
handles it. Our compiled-library flow just adds an explicit save
+install step; it doesn't try to enforce a particular reference
type.

### Library version + repository hygiene

Each library installed by `save_plc_as_library` lands in the
shared system repository. Two practices keep this clean:

- Use a stable, per-fixture library name (`B1Library`,
  `B2Library`, etc.). Different bench tasks don't collide.
- Pass `bUninstallOldVersion=true` to `InstallLibrary` so re-runs
  replace the prior install rather than accumulating versions.

The bench harness may add a cleanup step in `bench/post_session.py`
after each task run; that lives in ADR-0007's scope, not here.

## Alternatives considered

- **TwinCAT 4026 Source-Only library references.** The UI in
  4026 exposes a "Source PLC" reference type that lets the
  consumer build against the library project's source without a
  save+install cycle. Rejected for v1 because the automation
  interface entry point is not publicly documented; would need
  a spike or `.plcproj` XML synthesis. The compiled-library path
  produces equivalent build behaviour for our purpose using only
  documented methods. If a future need surfaces and the API
  documents, this is additive (a new `reference_type` literal
  value); not a teardown.
- **`SaveAsCompiledLibrary` (encrypted libraries).** Not exposed
  by the automation interface. Not needed for the bench. Out of
  scope.
- **Combined `add_plc_project_with_library_reference`.** Tempting
  for the bench's specific flow but rejected — composability is
  better. The bench is one caller; other operators will want the
  primitives separately.
- **Cross-sln library references** (Library is in one sln, Tests
  is in another). Out of scope for v1. Same-sln only.
  Documented in the docs page as a known restriction.
- **`.plcproj` XML synthesis for library refs.** Available as
  a fallback but rejected here — the documented `AddLibrary`
  method works and keeps us inside the "never edit `.plcproj`
  XML directly" rule from `tc-write-st`.

## Consequences

**Enables:** ADR-0007 (bug-hunting bench). Authoring a library +
application split or a library + test split inside TcKit, with no
manual XAE work needed. The portable CLAUDE.md template can carry
the orchestration rule into any downstream TwinCAT project.

**Costs:** Three new port methods, three new bridge scripts,
three new adapter methods, three new MCP tools, one new docs
page, one new integration test, one skill update, one template
update. Roughly the same scope as ADR-0006's PR set. The shared
system library repo accumulates state across runs; mitigated by
stable per-fixture library names and `bUninstallOldVersion=true`.

**Locks in:** TwinCAT 4026+ for these methods (the documented
infosys pages target the current major; older builds may have a
narrower API surface). Documented in the docs page.

**Risks:**

- `SaveAsLibrary`'s output distributor string may differ from
  the assumed `"Tc3 Project"`. Phase B confirms and adjusts the
  `add_library_reference` default. Low-risk shape question.
- `ITcPlcLibraryManager` navigation: the library manager hangs
  off a PLC project's `References` tree node, but the exact
  TIPC path string is undocumented. Phase B confirms by walking
  the children of `TIPC^<plc>^<plc> Project` in an interactive
  PowerShell session against an open 4026 sln.
- Library names that collide with system or vendor libraries
  installed in the same repo would cause `AddLibrary` to resolve
  to the wrong library. Mitigation: per-fixture library names
  in the bench; the docs page warns operators about this.

**Locks out:** nothing structural. Source-Only references can be
added as a new `reference_type` literal value later if the API
becomes documented; cross-sln references can be added as a new
method or an optional `library_path` parameter.

## Status notes

- 2026-05-14: Drafted as `Proposed`. Triggered by ADR-0007
  planning; ADR-0007's status notes describe the rethink that
  surfaced this gap. Verified all four automation interface
  methods are documented on Beckhoff infosys (links in §Context).
  Implementation lands as a single PR; ADR-0007's Phase C0 (the
  pilot fixture authoring) is the first real exercise of these
  tools.
- 2026-05-14: Implemented in
  [#71](https://github.com/georgeturneruk/tckit/pull/71). Port,
  adapter, MCP server, bridge harness scripts, routes, skill +
  template updates, unit tests, integration test, and docs page
  all landed in one squash commit. Two parameter defaults
  (`distributor="Tc3 Project"` on `add_library_reference` and the
  `References` tree-item path inside `Add-TcLibraryReference.ps1`)
  remain spike-by-implementation: the integration test in
  `tests/integration/test_multi_plc_library.py` is the
  validation. If it fails with "library not found" against a real
  4026 install, the distributor needs adjusting from the actual
  `SaveAsLibrary` output; expected as a small follow-up.
- 2026-05-14: Extended writer port with `add_library_placeholder`,
  wrapping `ITcPlcLibraryManager.AddPlaceholder(placeholder_name,
  default_lib, default_version, default_distributor)`
  ([infosys 242882699](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242882699.html)).
  Surfaced as a gap during ADR-0007 Phase C0: the bench's TcUnit
  reference is conventionally a `<PlaceholderReference>`, not a
  `<LibraryReference>`, and `add_library_reference` produces the
  wrong on-disk shape for it. Same shape as `add_library_reference`
  — adapter, MCP tool, bridge route `/add-library-placeholder`,
  harness `Add-TcLibraryPlaceholder.ps1`. Distributor defaults to
  empty string (matching the documented API default); callers must
  pass it explicitly for non-system libraries (`"www.tcunit.org"`
  for TcUnit, `"Beckhoff Automation GmbH"` for Tc2/Tc3). The
  on-disk verification in
  `test_end_to_end_add_library_placeholder` reads the produced
  `.plcproj` and asserts the `<PlaceholderReference>` element
  lands.
- 2026-05-14: Renamed the first PLC produced by `create_project` from
  `${SlnName}` to `${SlnName}_Plc`. Observed during Phase C0 retries:
  when the sln, the VS Project node (the `.tspproj` wrapper) and the
  first PLC under TIPC all share one name, TcXaeShell crashes on
  solution load with `RPC_E_CALL_REJECTED` / `MK_E_UNAVAILABLE` and
  the process dies. Giving the PLC a distinct default name keeps the
  three tree items disambiguated. Change is purely at the harness
  default — `create_project`'s port signature is unchanged, and the
  harness still accepts an explicit `PlcName` parameter to override.
  `add_plc_project` callers are unaffected (they already supply an
  explicit name). The integration test and B1 fixture follow the new
  convention.
- 2026-05-14: **Reverse the multi-PLC layout entirely** in
  [#81](https://github.com/georgeturneruk/tckit/pull/81). The previous
  pattern (one PLC-only `.tspproj`, two PLCs under one `<Plc>` element)
  authored cleanly and built in-memory but crashed
  `TcXaeShell.exe` on every `Solution.Open` from disk with an
  `AccessViolationException` in `TwinCAT System Manager.x64.dll`
  during `IVsParentProject.OpenChildren()`. Root cause: the PLC-only
  `.tspproj` template, written via `Solution.AddFromTemplate`, doesn't
  persist the System Manager `<Instance>` block for additional PLCs;
  the on-disk file is just a 4-line skeleton. The wizard's
  `File → New → TwinCAT XAE Project` path uses a full `.tsproj`
  template, with one PLC per TwinCAT project and additional projects
  added as siblings at sln level. We now match that:
  - `New-TcProject` uses the full template
    (`Components\Base\PrjTemplate\TwinCAT Project.tsproj`) and places
    the `.tsproj` in a subdir named after itself.
  - `Add-TcPlcProject` adds a second TwinCAT project at sln level,
    suffixed `_Tc` so its name doesn't collide with the PLC's;
    same-name objects at different tree levels also crash XAE on
    save.
  - Both call `File.SaveAll` after the structural mutation;
    `Solution.SaveAs` alone doesn't flush `<System>`/`<Plc>`/`<Instance>`
    to disk.
  - In multi-tsproj slns every TwinCAT project exposes its own
    `ITcSysManager`; `_TcDte.psm1` gains `Get-TcSysManagers` (plural)
    and `Get-TcSysManager` takes an optional `-PlcName` to pick the
    sysmanager hosting the named PLC. The 12 downstream harness
    scripts switch to `Resolve-TcPlcName -Dte $dte` followed by
    `Get-TcSysManager -Dte $dte -PlcName $plc`. `Invoke-TcDeploy`
    gains a `-PlcName` parameter for the same reason.
  - `Resolve-TcPlcName`'s port signature is unchanged from a caller
    POV; what changed is what it scans. Callers that don't care
    about the .tsproj wrapping continue to work unchanged.

  ADR-0009's port signatures (`create_project`, `add_plc_project`,
  `save_plc_as_library`, `add_library_reference`,
  `add_library_placeholder`) are unchanged from
  [#71](https://github.com/georgeturneruk/tckit/pull/71); only the
  on-disk shape and the bridge implementation differ. Compiled
  library references still go through the same `AddLibrary` /
  `AddPlaceholder` calls.
