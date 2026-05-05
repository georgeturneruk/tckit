# TcKit — Project Context Document
> Hand this to Claude Code at the start of any session to establish full project context.

---

## What Is TcKit?

TcKit is an open source developer toolchain that brings modern AI-assisted development practices to TwinCAT 3 PLC engineering. It connects Claude Code to TwinCAT projects via MCP (Model Context Protocol), enabling Claude to read project structure, write ST code, trigger builds, deploy to targets, run TcUnit tests, and iterate autonomously on failures.

The project is named in the spirit of TcUnit — the de facto TwinCAT unit testing framework — and is intended to become community infrastructure for the TwinCAT ecosystem, similar to what VIPM and lvCICD represent for the LabVIEW community.

---

## Core Design Philosophy

### Ports & Adapters (Hexagonal Architecture)
Every external concern is abstracted behind a port (Python abstract base class). Concrete adapters implement the port. The MCP server only ever talks to ports — never to adapters directly.

**The one hard rule:** adapters may only import from ports and stdlib. Never from each other. This is enforced by linting.

**Why this matters:** Beckhoff may ship new tooling (CLI, LSP server, etc.). When they do, you write a new adapter and change one config value. Nothing else changes.

### Loose Coupling Over Convenience
Never bake in assumptions. COM version string, AMS ID, XAE mode, parser choice, test framework — all config values. The MCP server is the only stable contract.

### Surgical Context Usage
Claude should never fetch more than it immediately needs. Always: structure first, then interface, then specific item. Never fetch a full POU when you only need one method.

---

## Architecture Overview

```
Claude Code
    │
    ▼ (MCP protocol)
MCP Server (Python, runs in Docker)
    │
    ▼ (port interfaces)
┌─────────────────────────────────────────────────────┐
│  Reader      Writer      Builder    TestRunner       │
│  DocGen      DocsSearcher                           │
└─────────────────────────────────────────────────────┘
    │               │              │
    ▼               ▼              ▼
blark_reader   automation_   xae_com_      tcunit_
               writer        builder       runner
(Docker)       (bridge →     (bridge →     (bridge →
               Windows)      Windows)      Windows)
                    │
                    ▼
              TwinCAT XAE (attach or headless)
                    │
                    ▼
              External PLC or VM (via ADS)
```

### The Bridge Service
The Windows bridge service is a small REST API (PowerShell) that runs natively on the Windows machine with XAE installed. The Docker container calls the bridge for anything that requires COM/XAE. The bridge executes PowerShell harness scripts and returns JSON results.

This split means:
- Docker container has no Windows dependency
- Laptop (read-only mode) works without the bridge running
- Bridge is the only Windows-specific component

---

## The Six Ports

### 1. ProjectReader (`ports/reader.py`)
Read-only access to TwinCAT project structure and code.
```python
get_structure(project_path: str) -> ProjectStructure
get_pou_interface(pou_name: str) -> POUInterface      # declarations + method signatures, no bodies
get_pou_item(pou_name: str, item_name: str) -> POUItem  # single method/action/property body only
get_gvl(gvl_name: str) -> GVL
```
**Current adapter:** `blark_reader` — uses blark Python library, runs in Docker, no XAE required.
**Future adapter:** `lsp_reader` — if TwinCAT LSP server matures.

### 2. ProjectWriter (`ports/writer.py`)
Structural writes to TwinCAT project via automation interface.
```python
open_project(solution_path: str) -> Result
create_project(name: str, path: str) -> Result
add_pou(name: str, pou_type: POUType, code: str) -> Result
add_method(pou_name: str, method_name: str, code: str) -> Result
update_pou_item(pou_name: str, item_name: str, code: str) -> Result
```
**Current adapter:** `automation_writer` — calls bridge → PowerShell → TcXaeShell.DTE.17.0 COM.
**Why not manual XML:** GUID assignment, .plcproj cross-references, and tree indexing are all handled automatically by the automation interface. Manual XML risks silent corruption.

### 3. BuildRunner (`ports/builder.py`)
Build, deploy, and runtime control.
```python
build(project_path: str) -> BuildResult         # returns structured errors with file/line/message
deploy(target_ams_id: str) -> Result
start_runtime(target_ams_id: str) -> Result
get_status() -> BuildStatus
```
**Current adapter:** `xae_com_builder` — calls bridge → PowerShell → automation interface.
**Future adapter:** `cli_builder` — if Beckhoff ship CLI tools.

