<p align="center">
  <img src="docs/content/assets/logo-full.svg" alt="TcKit" width="100">
</p>

# TcKit

An MCP server that gives AI agents a precise, structured view of a TwinCAT 3 project — and the tools to change, build, and test it.

**[tckit.org](https://tckit.org)** — full documentation

---

> [!WARNING]
> **TcKit is in active development and not yet production-ready.** Expect breaking changes between minor versions, rough edges, and missing features. The Phase 1 read layer is stable; writer/builder/test paths and the plugin install flow are new in v0.2.0 and have not been battle-tested across a wide range of TwinCAT setups. Use against test PLCs and dev VMs, not production. [Open an issue](https://github.com/georgeturneruk/tckit/issues) if you hit something.

---

## Why TcKit

LLMs do not get smarter when you give them more tokens. Quality degrades as context fills up — Anthropic call this [context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents), and PLC projects are an unusually fast way to trigger it: a single `.TcPOU` file is XML wrapped around code, easily thousands of lines for one function block. Pasting one in to ask about one method poisons the rest of the conversation.

TcKit is the layer in between. Instead of dumping files at the model, it exposes **six capabilities** shaped around the patterns Anthropic recommends for agent tools: layered just-in-time reads, a single source of truth for writes, and structured results from builds and tests.

---

## What it solves

| Problem | What goes wrong without it | TcKit's answer |
|---|---|---|
| **Context rot** as tokens accumulate | Whole-POU paste to ask about one method | **ProjectReader** — three precision levels from project structure → POU interface → single item |
| **Pre-loading vs just-in-time retrieval** | Stuffing library manuals into context "just in case", or hallucinating vendor FB signatures | **DocsSearcher** — fetch the one relevant doc page on demand |
| **Drifting sources of truth** | Hand-edited XML diverging from project cross-refs; agent-invented GUIDs | **ProjectWriter** — every structural change goes through the IDE, which stays authoritative |
| **Unstructured tool output** | Scraping raw build/test logs for the one line that matters | **BuildRunner** and **TestRunner** — parsed, structured results |
| **Stable surface under churn** | Re-prompting every time the underlying tooling shifts | **Ports & adapters** — swap the adapter, the tool shape stays the same |

---

## The six capabilities

Each port is the stable contract; adapters are interchangeable implementations behind it.

**ProjectReader** — read project structure and code at three precision levels.
`get_structure` · `get_pou_interface` · `get_pou_item` · `get_gvl` · `get_dut`
*Why this shape:* the agent pulls only what it needs, no whole-file dumps.

**ProjectWriter** — structural writes via the IDE, never raw XML.
`open_project` · `create_project` · `add_pou` · `add_method` · `update_pou_item`
*Why this shape:* the IDE owns GUIDs, cross-refs, and tree state. One source of truth.

**BuildRunner** — build, deploy, and runtime control with structured diagnostics.
`build` · `deploy` · `start_runtime` · `get_status`
*Why this shape:* errors come back as `{file, line, message, severity}`, not log scrape.

**TestRunner** — run unit tests and return parsed results.
`run_tests` · `wait_complete` · `get_results` · `get_status`
*Why this shape:* a suite/test tree the agent can act on; failing assertions point straight at a POU item.

**DocGenerator** — render documentation from comments embedded in the ST source.
`generate` · `get_status`
*Why this shape:* code-as-source-of-truth for docs. No drifting wiki.

**DocsSearcher** — fetch vendor documentation pages on demand.
`find_fb` · `find_library` · `search` · `get_page`
*Why this shape:* one page, fetched when needed — instead of pre-loading manuals or hallucinating signatures.

See [Capabilities](https://tckit.org) on the docs site for the full method tables and rationale.

---

## Architecture

```
AI agent (any MCP client)
        │  MCP protocol
        ▼
 TcKit MCP Server          (Docker)
        │
        ├── ProjectReader  ──► reader adapter
        ├── ProjectWriter  ──► writer adapter      ──┐
        ├── BuildRunner    ──► build adapter       ──┤ Windows bridge
        ├── TestRunner     ──► test adapter        ──┤ (PowerShell → XAE)
        ├── DocGenerator   ──► doc-gen adapter        │
        └── DocsSearcher   ──► docs-search adapter ──┘
                                        │
                                  TwinCAT XAE
                                        │
                                   PLC / VM
```

Adapters are isolated behind port interfaces (Python ABCs). The server only calls ports — never adapters directly. Adapters may only import from ports and stdlib.

---

## Quick Start

> [!CAUTION]
> TcKit is an **engineering tool for development environments**. The `deploy` and `start_runtime` tools write to and restart a running PLC. By default these require explicit `confirmed=True` — always verify the target NetId. Set `SAFETY_CONFIRMATIONS=false` only on closed networks. Set `BLOCKED_NETIDS=<netid>,...` to permanently block targets (e.g. a production PLC); blocked NetIds cannot be bypassed even with `confirmed=True`.

**Requirements (both paths):** Claude Code, TwinCAT 3.1 Build 4026 + TcXaeShell on a Windows host (only for write/build/deploy/test; reads work without it). The Windows bridge service runs natively in PowerShell.

### Plugin (recommended)

Needs [`uv`](https://docs.astral.sh/uv/) installed (`pip install uv`). In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled `tc-config` skill walks you through prompts and writes `~/.tckit/config.toml`. The MCP server runs as `uvx tckit`, fetching the package from PyPI on first use.

### Docker (opt-in)

For users who want isolation, multi-client setups, or a remote-server install:

```bash
git clone https://github.com/georgeturneruk/tckit
cd tckit
# In Claude Code:
> /tc-config init        # pick "docker" mode, fill in prompts

docker compose -f docker/docker-compose.yml up -d
claude mcp add --transport sse tckit http://localhost:8000/sse
```

You can install the plugin separately just for the skills.

### Bridge (both paths)

For write/build/deploy/test, run the bridge in a separate PowerShell window with TcXaeShell open:

```powershell
.\bridge\Start-Bridge.ps1
```

---

## Status

| Capability | State |
|---|---|
| ProjectReader | Implemented |
| DocsSearcher | Implemented |
| DocGenerator | Implemented |
| ProjectWriter | Implemented |
| BuildRunner | Implemented |
| TestRunner | Planned |

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — MIT licence.
