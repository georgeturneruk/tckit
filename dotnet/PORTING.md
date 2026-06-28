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

The COM half (build, deploy) rides the same automation seam + STA layer as the
authoring lane; the ADS half (start_runtime, run_tests, get_test_results) is
native Beckhoff.TwinCAT.Ads behind an `IAdsFactory` seam, so the orchestration
(state transitions, the suites-finished poll, XML parsing) is CI-tested against a
fake without a live runtime. All five are exposed as `TcKit.Cli` verbs and
validated live by [`oracle/smoke-build-test.ps1`](oracle/smoke-build-test.ps1):
a full save-as-library -> build -> deploy -> start_runtime -> run_tests ->
get_test_results cycle against the B1 TcUnit fixture on a real 4026 passed green,
with the runner publishing a real xUnit XML that get_test_results parsed.

- [x] build — CheckAllObjects + Error List mapping CI-tested (fake) + live-validated
- [x] deploy — config-resolve + autostart + activate CI-tested (fake) + live-validated
- [x] start_runtime — WriteControl Run/Config CI-tested (fake) + live-validated (ADS)
- [x] run_tests — Run-mode + finished-poll + XML inline CI-tested (fake) + live-validated (ADS)
- [x] get_test_results — JUnit XML parser CI-tested (fixtures) + live-validated against a real run

## Docs (Beckhoff infosys)

- [ ] find_fb
- [ ] search_docs
- [ ] get_doc_page
- [ ] get_doc_status
- [ ] generate_docs

## Project / config

- [x] open_project — CI-tested + live-validated (XAE)
- [x] create_project — CI-tested (fake) + live-validated (writer smoke)

## Authoring (Automation Interface; port last)

COM foundation in place (`TcKit.Adapters.Automation`), built around an **automation
seam** (`ITcSession` / `ITcSysManager` / `ITcTreeItem`) so the authoring logic is
testable without TwinCAT:

- `ProjectAuthor` — all navigation + verb logic against the seam (COM-free).
- `ComTcSession` / `ComTcSysManager` / `ComTcTreeItem` — the live COM implementation
  (late-bound `dynamic`, `ComRetry`, `GetActiveObject` P/Invoke attach, STA executor).
- A fake seam in the tests encodes the AI behaviour (CreateChild kinds, declaration-only
  GVL/DUT, `^`-path resolution); `StCode` splitter and `TcKind` map are unit-tested too.

The COM foundation is **live-proven** against a real 4026. Two fixes came out of
the first smoke: an `IOleMessageFilter` on the STA thread (resolves
RPC_E_CALL_REJECTED busy rejections) and capturing tree-item path/kind before
navigating (TwinCAT AI invalidates a handle once you navigate away).

The whole lane (create / update / delete / library / scaffolding) is exposed as
`TcKit.Cli` write verbs, and [`oracle/smoke-writer.ps1`](oracle/smoke-writer.ps1)
drives every verb in dependency order against a self-cleaning scratch solution on
a live 4026. A full 28-verb sweep (scaffold two PLCs, author, update/patch,
save-as-library + reference + placeholder + parameters, delete in reverse) passes
green; every verb below is now live-validated through that harness.

- [x] add_pou — CI-tested + live-validated (XAE)
- [x] add_gvl — CI-tested (fake) + live-validated (writer smoke)
- [x] add_dut — CI-tested (fake) + live-validated (writer smoke)
- [x] add_method — CI-tested (fake) + live-validated (writer smoke)
- [x] add_property — CI-tested (fake) + live-validated (writer smoke)
- [x] add_variable — VAR-block editor + CI-tested (fake) + live-validated (writer smoke)
- [x] add_folder — CI-tested (fake) + live-validated (writer smoke)
- [x] add_plc_project — CI-tested (fake) + live-validated (writer smoke)
- [x] add_library_reference — CI-tested (fake) + live-validated (writer smoke)
- [x] add_library_placeholder — CI-tested (fake) + live-validated (writer smoke)
- [x] set_placeholder_parameters — .plcproj XML splice + CI-tested (fake) + live-validated (writer smoke)
- [x] save_plc_as_library — metadata round-trip + cold-start retry + CI-tested (fake) + live-validated (writer smoke)
- [x] update_pou_declaration — CI-tested (fake) + live-validated (writer smoke)
- [x] update_pou_implementation — CI-tested (fake) + live-validated (writer smoke)
- [x] update_method_body — CI-tested (fake) + live-validated (writer smoke)
- [x] update_pou_declaration_patch — CI-tested (fake) + live-validated (writer smoke)
- [x] update_pou_implementation_patch — CI-tested (fake) + live-validated (writer smoke)
- [x] update_method_body_patch — CI-tested (fake) + live-validated (writer smoke)
- [x] delete_pou — CI-tested (incl. task-binding scan) + live-validated (XAE)
- [x] delete_gvl — CI-tested (fake) + live-validated (writer smoke)
- [x] delete_dut — CI-tested (fake) + live-validated (writer smoke)
- [x] delete_method — CI-tested (fake) + live-validated (writer smoke)
- [x] delete_property — CI-tested (fake) + live-validated (writer smoke)
- [x] delete_variable — VAR-block editor + CI-tested (fake) + live-validated (writer smoke)
- [x] delete_folder — CI-tested (fake) + live-validated (writer smoke)
- [x] delete_library_reference — wildcard-version resolution + CI-tested (fake) + live-validated (writer smoke)
- [x] delete_placeholder — CI-tested (fake) + live-validated (writer smoke)