### 4. TestRunner (`ports/test_runner.py`)
TcUnit test execution and result parsing.
```python
run_tests() -> Result
wait_complete(timeout_seconds: int) -> Result
get_results() -> TestResults                    # structured JSON: suite/test/pass/fail/message
get_status() -> TestStatus
```
**Current adapter:** `tcunit_runner` — triggers via bridge, polls for XML output, parses to JSON.

### 5. DocGenerator (`ports/doc_generator.py`)
Sphinx documentation generation from RST-commented ST source.
```python
generate(project_path: str, output_path: str) -> Result
get_status() -> DocStatus
```
**Trigger modes:** `on_demand` (explicit call) | `on_build` (after successful build, default).
**Current adapter:** `sphinx_generator` — wraps plcdoc + Sphinx, runs in Docker.
**Comment style:** reStructuredText, aligned with Beckhoff TE1030. Always write RST comments.

### 6. DocsSearcher (`ports/docs_searcher.py`)
Targeted search and fetch of Beckhoff infosys documentation.
```python
find_fb(fb_name: str) -> FBDoc                 # search + fetch in one call, most common
find_library(library_name: str) -> LibraryDoc
search(query: str, section: str = None) -> SearchResults
get_page(url: str) -> DocPage
```
**Current adapter:** `beckhoff_infosys` — targets infosys.beckhoff.com, parses to structured JSON, local page cache.
**Future adapter:** `beckhoff_github` — Beckhoff GitHub examples and sample code.

---

## Config Schema

All behaviour is driven by `config.json` + `.env`. Never hardcode any of these values.

```json
{
  "reader": "blark",
  "writer": "automation_interface",
  "builder": "xae_com",
  "test_runner": "tcunit",
  "doc_generator": "sphinx",
  "docs_searcher": "beckhoff_infosys",

  "xae_mode": "attach",
  "com_version": "17.0",

  "doc_trigger": "on_build",
  "comment_style": "rst",
  "docs_output_path": "./docs/plc",

  "infosys_cache_path": "./cache/infosys",
  "infosys_lang": "1033"
}
```

Environment-specific values in `.env` (gitignored, `.env.example` committed):
```
PLC_PROJECT_PATH=
BRIDGE_URL=
XAE_MODE=attach
TARGET_AMS_ID=
COM_VERSION=17.0
```

### XAE Modes
- **attach** — connects to already-running XAE instance via `GetActiveObject("TcXaeShell.DTE.17.0")`. Default for dev.
- **headless** — spawns new invisible XAE instance. For CI only.

Mode detection logic: try `GetActiveObject` first, fall back to spawning if nothing running (when `xae_mode=headless`).

---

## Tech Stack

| Component | Technology | Notes |
|-----------|-----------|-------|
| MCP Server | Python 3.11+ | Runs in Docker |
| Reader | blark | pip install blark, parses .TcPOU natively |
| Writer / Builder | PowerShell + COM | Windows only, runs via bridge |
| Test runner | TcUnit | XML output parsed to JSON |
| Doc generator | plcdoc + Sphinx | Runs in Docker |
| Docs searcher | httpx + BeautifulSoup | Runs in Docker |
| Bridge | PowerShell REST | Runs natively on Windows |
| Website | MkDocs + Material | Deployed to GitHub Pages |
| Container | Docker + Compose | Dev and CI modes |
| Target | TwinCAT 3.1 Build 4026 | TcXaeShell.DTE.17.0 |

---

## Repo Structure

