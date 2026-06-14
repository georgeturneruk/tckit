# Docker (CI / dev only)

Docker mode is a contributor and CI-runner convenience, not a recommended user install. For day-to-day use, install via the plugin or `pip` (see [Installation](installation.md)).

## Why it's not a user path

The MCP server runs Linux inside the container; Claude Code on the Windows host passes project paths like `C:/MyProject`. The container only sees its mounted volumes (`/projects` by default), so any path that wasn't pre-mounted at exactly that location fails. The bridge (the COM-talking PowerShell process) is Windows-only regardless, so the only thing Docker mode brings is read-only project parsing, which the native install already does without any path-translation gymnastics.

Tracking: [issue #43](https://github.com/georgeturneruk/tckit/issues/43).

## Use it for

- CI: running the test suite or doc generator against a project pre-copied into the image.
- Contributor sandboxes where you want the Python side isolated from the host.

## Configuration

```bash
cp docker/.env.example docker/.env
```

Edit `docker/.env`:

```bash
PROJECT_PATH=/projects/MyProject.sln
BRIDGE_URL=http://host.docker.internal:8765
TARGET_AMS_ID=192.168.1.100.1.1
XAE_MODE=attach
```

`PROJECT_PATH` is read by the docs service to know which project to document; bridge-backed tools (reads, edits, builds) operate on the solution open in the attached XAE and don't need it. Mount the host directory containing your projects via `PLC_PROJECTS_HOST_PATH` (defaults to `./projects`). Inside the container it appears as `/projects`. Any path the agent passes must resolve inside that mount.

## Start

```bash
docker compose -f docker/docker-compose.yml up
```

Register the SSE endpoint with Claude Code:

```bash
claude mcp add --transport sse tckit http://localhost:8000/sse
```

## Read-only mode (no bridge)

Without the Windows bridge running, only the reader, doc generator, and infosys searcher are usable. Write, build, deploy, and test return an error.

## Running tests in Docker

```bash
docker compose -f docker/docker-compose.yml run tckit pytest tests/unit/ -v
```
