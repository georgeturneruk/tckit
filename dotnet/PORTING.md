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

The eight ADS-native tools are ported behind two seams so the orchestration and decoding are
CI-tested against fakes without a live runtime: symbol I/O (`ISymbolSessionFactory` →
`AdsSymbolIo`, native `AdsClient.ReadValue/WriteValue/InvokeRpcMethod`) and hardware diagnostics
(`IHardwareSource` → `TwinSharpHardwareInspector`, native TwinSharp `TcSystem`/`IPC`/`NC`). The
pure decoding (EtherCAT slave state names + link health, master device-state flags, axis state
name, UPS power/battery health) lives in `HardwareDecode` and is unit-tested directly. All eight
are exposed as `TcKit.Cli` verbs and MCP tools; write_symbols / invoke_rpc gate on confirmed=true
at the tool boundary. **Live validation against a real 4026 is still pending** (no live target in
the porting session); the IPC reads in particular (CPU frequency units, router-memory mapping)
want a live cross-check.

- [x] read_symbols — CI-tested (fake); live-validation pending
- [x] write_symbols — best-effort per-path errors + confirmed gate; CI-tested (fake); live pending
- [x] invoke_rpc — confirmed gate; CI-tested (fake); live pending
- [x] get_ethercat_status — master flags + slave decode CI-tested; live pending
- [x] list_ethercat_masters — CI-tested (fake); live pending
- [x] get_ipc_hardware — module mapping CI-tested (fake); live pending (units cross-check)
- [x] get_axis_state — axis lookup + state-name decode CI-tested; live pending
- [x] list_axes — CI-tested (fake); live pending

scan_hardware and scaffold_hardware_code are **not** ADS — they navigate the TIID I/O tree over
the COM Automation Interface (and scaffold_hardware_code also reuses add_gvl), so they ride the
automation seam (`IHardwareScanner` → `AutomationHardwareScanner`, delegating to
`ProjectAuthor.ScanHardware` / `ScaffoldHardwareCode`). The terminal-name parsing, the device
catalogue, the GVL codegen, and the TIID topology build are all CI-tested against the in-memory
fake; scaffold stays atomic within one COM session. Exposed as `TcKit.Cli` verbs and MCP tools,
and **live-validated on a real 4026** (scan enumerated a master + EK1100 + EL terminals; scaffold
wrote the expected GVL).

- [x] scan_hardware — TIID walk + name parsing CI-tested (fake) + live-validated
- [x] scaffold_hardware_code — catalogue + codegen + add_gvl reuse CI-tested (fake) + live-validated

### I/O authoring (net-new; no Python equivalent)

Hardware-configuration verbs added on top of the Python surface, behind `IHardwareConfigurer` →
`AutomationHardwareConfigurer` (delegating to `ProjectAuthor.AddEtherCatMaster` / `AddEtherCatBox` /
`DeleteIoDevice`). They drive `ITcSmTreeItem.CreateChild` on the I/O tree (EtherCAT master = subtype
111, box/terminal = subtype 9099 with the order number as vInfo). CI-tested against the fake seam
and **live-validated on a real 4026** (added a master + EK1100 + EL1008 nested under the coupler,
scanned them back, then cascade-deleted the device).

- [x] add_ethercat_master — CI-tested (fake) + live-validated
- [x] add_ethercat_box — coupler/terminal by order number + nesting CI-tested (fake) + live-validated
- [x] delete_io_device — name lookup + cascade CI-tested (fake) + live-validated

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

The infosys searcher (`TcKit.Adapters.Docs`, `IDocsSearcher` -> `BeckhoffInfosysSearcher`) navigates
infosys's own `menu.php` tree to build per-section title -> URL indexes, caching both the indexes and
fetched pages to disk; no external search. HTTP sits behind an `IInfosysClient` seam so the navigator,
HTML parser (AngleSharp, replacing BeautifulSoup), index search, URL normalisation, and disk caching
are all CI-tested against canned HTML without a live infosys. Exposed as `TcKit.Cli` verbs (`find-fb`,
`search-docs`, `get-doc-page`) and MCP tools, and live-smoked against real infosys (single-page fetch
+ a full `find_fb` crawl). The disk-cache JSON keys match the Python adapter's so caches interchange.
`find_library` is on the port for completeness but, as in Python, is not exposed as a tool.