```
tckit/
├── .github/
│   ├── workflows/
│   │   ├── ci.yml
│   │   ├── docs.yml
│   │   └── release.yml
│   └── ISSUE_TEMPLATE/
│
├── docker/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   ├── docker-compose.ci.yml
│   └── .env.example
│
├── tckit/                          ← Python package
│   ├── server.py                   ← MCP entry point
│   ├── config.py
│   ├── ports/
│   │   ├── reader.py
│   │   ├── writer.py
│   │   ├── builder.py
│   │   ├── test_runner.py
│   │   ├── doc_generator.py
│   │   └── docs_searcher.py
│   └── adapters/
│       ├── readers/blark_reader.py
│       ├── writers/automation_writer.py
│       ├── builders/xae_com_builder.py
│       ├── test_runners/tcunit_runner.py
│       ├── doc_generators/sphinx_generator.py
│       └── docs_searchers/beckhoff_infosys.py
│
├── bridge/                         ← Windows bridge service
│   ├── Start-Bridge.ps1
│   └── harness/
│       ├── Invoke-TcBuild.ps1
│       ├── Invoke-TcDeploy.ps1
│       ├── Invoke-TcRuntime.ps1
│       └── Get-TcUnitResults.ps1
│
├── tests/
│   ├── unit/
│   ├── integration/
│   └── fixtures/
│       └── sample_project/         ← committed .TcPOU files for CI testing
│
├── docs/                           ← MkDocs website source
│   ├── mkdocs.yml
│   └── content/
│       ├── index.md
│       ├── getting-started/
│       ├── architecture/
│       ├── adapters/
│       └── contributing.md
│
├── examples/
│   └── sample-plc-project/
│
├── scripts/
│   ├── validate-blark.py
│   └── spike-com.ps1
│
├── CLAUDE.md                       ← Claude Code instructions (see separate file)
├── config.example.json
├── pyproject.toml
├── README.md
├── CONTRIBUTING.md
└── LICENSE
```

---

## Development Phases

### Phase 1 — Read Layer + Discovery (Start Here)
**Goal:** Claude can read any TwinCAT project from either machine. Architecture established.

Tasks:
1. Set up repo structure and Python package skeleton
2. Define all six port interfaces as abstract base classes
3. Set up adapter registry — config.json wires name to class
4. Enforce adapter isolation via linting (no cross-adapter imports)
5. Set up Docker environment — MCP server + blark + sphinx + httpx/BS4
6. Implement `blark_reader` adapter — validate against real .TcPOU files first
7. Implement `sphinx_generator` adapter — generate() and get_status() only
8. Implement `beckhoff_infosys` adapter — find_fb(), search(), get_page(), page cache
9. Wire all three as MCP tools with own JSON schema (not blark internals)
10. Write Claude Code system prompt (see CLAUDE.md)
11. Validate end-to-end from both machines

**Deliverable:** Claude reads real project, consults infosys docs, generates Sphinx docs. No XAE needed.

### Phase 2 — Write Layer
**Goal:** Claude can safely write ST code back and build.

Tasks:
1. Manual spike — validate TcXaeShell.DTE.17.0 on 4026 machine
2. Confirm ProduceXml/ConsumeXml output shape on real project
3. Confirm CreateChild GUID behaviour for methods
4. Build Windows bridge service — Start-Bridge.ps1 REST listener
5. Implement harness scripts — Build, Deploy, Runtime
6. Implement `automation_writer` adapter via bridge
7. Implement `xae_com_builder` adapter via bridge
8. Mode detection — GetActiveObject first, headless fallback
9. Wire on_build doc trigger — generate() after successful build
10. Validate full read/write/build loop

**Deliverable:** Claude edits code, builds, gets structured errors, fixes and rebuilds.

### Phase 3 — Test Loop
**Goal:** Autonomous write → build → deploy → test → fix loop.

Tasks:
1. Implement `tcunit_runner` adapter — run, wait, parse XML to JSON
2. ADS route validation before deploy
3. Full loop validation on external target (VM or real PLC)
4. System prompt tuning for autonomous iteration
5. Loop guard — max iterations, escalate to human on repeated failure
6. Add test scaffolding — generate TcUnit FB skeleton from existing FB interface
7. Validate Claude writes code, tests pass autonomously

**Deliverable:** Full autonomous loop. Same pattern as pytest/jest, for TwinCAT.

### Phase 4 — Intelligence + CI
**Goal:** Polish, CI pipeline, LSP exploration, open source launch.

Tasks:
1. Profile real Claude Code sessions — measure and optimise context usage
2. Evaluate truST LSP server against .TcPOU files
3. If viable — implement lsp_reader adapter, config flag to switch
4. Containerise for CI — docker-compose.ci.yml
5. GitHub Actions — ci.yml, docs.yml, release.yml
6. PyPI publish — pip install tckit
7. Docker Hub publish — docker pull tckit/server
8. MkDocs website — GitHub Pages deploy
9. Submit blark grammar fixes upstream
10. Open source launch — README, CONTRIBUTING, good first issues

---

## Key Technical Details

### TwinCAT 4026 Specifics
- COM entry point: `TcXaeShell.DTE.17.0` (not 15.0 — 4026 uses forked VS shell)
- 4026 uses updated project format — XML schema differences from 4024
- Validate COM version string manually before building any scripts

