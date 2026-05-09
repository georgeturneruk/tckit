# Installation

## Requirements

- **Docker** + **Docker Compose** (any OS)
- **Python 3.11+** (optional — only if running outside Docker)
- **Windows + TwinCAT 3.1 Build 4026** (only needed for write/build/deploy)

## Docker (recommended)

```bash
git clone https://github.com/turb5/tckit
cd tckit
cp docker/.env.example docker/.env
```

Edit `docker/.env` with your machine-specific values (see [Docker Setup](docker-setup.md)).

```bash
docker compose -f docker/docker-compose.yml up
```

The MCP server is now listening at `http://localhost:8000`.

## pip (development)

```bash
pip install -e ".[dev]"
```

This installs the `tckit` console script. By default it runs the MCP server over stdio:

```bash
tckit --help          # show available transports and flags
tckit                 # start MCP server on stdio (default)
tckit --transport sse # start MCP server on http://localhost:8000/sse (Docker mode)
```

## Verify

```bash
# Run unit tests
pytest tests/unit/ -v

# Smoke-test the CLI
tckit --help
```

To verify the MCP server runs end-to-end, point an MCP client at the running server. For stdio: register with `claude mcp add tckit -- tckit`. For SSE: point at `http://localhost:8000/sse` after `docker compose up`.
