<p align="center">
  <img src="docs/content/assets/logo-full.svg" alt="TcKit" width="100">
</p>

# TcKit

An MCP server that gives AI agents a precise, structured view of a TwinCAT 3 project, and the tools to change, build, and test it.

**[tckit.org](https://tckit.org)** for full documentation.

---

> [!WARNING]
> **TcKit is in active development and not yet production-ready.** Expect breaking changes between minor versions, rough edges, and missing features.

---

## Why TcKit

LLMs get worse as their context fills up. Anthropic call this [context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents). TwinCAT's file format makes it worse: a `.TcPOU` is XML wrapped around code, easily thousands of lines for one function block. That XML contaminates context the moment it's loaded.

TcKit is the layer in between. It groups six MCP capabilities around three ideas: just-in-time retrieval for reads, a single source of truth for writes, and structured results from builds and tests.

Wired together, the model runs that loop without an engineer walking each cycle: write a method, build, test, fix the failure, build again.

## Capabilities

| Port | Purpose |
|---|---|
| **ProjectReader** | Layered reads: project structure → POU interface → single method/property |
| **ProjectWriter** | Structural writes via the IDE so GUIDs and cross-refs stay consistent |
| **BuildRunner** | Build, deploy, and runtime control with parsed `{file, line, message, severity}` diagnostics |
| **TestRunner** | Run TcUnit suites and return parsed pass/fail trees |
| **DocGenerator** | Render navigable HTML docs from comments in the ST source |
| **DocsSearcher** | Fetch the one relevant Beckhoff infosys page on demand, no manual pre-loading |

Each port is a stable contract; adapters are swappable. See [tckit.org](https://tckit.org) for method tables and rationale.

## Benchmarks

Head-to-head writer-task runs of TcKit-equipped Claude vs vanilla Claude:

| Task | Tokens | Wall time | Tool calls |
|---|---|---|---|
| Add a `VAR_INPUT` to an FB | **2.4× fewer** (1,653 → 691) | **1.27× faster** (27.5s → 21.7s) | 5 → 3 |
| Add a method to an FB | **2.4× fewer** (1,236 → 508) | **1.69× faster** (26.2s → 15.5s) | 5 → 2 |

N=1 per cell. See [`bench/findings/`](bench/findings/) for full methodology and a record of the harness gotchas behind the numbers.

## See it in action

The doc generator run against [TcUnit](https://github.com/tcunit/TcUnit) is published live at **[tckit.org/examples/tcunit/](https://tckit.org/examples/tcunit/)**. Navigate the function block hierarchy, search the API, drill into a method, all rendered from TcUnit's source code. Under the hood, the same understanding of TwinCAT's XML powers ProjectReader: an agent reads a project the way you would, never loading more than the question needs.

## Architecture

```
AI agent (MCP client) → TcKit MCP Server → Port (ABC) → Adapter → TwinCAT XAE / PLC
```

The server only calls ports. Adapters may only import from ports and stdlib, never from each other. CI enforces it. Full diagram at [tckit.org/architecture/overview/](https://tckit.org/architecture/overview/).

## Quick Start

> [!CAUTION]
> The `deploy` and `start_runtime` tools write to and restart a running PLC. They require explicit `confirmed=True` by default. Always verify the target NetId. Set `BLOCKED_NETIDS=<netid>,...` to permanently block targets (e.g. a production PLC).

Requires Claude Code, plus TwinCAT 3.1 Build 4026 + TcXaeShell on a Windows host (only for write/build/deploy/test; reads work without it).

**Plugin (recommended).** Needs [`uv`](https://docs.astral.sh/uv/). In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled `tc-config` skill walks you through setup. The MCP server runs as `uvx tckit`.

**pip.** If you'd rather manage the MCP server yourself: `pip install tckit`, then `tckit init` to scaffold `~/.tckit/config.toml`, then `claude mcp add tckit -- tckit`.

**Docker.** CI / containerised dev only. See [tckit.org/getting-started/docker-setup/](https://tckit.org/getting-started/docker-setup/) for the caveats.

**Bridge.** For write/build/deploy/test, run the bridge in a separate PowerShell window with TcXaeShell open:

```powershell
.\bridge\Start-Bridge.ps1
```

## Status

All six capabilities are implemented and shipping. See [releases](https://github.com/georgeturneruk/tckit/releases) for version history.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). MIT licence.
