# TcKit (Claude Code plugin)

Connects Claude Code to TwinCAT 3 PLC projects. Read project structure, write Structured Text, trigger builds, deploy to targets, run TcUnit tests.

## Install

In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
```

Then walk through setup:

```
> Set me up for TcKit.
```

The bundled `tc-config` skill drives a guided init: prompts for your AMS Net IDs, safety stance, project paths, writes `~/.tckit/config.toml`, runs a smoke test.

## Prerequisites

- **Claude Code** (CLI / IDE extension / desktop app).
- **`uv`** — install via `pip install uv` or your platform's installer. The plugin's MCP server runs as `uvx tckit`, which fetches the Python package from PyPI on first use.
- **TwinCAT 3.1 Build 4026** + **TcXaeShell** on a Windows host (only required for write/build/deploy/test; read-only operations work without it).
- **Bridge service**: run `.\bridge\Start-Bridge.ps1` in a separate PowerShell window to expose TcXaeShell COM to TcKit. Requires TcXaeShell to be open.

## What you get

Five skills that load on demand based on what you ask:

| Skill | Loads when |
|-------|------------|
| `tckit:tc-read-project` | Inspecting / navigating a TwinCAT project |
| `tckit:tc-beckhoff-docs` | Researching a Beckhoff library FB or function |
| `tckit:tc-write-st` | Writing or modifying Structured Text |
| `tckit:tc-build-test-loop` | Building, deploying, running TcUnit tests |
| `tckit:tc-config` | Configuring TcKit, editing safety stance, switching modes |

Plus the underlying MCP server exposing 20 tools (project reader, writer, builder, test runner, doc generator, Beckhoff infosys searcher).

## Docker mode (opt-in)

If you prefer to run TcKit in a container instead of via `uvx`:

```bash
git clone https://github.com/georgeturneruk/tckit
cd tckit
# In Claude Code:
/tc-config init
# Pick "docker" mode, fill in prompts.
docker compose -f docker/docker-compose.yml up -d
claude mcp add --transport sse tckit http://localhost:8000/sse
```

You can install this plugin separately just for the skills. The plugin's bundled MCP registration is stdio-only; Docker users register the SSE endpoint manually.

## Documentation

- Project docs: <https://tckit.org>
- Source: <https://github.com/georgeturneruk/tckit>
- Issues: <https://github.com/georgeturneruk/tckit/issues>

## Licence

MIT. See [LICENSE](LICENSE).