### blark
- pip install blark
- Parses .TcPOU, .TcGVL, .tsproj natively
- Built on Lark Earley grammar
- Actively maintained but slow-moving — validate against real project files early
- Grammar gaps expected — workaround or contribute fixes upstream
- GitHub: https://github.com/klauer/blark

### Automation Interface Key Methods
- `ProduceXml()` — export node content as XML (read)
- `ConsumeXml()` — write XML content back to node
- `CreateChild(name, kind)` — add new POU/method/property (GUID auto-assigned)
- `ITcSmTreeItem` — main tree navigation interface
- `ActivateConfiguration()` — deploy to target
- `StartRestartTwinCAT()` — start runtime

### TcUnit
- Results written as XML — validate output path on 4026 before building parser
- XML format: suite/test/pass/fail/message hierarchy
- Completion detection: poll for XML file creation + runtime state

### Beckhoff Infosys Structure
```
https://infosys.beckhoff.com/content/1033/
  tc3_plc_intro/          ← PLC programming guide
  tcplclib_tc2_*/         ← TC2 standard libraries
  tcplclib_tc3_*/         ← TC3 libraries
  tf6xxx_*/               ← TwinCAT functions
  tc3_ads_intro/          ← ADS documentation
```
- Search URL pattern: `https://infosys.beckhoff.com/index_en.htm#search=FB_Name`
- Parse inputs/outputs/description from HTML into structured JSON
- Cache pages locally — protects against HTML structure changes

### RST Comment Convention
Aligned with Beckhoff TE1030. Claude always writes RST-format comments:
```pascal
{attribute 'TcRpcEnable'}
// :Description: Reads an SDO value from an EtherCAT slave
// :param sNetId: AMS Net ID of the EtherCAT master
// :param nSlaveAddr: EtherCAT slave address
// :returns: TRUE if read successful
METHOD ReadSDO : BOOL
```

---

## Known Risks & Spikes Needed

| Risk | Mitigation |
|------|-----------|
| blark grammar gaps on real project | Validate in Phase 1 week 1 before building anything else |
| COM version string wrong | Manual spike before Phase 2 |
| Headless XAE COM stability | Budget 2-3x expected time, retry with backoff |
| ADS route to external target | Validate route before attempting deploy |
| TcUnit XML output path/format on 4026 | Check before building parser |
| Infosys HTML structure changes | Local cache mitigates, only adapter needs updating |
| Beckhoff licensing for headless XAE | Check EULA before CI mode implementation |
| Rename operations | No automation interface support — blark find+replace, flag as review-required |

---

## Multi-Machine Setup

### PC (Windows + XAE)
- Runs bridge service natively
- XAE installed, attach mode default
- `.env.pc` with local paths and bridge on localhost

### Laptop (any OS)
- Docker only
- No XAE, no bridge
- `.env.laptop` with `BRIDGE_URL` pointing to PC IP
- Read-only mode when PC bridge not running (blark + infosys still work)
- Full mode when PC bridge running over network

### Shared
- Git repo — everything except .env files
- Docker image — consistent Python environment
- config.json — committed, no secrets

---

## Naming & Conventions

- **Project name:** TcKit
- **Python package:** `tckit`
- **Docker image:** `tckit/server`
- **PyPI:** `tckit`
- **GitHub:** `github.com/[you]/tckit`
- **Website:** MkDocs + Material, GitHub Pages

### Code Conventions
- Python 3.11+, type hints everywhere
- Ports are abstract base classes with `@abstractmethod`
- Adapters named `{tool}_{port_type}` e.g. `blark_reader`, `xae_com_builder`
- All MCP tool responses are JSON with consistent schema
- PowerShell scripts use approved verbs: `Invoke-`, `Get-`, `Start-`, `Stop-`

---

## Reference Projects

| Project | Relevance |
|---------|-----------|
| blark | ST parser — core read dependency |
| TcUnit | Test framework — core test dependency |
| plcdoc | Sphinx ST doc generation |
| mcp-server-git | Anthropic MCP reference implementation |
| continue.dev | Most architecturally similar AI dev tool |
| lvCICD | LabVIEW CI analogue — study PowerShell wrapper pattern |
| truST platform | Potential future LSP reader adapter |
| pytmc | Alternative TcPOU parser (SLAC/EPICS focused) |
