# Writer

Structural writes route every change through Beckhoff's Automation Interface: TcXaeShell open with the solution loaded, changes landing via the same mechanism the IDE uses. That makes writes Windows-only by design (a deterministic on-disk XML backend existed briefly, ADR-0017, and was retired by ADR-0019 in favour of a dedicated external tool).

The tools are the writer; never edit `.TcPOU` / `.plcproj` files by hand alongside them, because XAE's next save would revert on-disk edits it never saw.

All write tools take an optional `plcName` to target one PLC project in a multi-PLC solution (`PLC_PROJECT_NAME` sets a session default); an ambiguous call returns an error listing the candidates.

## Create

| Tool | Notes |
|---|---|
| `OpenProject(solutionPath)` | Open (or confirm open) a solution in XAE; idempotent |
| `CreateProject(name, path)` | New solution + standard PLC project (`<name>_Plc`) |
| `AddPlcProject(plcName, solutionPath?)` | Second (or further) PLC project in an existing solution |
| `AddPou(name, pouType, code, parentFolder?)` | `function_block` \| `function` \| `program` \| `interface`; full ST source |
| `AddGvl(name, code, parentFolder?)` | Declaration-only VAR_GLOBAL block |
| `AddDut(name, code, dutKind?)` | `struct` \| `enum` \| `union`; full TYPE source |
| `AddMethod(pouName, methodName, code)` | Full source including METHOD header |
| `AddProperty(pouName, propertyName, returnType, getterCode?, setterCode?)` | At least one accessor |
| `AddVariable(pouName, scope, declaration, itemName?)` | One line into a named VAR block; `itemName` targets a method's locals |
| `AddFolder(name, parentPath?)` | Folder in the source tree |

## Update

| Tool | Replaces |
|---|---|
| `UpdatePouDeclaration(pouName, code)` | FB-level declaration block |
| `UpdatePouImplementation(pouName, code)` | Cyclic body (statements only) |
| `UpdateMethodBody(pouName, methodName, code)` | Full method/action/property source |

Each has a `...Patch(pouName, oldString, newString)` variant: an anchored edit that replaces exactly one occurrence and fails if the anchor is missing or ambiguous. Prefer the patch for small edits; it can't clobber code you didn't look at.

## Delete

`DeletePou`, `DeleteMethod`, `DeleteProperty`, `DeleteGvl`, `DeleteDut`, `DeleteVariable`, `DeleteFolder`. Guards where it matters: a PROGRAM still bound to a task is refused, and a non-empty folder needs `recursive=true`.

## Libraries and multi-PLC solutions

The typical split is a library PLC plus a consumer (application or TcUnit tests) in one solution:

1. `CreateProject` the solution, author the library source.
2. `SavePlcAsLibrary(outputPath, install=true)` to produce and install the `.library`.
3. `AddPlcProject` for the consumer, then `AddLibraryReference(libraryName)` from it.

A compiled reference resolves against the *installed* library, so after editing library source, save + install again before rebuilding the consumer; otherwise the build picks up stale code.

Two reference styles:

- `AddLibraryReference` pins a specific library by name + version + distributor. Use it for libraries you produced in the same solution. Distributor defaults to `Tc3 Project`; a "library not found" after adding usually means a distributor mismatch.
- `AddLibraryPlaceholder` adds a re-pointable placeholder, which is what the IDE writes for external libraries (`TcUnit` with distributor `www.tcunit.org`, `Tc2_Standard`, `Tc3_Module`, ...). `SetPlaceholderParameters` sets library parameter overrides on it, e.g. TcUnit's `xUnitEnablePublish`.

## Why this shape

A TwinCAT project is more than a folder of `.TcPOU` files: GUIDs, cross-references in `.plcproj`, and tree indexes have to stay consistent. If an agent edits XML ad hoc it reasons over two parallel realities, the files on disk and what the IDE thinks the project is. Routing every structural change through one writer keeps one source of truth; the agent never invents GUIDs or reconciles drift.
