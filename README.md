# TcKit

AI-assisted development toolchain for TwinCAT 3 PLC engineering.

TcKit connects Claude Code to TwinCAT 3 projects via MCP (Model Context Protocol). Claude can read project structure, write ST code, trigger builds, deploy to targets, run TcUnit tests, and iterate autonomously on failures.

---

## Architecture

```
Claude Code
    │
    ▼ (MCP protocol)
MCP Server (Python, Docker)
    │
    ▼ (port interfaces)
┌──────────────────────────────────────────────┐
│  Reader   Writer   Builder   TestRunner       │
│  DocGen   DocsSearcher                        │
└──────────────────────────────────────────────┘
    │              │              │
    ▼              ▼              ▼
blark_reader  automation_   xae_com_
              writer        builder
(Docker)      (bridge →     (bridge →
               Windows)      Windows)
                   │
                   ▼
             TwinCAT XAE
                   │
                   ▼
             PLC / VM (ADS)
```

Every external concern is abstracted behind a port (Python ABC). Adapters implement ports. The MCP server only calls ports — never adapters directly.

**The one hard rule:** adapters may only import from ports and stdlib. Never from each other.

---

## Quick Start

### Requirements

- Docker + Docker Compose
- (For writes/builds) Windows PC with TwinCAT 3.1 Build 4026 installed

### Run the MCP server

```bash
cp docker/.env.example docker/.env
# edit docker/.env with your paths and bridge URL

docker compose -f docker/docker-compose.yml up
```

### Windows bridge (for write/build operations)

On the Windows machine with XAE installed:

```powershell
.\bridge\Start-Bridge.ps1
```

### Connect Claude Code

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

## Development

```bash
# Run tests
docker compose -f docker/docker-compose.yml run tckit pytest tests/

# Lint
docker compose -f docker/docker-compose.yml run tckit ruff check tckit/

# Docs (local preview)
pip install mkdocs-material
mkdocs serve
```

---

## Project Status

Phase 1 (Read Layer) — in progress.

| Phase | Status |
|-------|--------|
| 1 — Read layer (blark reader, docs searcher, doc generator) | In progress |
| 2 — Write layer (automation writer, XAE builder) | Planned |
| 3 — Test loop (TcUnit runner, autonomous loop) | Planned |
| 4 — CI, PyPI, open source launch | Planned |

---

## Docs

Full documentation at [tckit.dev](https://tckit.dev) (coming soon).

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

MIT
