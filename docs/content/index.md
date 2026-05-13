# TcKit

**An MCP server that gives AI agents a precise, structured view of a TwinCAT 3 project — and the tools to change, build, and test it.**

---

## Why TcKit

LLMs do not get smarter when you give them more tokens. Quality degrades as context fills up — Anthropic call this [context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents), and PLC projects are an unusually fast way to trigger it: a single `.TcPOU` file is XML wrapped around code, easily thousands of lines for one function block. Pasting one in to ask about one method poisons the rest of the conversation.

TcKit is the layer in between. Instead of dumping files at the model, it exposes **six capabilities** shaped around the patterns Anthropic recommend for agent tools: layered just-in-time reads, a single source of truth for writes, and structured results from builds and tests.

---

## What it solves

| Problem | What goes wrong without it | TcKit's answer |
|---|---|---|
| **Context rot** as tokens accumulate | Whole-POU paste to ask about one method | [**ProjectReader**](capabilities/project-reader/overview.md) — three precision levels |
| **Pre-loading vs just-in-time retrieval** | Stuffing manuals into context "just in case", or hallucinating vendor FB signatures | [**DocsSearcher**](capabilities/docs-searcher/overview.md) — fetch one page on demand |
| **Drifting sources of truth** | Hand-edited XML diverging from project cross-refs | [**ProjectWriter**](capabilities/project-writer/overview.md) — IDE stays authoritative |
| **Unstructured tool output** | Scraping raw logs for the one line that matters | [**BuildRunner**](capabilities/build-runner/overview.md) + [**TestRunner**](capabilities/test-runner/overview.md) — parsed results |
| **Stable surface under churn** | Re-prompting when the underlying tooling shifts | **Ports & adapters** — swap the adapter, tool shape stays |
| **Code as source of truth for docs** | Drifting wikis next to authoritative code | [**DocGenerator**](capabilities/doc-generator/overview.md) — render docs from ST comments |

---

## Capabilities at a glance

| Port | What it does | State |
|---|---|---|
| [ProjectReader](capabilities/project-reader/overview.md) | Read POUs, interfaces, methods, GVLs, DUTs at three precision levels | Complete |
| [DocsSearcher](capabilities/docs-searcher/overview.md) | Fetch vendor documentation pages on demand | Complete |
| [DocGenerator](capabilities/doc-generator/overview.md) | Render docs from comments in ST source | Complete |
| [ProjectWriter](capabilities/project-writer/overview.md) | Structural writes via the IDE's authoring interface | Complete |
| [BuildRunner](capabilities/build-runner/overview.md) | Build, deploy, runtime control with structured diagnostics | Complete |
| [TestRunner](capabilities/test-runner/overview.md) | Run unit tests, return parsed suite/test trees | Complete |

---

## Quick start

In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled `tc-config` skill walks you through the prompts. The MCP server runs as `uvx tckit`, fetching the package from PyPI on first use.

For write, build, deploy, and test, you also need the Windows bridge running. See [Getting Started → Installation](getting-started/installation.md) for the pip and Docker paths, and [Bridge Setup](getting-started/bridge-setup.md) for the bridge.

---

## Design philosophy

TcKit uses a **ports & adapters** (hexagonal) architecture. Every external concern is abstracted behind a port (Python ABC). Adapters implement ports. The MCP server only calls ports.

**The one hard rule:** adapters may only import from ports and stdlib. Never from each other.

This means when the underlying tooling changes — a new IDE COM version, a new build CLI, a different docs source — you write a new adapter and change one config value. The port contract, and therefore the agent-facing tool surface, does not change.

See [Architecture → Overview](architecture/overview.md) for the full picture.
