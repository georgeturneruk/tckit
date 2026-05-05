# Architecture Overview

TcKit uses a **ports & adapters** (hexagonal) architecture.

```
Claude Code
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

## The bridge split

The Windows bridge service (`bridge/Start-Bridge.ps1`) runs natively on the Windows machine with XAE installed. The Docker container calls the bridge for anything requiring COM or XAE.

This means:

- The Docker container has no Windows dependency
- Laptop (read-only mode) works without the bridge running
- The bridge is the only Windows-specific component

## The adapter isolation rule

**Adapters may only import from `tckit.ports` and stdlib. Never from each other.**

This is enforced by linting (`ruff check`). If you need to share logic between adapters, put it in `tckit/utils/` and import that — never adapter-to-adapter.

## Adding a new adapter

See [Contributing](../contributing.md) for the full process.

1. Create the file in the correct `adapters/` subdirectory
2. Import only from `tckit.ports` and stdlib
3. Implement all abstract methods from the port
4. Register it in `tckit/config.py`
5. Add the config name to `config.example.json`
6. Write unit tests in `tests/unit/`
7. Document it in `docs/content/adapters/`
