# Architecture Overview

TcKit uses a **ports & adapters** (hexagonal) architecture.

```
AI agent (any MCP client)
    │
    ▼ (MCP protocol)
MCP Server (Python, Docker)
    │
    ▼ (port interfaces — ABCs)
┌──────────────────────────────────────────────────────┐
│  ProjectReader   ProjectWriter   BuildRunner          │
│  TestRunner      DocGenerator    DocsSearcher         │
└──────────────────────────────────────────────────────┘
    │                  │                  │
    ▼                  ▼                  ▼
xml_reader      automation_writer   xae_com_builder
(Docker)        (bridge → COM)      (bridge → COM)
                      │
                      ▼
                TwinCAT XAE
                      │
                      ▼
                PLC / VM (ADS)
```

## Source tree

```
tckit/
├── tckit/
│   ├── server.py           ← MCP server entry point
│   ├── config.py           ← config + .env loader
│   ├── ports/              ← abstract base classes ONLY
│   │   ├── reader.py
│   │   ├── writer.py
│   │   ├── builder.py
│   │   ├── test_runner.py
│   │   ├── doc_generator.py
│   │   └── docs_searcher.py
│   └── adapters/           ← concrete implementations
│       ├── readers/
│       ├── writers/
│       ├── builders/
│       ├── test_runners/
│       ├── doc_generators/
│       └── docs_searchers/
├── bridge/                 ← PowerShell, Windows only
├── tests/
│   └── fixtures/sample_project/   ← real .TcPOU files for testing
├── docs/                   ← MkDocs website
└── docker/
```

## The bridge split

The Windows bridge service (`bridge/Start-Bridge.ps1`) runs natively on the Windows machine with XAE installed. The Docker container calls the bridge for anything requiring COM or XAE.

This means:

- The Docker container has no Windows dependency
- Laptop (read-only mode) works without the bridge running
- The bridge is the only Windows-specific component

## The adapter isolation rule

**Adapters may only import from `tckit.ports`, `tckit.utils`, or stdlib. Never from each other.**

This is enforced by `scripts/check-adapter-isolation.py`, which runs in CI alongside ruff. If you need to share logic between adapters, put it in `tckit/utils/` and import that — never adapter-to-adapter.

## Port methods at a glance

| Port           | Key methods |
|----------------|-------------|
| ProjectReader  | `get_structure()`, `get_pou_interface()`, `get_pou_declaration()`, `get_pou_item()`, `get_gvl()`, `get_dut()` |
| ProjectWriter  | `open_project()`, `create_project()`, `add_pou()`, `add_gvl()`, `add_method()`, `add_property()`, `update_pou_declaration()`, `update_pou_implementation()`, `update_method_body()` (+ `_patch` variants) |
| BuildRunner    | `build()`, `deploy()`, `start_runtime()`, `get_status()` |
| TestRunner     | `run_tests()`, `wait_complete()`, `get_results()`, `get_status()` |
| DocGenerator   | `generate()`, `get_status()` |
| DocsSearcher   | `find_fb()`, `find_library()`, `search()`, `get_page()` |

Each port has its own capability page under [Capabilities](../capabilities/project-reader/overview.md) with the detail on inputs, outputs, and adapter-specific behaviour.

## Adding a new adapter or port

See [CONTRIBUTING.md](https://github.com/georgeturneruk/tckit/blob/main/CONTRIBUTING.md) in the repo root for the contributor guide (adapter recipe, port recipe, code style, local commands, PR workflow).
