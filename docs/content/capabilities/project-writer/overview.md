# Writer

Structural writes go through one of two backends, selected once per session:

- **automation** (default on Windows) routes every change through Beckhoff's Automation Interface. Requires TcXaeShell open with the solution loaded; changes land via the same mechanism the IDE uses.
- **xml** (default elsewhere; opt in with `TCKIT_WRITER=xml`) deterministically edits the project files on disk in the same shape XAE writes them. No TwinCAT install needed, so writes work on Linux and in CI. The target solution comes from `OpenProject` (or the `TCKIT_SOLUTION` environment variable for one-shot hosts like the CLI).

Either way the tools are the writer; never edit `.TcPOU` / `.plcproj` files by hand alongside them. The backend is fixed for the session — mixing them mid-session risks XAE's next save reverting on-disk edits it never saw.

All write tools take an optional `plcName` to target one PLC project in a multi-PLC solution (`PLC_PROJECT_NAME` sets a session default); an ambiguous call returns an error listing the candidates.

Three tools need XAE machinery and fail with a clear error on the xml backend: `CreateProject` and `AddPlcProject` (project templates) and `SavePlcAsLibrary` (the compiler). The xml backend's `AddLibraryReference` also cannot check the library is installed; a wrong name surfaces at build time instead of write time.

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

The xml backend earns the same trust differently: it emits exactly the file shapes XAE writes (verified verb by verb against the automation backend on a live TwinCAT install), maintains the `.plcproj` compile items with every structural change, and refuses anything it cannot do deterministically.
