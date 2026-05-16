# TcKit

An MCP server that gives AI agents a precise, structured view of a TwinCAT 3 project, and the tools to change, build, and test it.

---

## Why TcKit

LLMs get worse as their context fills up. Anthropic call this [context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents). TwinCAT's file format makes it worse: a `.TcPOU` is XML wrapped around code, easily thousands of lines for one function block. That XML contaminates context the moment it's loaded.

TcKit is the layer in between. It groups six MCP capabilities around three ideas: just-in-time retrieval for reads, a single source of truth for writes, and structured results from builds and tests.

Wired together, the model runs that loop without an engineer walking each cycle: write a method, build, test, fix the failure, build again.

## Capabilities

| Port | Purpose |
|---|---|
| [**ProjectReader**](capabilities/project-reader/overview.md) | Layered reads: project structure, POU interface, single method or property |
| [**ProjectWriter**](capabilities/project-writer/overview.md) | Structural writes via the IDE so GUIDs and cross-refs stay consistent |
| [**BuildRunner**](capabilities/build-runner/overview.md) | Build, deploy, and runtime control with parsed `{file, line, message, severity}` diagnostics |
| [**TestRunner**](capabilities/test-runner/overview.md) | Run TcUnit suites and return parsed pass/fail trees |
| [**DocGenerator**](capabilities/doc-generator/overview.md) | Render navigable HTML docs from comments in the ST source |
| [**DocsSearcher**](capabilities/docs-searcher/overview.md) | Fetch the one relevant Beckhoff infosys page on demand, no manual pre-loading |

Each port is a stable contract; adapters are swappable. See [Architecture → Overview](architecture/overview.md) for method tables and rationale.

## Benchmarks

Head-to-head writer-task runs of TcKit-equipped Claude vs vanilla Claude:

| Task | Tokens | Wall time | Tool calls |
|---|---|---|---|
| Add a `VAR_INPUT` to an FB | **2.4× fewer** (1,653 → 691) | **1.27× faster** (27.5s → 21.7s) | 5 → 3 |
| Add a method to an FB | **2.4× fewer** (1,236 → 508) | **1.69× faster** (26.2s → 15.5s) | 5 → 2 |

N=1 per cell. See [`bench/findings/`](https://github.com/georgeturneruk/tckit/tree/main/bench/findings) for full methodology.

## See it in action

The doc generator run against [TcUnit](https://github.com/tcunit/TcUnit) is published live at [tckit.org/examples/tcunit/](https://tckit.org/examples/tcunit/). Navigate the function block hierarchy, search the API, drill into a method, all rendered from TcUnit's source code. Under the hood, the same understanding of TwinCAT's XML powers [ProjectReader](capabilities/project-reader/overview.md): an agent reads a project the way you would, never loading more than the question needs.

## Quick start

!!! warning
    **TcKit is in active development and not yet production-ready.** Expect breaking changes between minor versions, rough edges, and missing features.

See [Getting Started → Installation](getting-started/installation.md). Plugin install in Claude Code is one command; for write, build, deploy, and test you also need the Windows bridge running.

## Status

All six capabilities are implemented and shipping. See [releases](https://github.com/georgeturneruk/tckit/releases) for version history.
