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

LLMs get worse as their context fills up. Anthropic call this [context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents), and PLC projects trigger it fast: a single `.TcPOU` is XML wrapped around code, thousands of lines for one function block. Pasting one in to ask about one method poisons the rest of the conversation.

TcKit is the layer in between. Six MCP capabilities, each shaped around just-in-time retrieval, a single source of truth for writes, and structured results from builds and tests.

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

| Task | Vanilla tokens | TcKit tokens | Vanilla wall | TcKit wall | Tool calls (V → T) |
|---|---|---|---|---|---|
| Add a `VAR_INPUT` to an FB | 1,653 | **691** (2.4×) | 27.5s | **21.7s** (1.27×) | 5 → 3 |
| Add a method to an FB | 1,236 | **508** (2.4×) | 26.2s | **15.5s** (1.69×) | 5 → 2 |

N=1 per cell. See [`bench/findings/`](bench/findings/) for full methodology and a record of the harness gotchas behind the numbers.

## See it in action

The doc generator run against [TcUnit](https://github.com/tcunit/TcUnit) is published live at **[tckit.org/examples/tcunit/](https://tckit.org/examples/tcunit/)**. Navigate the function block hierarchy, search the API, drill into a method, all rendered from comments in TcUnit's ST source.

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

**Docker (opt-in).** For isolation or remote-server installs, see [tckit.org/getting-started/docker-setup/](https://tckit.org/getting-started/docker-setup/).

**Bridge.** For write/build/deploy/test, run the bridge in a separate PowerShell window with TcXaeShell open:

```powershell
.\bridge\Start-Bridge.ps1
```

## Status

All six capabilities are implemented and shipping. See [releases](https://github.com/georgeturneruk/tckit/releases) for version history.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). MIT licence.
