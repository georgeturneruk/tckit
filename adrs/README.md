# ADRs

Always-fresh skim list. **Read this file first; descend into a specific ADR
only when its Current state intersects your task.**

Status reflects each ADR's `status:` frontmatter. Summary is the decision in
its current form (post-implementation deviations included).

| #    | Status      | Title                                                | Summary |
|------|-------------|------------------------------------------------------|---------|
| [0001](0001-project-navigation-port.md) | Exploring   | Project navigation port (ProjectSearcher)            | Defer content-search port; stock `Grep` + `get_pou_item` covers most navigation. Reopen narrowly (`find_callers`, `find_instantiations`) when a real session needs it. |
| [0002](0002-project-orientation.md) | Implemented | Project orientation (extended `get_structure`)       | `get_structure` returns task layout (from `.TcTTO`, fallback `.tsproj`), library refs, and folder grouping per POU. `tc-orient-project` skill codifies first-touch navigation. |
| [0003](0003-patch-style-pou-item-writes.md) | Implemented | Patch-style writes for POU items                     | `update_pou_item_patch` (anchor-based Edit-style replace) + `add_variable` + `get_pou_declaration`. Read-modify-write done bridge-side. Later split per ADR-0010 into declaration / implementation / method-body variants. |
| [0004](0004-reader-cache-invalidation.md) | Exploring   | Reader cache invalidation                            | Use `.plcproj` mtime as staleness signal; one `stat()` per read, rebuild on change. Extended to per-`.plcproj` mtime under ADR-0005 multi-PLC. |
| [0005](0005-multi-project-sln-support.md) | Implemented | Multi-project sln support                            | Every project-scoped tool takes optional `plc_name=`; `PLC_PROJECT_NAME` env is the session-wide default. `ProjectStructure.plcs: dict[str, PLCSection]` in every case (single-PLC returns one-entry dict). |
| [0006](0006-test-runner-tcunit-adapter.md) | Implemented | TestRunner adapter for TcUnit                        | Bridge routes `POST /runtime`, `/tcunit-run`, `/results`; adapter is thin route caller. `run_tests` / `get_results` take `target_ams_id` first. xUnit XML gated on `GVL_Param_TcUnit.xUnitEnablePublish`; path resolution per ADR-0011. |
| [0007](0007-bug-hunting-bench.md) | Proposed    | Bug-hunting bench (closed-loop)                      | Fixtures at `bench/fixtures/bug-hunting/<id>/`, one `.sln` per task with library+tests `.plcproj` split. Vanilla vs tckit arms, post-session `/tcunit-run` validates. Active set: B1 (off-by-one) + T1 (Schmitt-trigger TDD) benched; T2 (PID anti-windup TDD) authored, awaiting first paired run. B2-B5 drafted as bug categories then dropped 2026-05-18. |
| [0008](0008-portable-twincat-claude-md.md) | Implemented | Portable TwinCAT CLAUDE.md template                  | `tckit/templates/twincat-claude.md` + topic files under `tckit/templates/twincat/`. `tckit init --with-claude-md` installs; `tckit doctor` nudges when a `.sln` has no sibling. Naming/comments/multi-PLC/cyclic-in-method/polymorphism-arrays/tcunit-tests as topic files. |
| [0009](0009-multi-plc-authoring-and-library-tools.md) | Implemented | Multi-PLC sln authoring + library tools              | `add_plc_project`, `save_plc_as_library`, `add_library_reference`, plus `add_library_placeholder`. Each TwinCAT project gets its own full `.tsproj`; PLC tree-item name distinct from sln/VS-project name. |
| [0010](0010-foundation-review-after-b1-smoke.md) | Implemented | Foundation review after B1 smoke                     | Five-wave rebuild after B1 surfaced cracks: `add_gvl`, library parameters (XAE on-disk schema, not `ConsumeXml`), `Split-TcCode` header-only fix, idempotent `save_plc_as_library`, autostart deploy default, central bridge-route timeouts, `read_symbols` primitive, `update_pou_item` split into declaration/implementation/method-body. |
| [0011](0011-tcunit-results-path-resolution-and-cold-start-recovery.md) | Accepted    | TcUnit results path + cold-start recovery            | UmRT XML auto-detect with mtime tiebreak, `run_tests` returns failure-first inline, `add_library_placeholder` idempotent, `save_plc_as_library` cold-start retry, `set_placeholder_parameters` route, `tckit doctor` TcUnit section. Promote to Implemented after T1 re-bench cycle is fully closed. |
| [0012](0012-property-and-dut-writer.md) | Implemented | Property and DUT writer additions                    | `add_property(pou_name, property_name, return_type, getter_code?, setter_code?)` and `add_dut(name, code, dut_kind=STRUCT)` (struct/enum/union, no ALIAS in v1). Each via its own bridge route + handler; mirrors `add_method` shape. |
| [0013](0013-folder-organisation-deletes-and-reader-symmetry.md) | Implemented | Folder organisation, deletes, and reader symmetry    | `add_folder` plus `parent_folder` on `add_pou`/`add_gvl`/`add_dut`/`add_method`/`add_property`; full delete set (`delete_pou` refuses task-bound PROGRAMs; `delete_variable` is a primitive; `delete_folder(recursive=)` defensively drains children; library/placeholder via `RemoveReference`). Reader gains `DUTKind.ALIAS`, `DUT.base_type`, and `GVLRef`/`DUTRef` with folder paths. |
| [0014](0014-build-diagnostics-and-tcxaeshell-express-limitation.md) | Accepted | Build diagnostics source + TcXaeShell Express limitation | `build` reads PLC diagnostics from the VS Error List: the documented EnvDTE path on full VS, and a UI Automation read of the rendered grid on TcXaeShell Express (where EnvDTE is null). Returns real file/line/code/description/project both ways; Express can't read the severity icon, so it's inferred (compiler code + CheckAllObjects), and falls back to honest pass/fail only when the XAE GUI isn't reachable. Dead ends recorded: compileinfo decode, MSBuild, GenerateBootProject, /log. ErrorList read shipped in #123; Express UIA read added after. |
| [0015](0015-csharp-dotnet-rearchitecture.md) | Accepted | C#/.NET rearchitecture (single in-process MCP server) | Rewritten as one net8 MCP server on `Beckhoff.TwinCAT.Ads` + TwinSharp (ADS/hardware) and the official .NET MCP SDK, with the Automation Interface authoring lane hand-rolled against `TCatSysManagerLib`. Port complete and cutover executed on `feat/csharp-rewrite`: Python package, bridge, and Docker mode deleted; safety stance is the `permissions.json` gate. SSE for separate machines left open, non-gating. Promote to Implemented on merge. |
| [0016](0016-reusable-tckit-ads-library.md) | Accepted | Reusable TcKit.Ads class library | Extract the generic ADS client layer into `dotnet/src/TcKit.Ads` (net8.0, Beckhoff.TwinCAT.Ads only): symbol session with stale-handle policy + enum-aware typed I/O, runtime state ops, licence inventory/preflight (port 30), message-log stream reader (port 100), TcUnit results resolution+parsing. Adapter becomes a thin shell; local folder-feed nupkg, SemVer. |

## Reading order

1. Skim this table. Note any ADR whose summary intersects your task.
2. Open the **Current state** block of relevant ADRs. That block is canonical
   for what currently holds (including post-implementation deviations).
3. Only descend into Context / Decision / Alternatives / Status notes when you
   need rationale.

## Maintenance

When an ADR is created, promoted, or has its Current state updated, the
corresponding row in this table updates in the same edit. The `tc-adr` skill
owns the discipline; this file is the surface.
