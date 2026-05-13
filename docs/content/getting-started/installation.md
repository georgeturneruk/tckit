# Installation

There are three install paths. **The plugin is recommended** for almost everyone. `pip` is for users who want to manage the MCP server themselves. Docker is opt-in for isolation, multi-client setups, or a remote-server install.

## Requirements

- [Claude Code](https://docs.claude.com/en/docs/claude-code)
- For write, build, deploy, and test: a **Windows** host with **TwinCAT 3.1 Build 4026** and **TcXaeShell**. Reads work without it.

The plugin install path uses [`uv`](https://docs.astral.sh/uv/) under the hood. If you don't have it, `pip install uv` will do.

## Plugin (recommended)

In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled `tc-config` skill walks you through the prompts and writes your config to `~/.tckit/config.toml`. The MCP server runs as `uvx tckit`, fetching the package from PyPI on first use; updates happen automatically.

That's it. Skip to [Bridge Setup](bridge-setup.md) if you need write/build/deploy/test.

## pip (without the plugin)

If you want to manage the MCP server yourself rather than going through the plugin:

```bash
pip install tckit
```

This installs the `tckit` console script. Run it directly:

```bash
tckit                       # MCP server on stdio (default)
tckit --transport sse       # MCP server on http://localhost:8000/sse
tckit config show           # print resolved config
tckit doctor                # health checks (config + bridge)
```

Register it with your MCP client. For Claude Code on stdio:

```bash
claude mcp add tckit -- tckit
```

You will need to write `~/.tckit/config.toml` by hand (or copy `config.example.json` and adapt). The plugin's `tc-config` skill is the easier path.

## Docker (opt-in)

For isolation, multi-client setups, or a remote-server install. See [Docker Setup](docker-setup.md) for the full walkthrough.

```bash
git clone https://github.com/georgeturneruk/tckit
cd tckit
docker compose -f docker/docker-compose.yml up -d
claude mcp add --transport sse tckit http://localhost:8000/sse
```

You can install the plugin separately just for the bundled skills.

## Bridge (Windows, for write/build/deploy/test)

All three install paths use the same bridge service for write operations. In a separate PowerShell window with TcXaeShell open:

```powershell
.\bridge\Start-Bridge.ps1
```

See [Bridge Setup](bridge-setup.md) for firewall and XAE-mode details.

## Verify

```bash
tckit doctor        # config + bridge health check
tckit --help        # available subcommands and flags
```

To verify the MCP server runs end-to-end, ask Claude Code (or any MCP client) to call a TcKit tool, for example `get_structure` against a TwinCAT project path.
