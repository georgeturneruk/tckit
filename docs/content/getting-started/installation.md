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

## Verify

```bash
# Check the server starts cleanly
python -m tckit.server --help

# Run unit tests
pytest tests/unit/ -v
```
