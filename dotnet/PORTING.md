# Tool porting checklist (ADR-0015)

Tracks the per-tool port from the Python TcKit to the C# server. Order: readers
and ADS/hardware first (most dependency coverage, lowest risk), the Automation
Interface authoring lane last (hardest, most banked behaviour). Each tool is
done only once it passes the parity oracle (see `oracle/`) against a fixture.

This is a local tracking surface; promote to a GitHub issue/milestone when the
work goes public.

## Readers (port first)

- [x] get_structure — `TcKit.Adapters.Reader`; xUnit + oracle green (sample, multi-PLC, T3)
- [x] get_pou_interface — xUnit + oracle green
- [x] get_pou_declaration — xUnit + oracle green
- [x] get_pou_item — methods, actions, property accessors (`Name.Get`/`.Set`); xUnit + oracle green
- [x] get_gvl — xUnit + oracle green
- [x] get_dut — struct/enum/union/alias + base_type; xUnit + oracle green

The readers share a stateful symbol index built by get_structure (per-PLC name ->
path, with .plcproj mtime staleness, ADR-0005). Hydrating the index from the
solution open in XAE (the Python `active_solution` path) is deferred to the COM
adapter lane; until then call get_structure first in a session.

## ADS / hardware (port early; TwinSharp + Beckhoff.TwinCAT.Ads)

- [ ] read_symbols
- [ ] write_symbols
- [ ] invoke_rpc
- [ ] get_ethercat_status
- [ ] list_ethercat_masters
- [ ] get_ipc_hardware
- [ ] get_axis_state
- [ ] list_axes
- [ ] scan_hardware
- [ ] scaffold_hardware_code

## Build / test / deploy

- [ ] build
- [ ] deploy
- [ ] start_runtime
- [ ] run_tests
- [ ] get_test_results

## Docs (Beckhoff infosys)

- [ ] find_fb
- [ ] search_docs
- [ ] get_doc_page
- [ ] get_doc_status
- [ ] generate_docs

## Project / config

- [~] open_project — built; pending live validation
- [ ] create_project

## Authoring (Automation Interface; port last)

COM foundation in place (`TcKit.Adapters.Automation`), built around an **automation
seam** (`ITcSession` / `ITcSysManager` / `ITcTreeItem`) so the authoring logic is
testable without TwinCAT:

- `ProjectAuthor` — all navigation + verb logic against the seam (COM-free).
- `ComTcSession` / `ComTcSysManager` / `ComTcTreeItem` — the live COM implementation
  (late-bound `dynamic`, `ComRetry`, `GetActiveObject` P/Invoke attach, STA executor).
- A fake seam in the tests encodes the AI behaviour (CreateChild kinds, declaration-only
  GVL/DUT, `^`-path resolution); `StCode` splitter and `TcKind` map are unit-tested too.

The create family below is implemented and **CI-tested against the fake**; the live
COM wrapper still needs an on-XAE smoke (self-cleaning add_pou probe once delete_*
lands), so these stay unchecked until that confirms.

- [~] add_pou — logic CI-tested (fake); pending live COM smoke
- [~] add_gvl — logic CI-tested (fake); pending live COM smoke
- [~] add_dut — logic CI-tested (fake); pending live COM smoke
- [~] add_method — logic CI-tested (fake); pending live COM smoke
- [~] add_property — logic CI-tested (fake); pending live COM smoke
- [ ] add_variable
- [~] add_folder — logic CI-tested (fake); pending live COM smoke
- [ ] add_plc_project
- [ ] add_library_reference
- [ ] add_library_placeholder
- [ ] set_placeholder_parameters
- [ ] save_plc_as_library
- [ ] update_pou_declaration
- [ ] update_pou_implementation
- [ ] update_method_body
- [ ] update_pou_declaration_patch
- [ ] update_pou_implementation_patch
- [ ] update_method_body_patch
- [ ] delete_pou
- [ ] delete_gvl
- [ ] delete_dut
- [ ] delete_method
- [ ] delete_property
- [ ] delete_variable
- [ ] delete_folder
- [ ] delete_library_reference
- [ ] delete_placeholder
