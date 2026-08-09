# Tool porting checklist (ADR-0015)

Tracks the per-tool port from the Python TcKit to the C# server. Order: readers
and ADS/hardware first (most dependency coverage, lowest risk), the Automation
Interface authoring lane last (hardest, most banked behaviour). Each tool is
done only once it passes the parity oracle (see `oracle/`) against a fixture.

This is a local tracking surface; promote to a GitHub issue/milestone when the
work goes public.

## Readers (port first)

- [x] get_structure — `TcKit.Adapters.Xml` (named `TcKit.Adapters.Reader` until the XML writer backend moved in); xUnit + oracle green (sample, multi-PLC, T3)
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
of the hardware doc sections (`InfosysNavigator.HardwareSections`) by an x-wildcard matcher
(`SectionCoversOrder`, which also drops a `-00xx` variant suffix from the slug so order-specific slugs
like `epp7342-0002` resolve from the bare order), then resolved by navigation. Two resolution paths:
the **fast path** scans the section overview for an inline anchor naming the order (most EL/EP/EPP);
the **fallback** is a bounded, product-branch-first walk of the menu.php tree (`FindOrderNodeAsync`)
for sections that nest the product several levels down (couplers, boxes: overview -> product overview
-> connection type -> "&lt;order&gt;" -> "Technical data"). The walk only accepts an order-named node
that owns a "Technical data" child, so an order-named aspect page (e.g. "Diagnostic LEDs &gt; EK1100")
is not mistaken for the product. The most specific matching section (fewest wildcards) is tried first,
so a catch-all (`erxxxx`, `eppxxxx-x7xx`) never shadows an exact section. The anchor resolver
(`FindLinkByOrder`) is wildcard-aware and prefers an exact match over a group heading, so "EP3174-0002"
wins over "EP31xx-xxxx" while coupler overviews that list ranges ("EK110x-00xx, EK15xx") still resolve.
Matcher, parser, anchor resolver, and the menu-tree walk are CI-tested against the fake seam;
live-smoked against real infosys, one order per family, all returning a technical-data table: EL3004,
ELM3504, EK1100, CU1128, EP3174, EPP6228, EPP3504, EJ1100, EPI1008.

`HardwareSections` is enumerated from the infosys fieldbus menu tree by
[`oracle/regen-hardware-sections.ps1`](oracle/regen-hardware-sections.ps1) (one seed page per family);
re-run it to refresh when Beckhoff adds products. Coverage now spans EtherCAT Terminals (EL/EM/ELM/ED),
couplers (EK/EKM), EtherCAT Box (EP) + rugged (ER) + 24 V (EQ), EtherCAT P Box (EPP), plug-in modules
(EJ), IO-Link boxes (EPI/ERI) and infrastructure/switches (CU).

Known limitation: the pure catch-all sections `erxxxx` (rugged) and `eqxxxx` (stainless) have no
per-order documentation page in infosys at all (they are a single generic doc; ER/EQ boxes are the EP
equivalents in a different housing), so find_hardware returns the family page description and URL with
an empty `technical_data`. EP data is not substituted, since the housing/protection specs differ. Not
covered at all: the AX servo drives (AX5000/AX8000) — their docs are not in the
`/content/1033/<section>/` tree the menu.php navigator walks. The motion/drives *programming* side is
covered via `tc2_mc2` on the find_fb path.

- [x] find_hardware — matcher + anchor resolver + menu-tree walk + table parser CI-tested (fake) + live-smoked (per-family)

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

### Safety stance (permission gate)

The Python `init` / `config` / `doctor` CLI subcommands and the layered TOML+JSON config loader are
**deliberately not ported** (most of it was bridge-era plumbing: `BRIDGE_URL`, `XAE_MODE`, adapter
overrides). Runtime defaults the C# server needs (e.g. `COM_VERSION`) are read straight from the
environment. The one piece with real behaviour behind it — the safety stance — is ported as a small,
hot-reloaded permission gate instead:

- `IPermissionGate` (`TcKit.Core.Ports`) → `FilePermissionGate` (`TcKit.Core.Security`), reading
  `~/.tckit/permissions.json` (or `$TCKIT_HOME/permissions.json`), hot-reloaded on mtime so an
  in-session edit (or a `SetPermissions` call) takes effect on the next tool call with no reconnect.
- Two axes. **mode** = `read` (inspect only) < `write` (author on disk) < `execute` (act on a live
  target); every mutating tool declares its level and the gate short-circuits with an error when the
  mode is below it. **NetId allow/block** gates execute-class calls by target: `blocked_net_ids` is a
  hard, unbypassable "never touch production" guard (block always wins); a non-empty `allowed_net_ids`
  is an allowlist. Execute-class = exactly the NetId-gated set (Deploy, StartRuntime, RunTests,
  WriteSymbols, InvokeRpc).
- `GetPermissions` / `SetPermissions` MCP tools make the soft facets (mode, allowlist) easy to swap
  mid-session; `SetPermissions` can *append* a blocked NetId but never remove one (the hard guard is
  lifted only by editing the file). Both tools are callable in any mode.
- Failure stances: missing file = permissive (opt-in); unparseable file = keep last good (no brick,
  no silent widening); valid file with a typo'd `mode` = fall to `read` (safe side).
- Fully CI-tested (`FilePermissionGateTests`, 18 cases: mode tiering, allow/block semantics, block-wins,
  hot-reload, append-only blocklist, failure stances). The dev/oracle CLI drives adapters directly and
  stays ungated by design — the gate guards the agent-facing MCP surface.

- [x] permission gate + GetPermissions / SetPermissions — CI-tested (`FilePermissionGateTests`)
- [x] docs page for the safety stance — `docs/content/getting-started/permissions.md`; tc-config skill updated to match

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

## XML writer backend (ADR-0017; second `IProjectWriter`)

`TcKit.Adapters.Xml` gained `XmlProjectWriter`: the same verb surface as the
automation lane, implemented as deterministic edits of the on-disk TwinCAT XML
(no COM, no XAE, runs on Linux). Backend selected per session via
`TCKIT_WRITER` / `--writer` (automation default on Windows, xml elsewhere).
Validation ladder per verb: CI-tested (temp-dir xunit, byte-exact emission with
pinned GUIDs) -> Linux CI integration (CLI drives the verb against a fixture
copy on ubuntu) -> **parity-validated** (`oracle/parity-writer.ps1` diffs
canonicalised trees against the automation backend on a live 4026).

- [x] all object verbs (add/update/patch/delete POU, GVL, DUT, folder, method,
  property, variable) — CI-tested; representative sequence in the Linux CI
  integration step; **parity-validated** (live 4026)
- [x] library verbs (reference / placeholder / parameters) — CI-tested +
  **parity-validated**; live-locked shapes: `*` version recorded as `newest`
  in LibraryReference Includes, LibraryReferences in their own ItemGroup
- [x] create_project / add_plc_project / save_plc_as_library — explicit
  unsupported `Result.Fail` on this backend (XAE templates / compiler)
- [x] parity sweep green on a live 4026 — 28 verbs, 0 diverged (three
  iterations: property shapes `PROPERTY PUBLIC` / accessor `Name` attrs /
  `PUBLIC`+VAR accessor declaration, then the library-lane shapes above).
  XAE opens and compiles an xml-authored project clean (CheckAllObjects,
  LineIds absent). Side finding, since fixed: ParameterGuard state was per
  process, so CLI-per-verb automation usage lost spliced parameter blocks
  on the next verb's save; the guard now seeds its registry from the
  on-disk Parameters blocks before every verb (live-verified: the sweep
  splices before further automation verbs and stays green).
