# Docker Setup

## Configuration

Copy the example environment file:

```bash
cp docker/.env.example docker/.env
```

Edit `docker/.env`:

```bash
# Absolute path to your TwinCAT solution or project file
PLC_PROJECT_PATH=/path/to/MyProject.sln

# URL of the Windows bridge service
# If bridge is on a separate machine, use its IP
BRIDGE_URL=http://host.docker.internal:8765

# XAE connection mode
XAE_MODE=attach   # attach to running XAE, or 'headless' for CI

# AMS Net ID of the target PLC (needed for deploy/test)
TARGET_AMS_ID=192.168.1.100.1.1
```

## Starting the server

```bash
docker compose -f docker/docker-compose.yml up
```

## Read-only mode (no bridge)

If the Windows bridge is not running, TcKit works in read-only mode:

- `xml_reader` — reads `.TcPOU`, `.TcGVL`, and `.TcDUT` files
- `html_generator` / `markdown_generator` — generates docs from ST comments
- `beckhoff_infosys` — searches Beckhoff documentation

Write, build, deploy, and test operations will return an error until the bridge is reachable.

## Running tests in Docker

```bash
docker compose -f docker/docker-compose.yml run tckit pytest tests/unit/ -v
```
