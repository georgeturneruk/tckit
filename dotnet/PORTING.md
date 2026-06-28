# Tool porting checklist (ADR-0015)

Tracks the per-tool port from the Python TcKit to the C# server. Order: readers
and ADS/hardware first (most dependency coverage, lowest risk), the Automation
Interface authoring lane last (hardest, most banked behaviour). Each tool is
done only once it passes the parity oracle (see `oracle/`) against a fixture.

This is a local tracking surface; promote to a GitHub issue/milestone when the
work goes public.

## Readers (port first)

- [ ] get_structure
- [ ] get_pou_interface
- [ ] get_pou_declaration
- [ ] get_pou_item
- [ ] get_gvl
- [ ] get_dut

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

- [ ] open_project
- [ ] create_project

## Authoring (Automation Interface; port last)

- [ ] add_pou
- [ ] add_gvl
- [ ] add_dut
- [ ] add_method
- [ ] add_property
- [ ] add_variable
- [ ] add_folder
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
