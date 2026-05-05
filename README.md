<p align="center">
  <img src="docs/content/assets/logo-full.svg" alt="TcKit" width="100">
</p>

# TcKit

A toolkit for AI-assisted development of TwinCAT 3 PLC projects.

TcKit provides an MCP (Model Context Protocol) interface so models can read project structure, write ST code, trigger builds, deploy to targets, run tests, and iterate autonomously on failures.

**[tckit.org](https://tckit.org)** — full documentation

---

## Open loop vs closed loop

Without TcKit, the model generates code but can't verify it — you copy, paste, build, and report errors back manually.

```
 Open loop
 ┌─────────┐   code   ┌──────────┐   copy/paste   ┌───────────┐
 │  Model  │ ───────► │   You    │ ──────────────► │ TwinCAT   │
 └─────────┘          └──────────┘  report errors  └───────────┘
```

With TcKit, the model drives the full cycle autonomously:

```
 Closed loop
 ┌─────────┐  write / build / test  ┌───────────┐
 │  Model  │ ──────────────────────►│ TwinCAT   │
 └─────────┘ ◄────────────────────── └───────────┘
               errors / test results
```

---

## Architecture

```
Model (Claude Code)
        │  MCP protocol
        ▼
 TcKit MCP Server          (Docker)
        │
        ├── ProjectReader  ──► xml_reader
        ├── ProjectWriter  ──► automation_writer  ──┐
        ├── BuildRunner    ──► xae_com_builder    ──┤ Windows bridge
        ├── TestRunner     ──► tcunit_runner      ──┤ (PowerShell → XAE COM)
        ├── DocGenerator   ──► sphinx_generator      │
        └── DocsSearcher   ──► beckhoff_infosys   ──┘
                                        │
                                  TwinCAT XAE
                                        │
                                   PLC / VM
```

Adapters are isolated behind port interfaces (Python ABCs). The server only calls ports — never adapters directly. Adapters may only import from ports and stdlib.

---

## Quick Start

**Requirements:** Docker + Docker Compose. For write/build operations: a Windows PC with TwinCAT 3.1 Build 4026.

```bash
cp docker/.env.example docker/.env
# edit docker/.env with your project path and bridge URL

docker compose -f docker/docker-compose.yml up
```

Windows bridge (write/build/test operations only):

```powershell
.\bridge\Start-Bridge.ps1
```

Add to your Claude Code MCP config:

```json
{
  "mcpServers": {
    "tckit": {
      "url": "http://localhost:8000/sse"
    }
  }
}
```

---

## Status

| Phase | |
|-------|-|
| 1 — Read layer (xml reader, docs searcher, doc generator) | ✅ |
| 2 — Write layer (automation writer, XAE builder) | Planned |
| 3 — Test loop (TcUnit runner, autonomous loop) | Planned |
| 4 — CI, PyPI, open source launch | Planned |

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — MIT licence.
