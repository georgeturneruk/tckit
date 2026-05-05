# TcKit

**AI-assisted development toolchain for TwinCAT 3 PLC engineering.**

TcKit connects Claude Code to TwinCAT 3 projects via MCP (Model Context Protocol). Claude can read project structure, write ST code, trigger builds, deploy to targets, run TcUnit tests, and iterate autonomously on failures.

---

## What it does

| Capability | Status |
|-----------|--------|
| Read POUs, interfaces, properties, GVLs, DUTs | ✅ Phase 1 |
| Search Beckhoff infosys documentation | ✅ Phase 1 |
| Generate Sphinx docs from RST-commented ST code | ✅ Phase 1 |
| Write ST code, add POUs and methods | Phase 2 |
| Build projects, get structured errors | Phase 2 |
| Run TcUnit tests and iterate autonomously | Phase 3 |

---

## Quick start

```bash
# Clone the repo
git clone https://github.com/turb5/tckit
cd tckit

# Configure
cp docker/.env.example docker/.env
# Edit docker/.env with your paths

# Start the MCP server
docker compose -f docker/docker-compose.yml up
```

Then add TcKit to your Claude Code MCP config:

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

## Design philosophy

TcKit uses a **ports & adapters** (hexagonal) architecture. Every external concern is abstracted behind a port (Python ABC). Adapters implement ports. The MCP server only calls ports.

**The one hard rule:** adapters may only import from ports and stdlib. Never from each other.

This means when Beckhoff ships new tooling, you write a new adapter and change one config value. Nothing else changes.

See [Architecture → Overview](architecture/overview.md) for the full picture.
