# Installation

Two recommended paths: the **Claude Code plugin** (easiest, drives a guided setup) and **pip** (manage the MCP server yourself). Docker exists for CI and containerised dev only; it's not a user install path.

## Requirements

- [Claude Code](https://docs.claude.com/en/docs/claude-code)
- For write, build, deploy, and test: a **Windows** host with **TwinCAT 3.1 Build 4026** and **TcXaeShell**. Reads work without it.

The plugin uses [`uv`](https://docs.astral.sh/uv/) under the hood. If you don't have it, `pip install uv` will do.

## Plugin (recommended)

In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled `tc-config` skill walks you through the prompts and writes your config to `~/.tckit/config.toml`. The MCP server runs as `uvx tckit`, fetching the package from PyPI on first use; updates happen automatically.

Skip to [Bridge Setup](bridge-setup.md) if you need write/build/deploy/test.

## pip (without the plugin)

If you want to manage the MCP server yourself rather than going through the plugin:

```bash
pip install tckit
tckit init                  # write ~/.tckit/config.toml from the bundled template
$EDITOR ~/.tckit/config.toml
tckit doctor                # health check
```

Then register it with Claude Code:

```bash
claude mcp add tckit -- tckit
```

`tckit init --print` emits the template to stdout if you'd rather drive your own scaffolding.

## Docker (CI / dev only)

Docker mode is supported for CI and contributor workflows, not as a user install path. The container can't reach Windows host paths passed in from Claude Code, so it works only against projects mounted at the same path the agent will request. See [Docker Setup](docker-setup.md) for details and the [open caveat](https://github.com/georgeturneruk/tckit/issues/43).

## Bridge (Windows, for write/build/deploy/test)

Both install paths use the same bridge service for write operations. In a separate PowerShell window with TcXaeShell open:

```powershell
.\bridge\Start-Bridge.ps1
```

See [Bridge Setup](bridge-setup.md) for firewall and XAE-mode details.

## Verify

```bash
tckit doctor        # config + bridge health check
tckit --help        # available subcommands and flags
```

To verify end-to-end, ask Claude Code (or any MCP client) to call a TcKit tool, for example `get_structure` against a TwinCAT project path.