`KnownSections` was broadened beyond the Python set (which missed major libraries): it now covers the
motion libraries (`tc2_mc2`, `tc2_mc2_drive`), IO-Link (`tc3_iolink`), and the wider PLC-library set
(fieldbus, system/utility, building automation), sourced from the infosys PLC-libraries menu tree.
The first find_fb into a large, uncached library section is slow (the BFS crawl walks the whole
section with a polite delay); the result is cached, so subsequent lookups are local.

- [x] find_fb — navigator + parser CI-tested (fake) + live-smoked (FB_MemSet, MC_Power)
- [x] search_docs — cached-index search (now also over the hardware sections) CI-tested (fake) + live-smoked
- [x] get_doc_page — fetch + parse + cache CI-tested (fake) + live-smoked

### Hardware docs (net-new; no Python equivalent)

`find_hardware(orderNumber)` looks up a Beckhoff hardware product by order number and returns its
terminal page description plus the parsed "Technical data" table. The order number is matched to one
of the curated hardware doc sections (`InfosysNavigator.HardwareSections`, sourced from the infosys
menu tree) by an x-wildcard matcher (`SectionCoversOrder`), then resolved by targeted navigation:
section overview -> terminal page -> menu-expand -> "&lt;order&gt; - Technical data" page. The matcher,
the technical-data table parser, the anchor-by-text resolver, and the full navigation are CI-tested
against the fake seam; live-smoked against real infosys (EL3004, EPP1008). Pairs with scan_hardware
and the EtherCAT authoring verbs, which deal in the same order numbers. Coverage: EtherCAT
terminals/boxes/measurement modules (EL/EK/EP/ELM/EM) and EtherCAT P boxes (EPP).

Not covered: the AX servo drives (AX5000/AX8000). Their documentation is not in the
`/content/1033/<section>/` tree that the menu.php navigator walks, so find_hardware cannot reach it
without a different fetch strategy. The motion/drives *programming* side is covered via `tc2_mc2` on
the find_fb path.

- [x] find_hardware — matcher + nav + technical-data parser CI-tested (fake) + live-smoked

The doc *generator* lane (`generate_docs`, `get_doc_status`) is a separate port (it parses local ST
comments, not infosys) and is not part of the searcher port. It lives in `TcKit.Adapters.DocGen`
(`IDocGenerator` -> `DocGenerator`), self-contained: it parses the local `.TcPOU`/`.TcGVL`/`.TcDUT`
tree into a doc model (comment auto-detection of RST line, RST block, and Beckhoff XML `<docu>`
styles; the same variable/struct/enum and meta parsing as the Python `_doc_model`) and hand-renders
either a self-contained HTML site or GitHub Flavoured Markdown (no Jinja, no Sphinx). The two Python
adapters collapse into one generator selected by a `format` argument (`html` default | `markdown`).
Adapter isolation forbids reusing the Reader's TcFileParser, so the lane carries its own slim XML
parse. The comment extractor, doc-model parsing, and full multi-PLC output layout (ADR-0005:
per-`.plcproj` sub-tree, per-PLC hierarchy + lunr `search-index.json`, used-by scoped within a PLC)
are CI-tested (xUnit), and cross-checked against the Python generators on both fixtures: the rendered
HTML is structurally identical (only insignificant inter-tag whitespace differs) and the search
index is byte-identical after JSON canonicalisation. Exposed as a `TcKit.Cli` verb (`generate-docs`)
and MCP tools.

- [x] get_doc_status — instance status (idle/generating/complete/error); CI-tested
- [x] generate_docs — HTML + Markdown renderers CI-tested + oracle-checked vs Python on both fixtures

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
