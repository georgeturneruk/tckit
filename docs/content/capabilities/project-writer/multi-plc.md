# multi-PLC sln authoring + library tools

**Port methods:** `add_plc_project`, `save_plc_as_library`, `add_library_reference`, `add_library_placeholder`
**MCP tools:** `mcp__tckit__add_plc_project`, `mcp__tckit__save_plc_as_library`, `mcp__tckit__add_library_reference`, `mcp__tckit__add_library_placeholder`
**Bridge harness:** `Add-TcPlcProject.ps1`, `Save-TcPlcAsLibrary.ps1`, `Add-TcLibraryReference.ps1`, `Add-TcLibraryPlaceholder.ps1`
**Status:** Phase B of ADR-0009 — adapter + bridge wired; integration tests exercise both reference styles end-to-end.
**Requires:** TwinCAT 4026+ (the documented automation interface entry points target the current major).

## What this is for

Authoring a sln that holds two or more PLC projects with a library reference between them — the typical Library + Application or Library + Tests split. The standard `create_project` only authors a single-PLC sln; this page covers the rest of the workflow.

## End-to-end recipe

```python
# 1) Create the sln + first PLC project (the library).
writer.create_project("MyProj", "C:/work")
sln_path = "C:/work/MyProj.sln"

# 2) Author the library source in the first PLC project (auto-named after sln).
writer.add_pou("FB_Adder", POUType.FUNCTION_BLOCK, "...", plc_name="MyProj")

# 3) Save it as a .library and install it into the system repo.
writer.save_plc_as_library(
    "MyProj", "C:/work/MyProj.library", install=True
)

# 4) Add a second PLC project (the consumer).
writer.add_plc_project(sln_path, "Tests")

# 5) Add a library reference from Tests to the just-installed library.
writer.add_library_reference("Tests", "MyProj")

# 6) Author the consumer source against the library.
writer.add_pou("FB_TestAdder", POUType.FUNCTION_BLOCK, "...", plc_name="Tests")

# 7) Build the consumer. The reference resolves against the installed library.
builder.build(sln_path, plc_name="Tests")
```

## Build orchestration: save+install before consumer build

A compiled library reference resolves against the *installed* library, not the source. After editing the library's source, you must `save_plc_as_library(install=True)` before rebuilding the consumer; otherwise the consumer build picks up stale code.

The `tc-build-test-loop` skill documents this rule for the model side. The bench harness (per ADR-0007) automates it on the validation side. Operators outside TcKit's loaded-skill scope read the rule via the [portable CLAUDE.md template](https://github.com/georgeturneruk/tckit/blob/main/templates/twincat-claude.md).

If the solution uses Source-Only references instead (resolved automatically by TwinCAT's build), this step is unnecessary. The save+install path is idempotent — running it on a Source-Only project is harmless.

## What is and isn't in scope

In scope (v1):

- Adding a second (or further) PLC project to an existing sln, project type `"standard"`.
- Saving an in-sln PLC project as a `.library` file in source form.
- Installing the resulting library into the standard system repository.
- Adding a compiled library reference between two PLC projects in the same sln.

Out of scope (deliberate):

- TwinCAT 4026 Source-Only library references. The automation interface entry point is not publicly documented; ADR-0009 chose the compiled-library path instead, which is equivalent in build behaviour for in-sln references and uses only documented methods.
- `.compiled-library` (encrypted) output. Not exposed by the automation interface.
- Cross-sln library references. Same-sln only.
- Non-default library repositories. `repository="System"` only in v1; other values return an explicit error.
- `add_plc_project(project_type="library")`. Reserved; returns an explicit error in v1.

## Distributor string

`add_library_reference` defaults `distributor="Tc3 Project"`, which is the conventional company string for libraries produced from a PLC project via `SaveAsLibrary`. If a project's company info differs (or the library was published with a different distributor), pass the actual value explicitly. Watch for "library not found" errors after `add_library_reference` — these usually mean a distributor mismatch.

## Hard reference vs placeholder

Two reference styles, two tools. They produce different on-disk XML and resolve differently at build time.

`add_library_reference` produces a `<LibraryReference>` element pinned to a specific library by name + version + distributor. Use this for libraries you produced in the same sln via `save_plc_as_library`.

`add_library_placeholder` produces a `<PlaceholderReference>` element with a `<DefaultResolution>` that the IDE can re-point without editing the reference. Use this for libraries conventionally referenced via a placeholder — `TcUnit`, `Tc2_System`, `Tc2_Standard`, `Tc3_Module`, `Tc2_Utilities`. This is what the IDE writes when an operator picks "Add Library..." and chooses a placeholder-style library.

```python
# Placeholder reference to TcUnit (distributor matters — it's not the
# Beckhoff one and there is no universally-correct default).
writer.add_library_placeholder(
    "Tests", "TcUnit", "TcUnit", distributor="www.tcunit.org"
)
```

For project-derived libraries the choice is yours; the bench uses the hard reference because the Library PLC is part of the same sln and the version doesn't need to float. For external system libraries, always use the placeholder so the project matches what the IDE would have written.

## Why this design

`ITcSmTreeItem.CreateChild`, `ITcPlcIECProject.SaveAsLibrary`, `ITcPlcLibraryManager.InstallLibrary`, `ITcPlcLibraryManager.AddLibrary`, and `ITcPlcLibraryManager.AddPlaceholder` are all documented entry points on the TwinCAT automation interface ([infosys: PLC projects](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242730891.html), [infosys: SaveAsLibrary](https://infosys.beckhoff.com/content/1031/tc3_automationinterface/242876683.html), [infosys: ITcPlcLibraryManager](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242879627.html), [infosys: AddPlaceholder](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242882699.html)). The bridge harness drives them through PowerShell COM dispatch, in the same style as the rest of the writer surface. No `.plcproj` XML synthesis, no IDE-only workflows.
