# TcKit

TwinCAT MCP server.

What can it do?

 - Read and write structured text code, and deploy it to a runtime
 - Read and write live variables over ADS
 - Write and run tests with TcUnit
 - Inspect and author EtherCAT hardware
 - Look up Beckhoff FBs and hardware datasheets, generate project docs

---

## Why TcKit

TwinCAT programming isn't like other software development. Code is stored in a proprietary format and there's no command line runner. Everything has to go through the Windows-based XAE.

Agents can manually manipulate TwinCAT files, but it's inefficient and can cause project corruption and instability. TcKit's writer goes through Beckhoff's Automation Interface. It avoids manual edits. Instead it uses the exact same mechanism as the XAE.

The reader is more efficient than having an agent manually sift through large amounts of XML (the format of TwinCAT files). [Context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) kills performance. TcKit prevents that.

And some of it simply isn't possible without extra tooling: reading live variables over ADS, EtherCAT diagnostics, running TcUnit suites and getting parsed results back.

## Capabilities

| Capability | Purpose |
|---|---|
| [**Reader**](capabilities/project-reader/overview.md) | Layered reads: project structure, POU interface, single method or property |
| [**Writer**](capabilities/project-writer/overview.md) | Structural writes via the Automation Interface so GUIDs and cross-refs stay consistent |
| [**Build & deploy**](capabilities/build-runner/overview.md) | Build with parsed `{file, line, message, severity}` diagnostics, deploy to a target |
| [**Testing**](capabilities/test-runner/overview.md) | Run TcUnit suites and return parsed pass/fail trees |
| [**Hardware**](capabilities/hardware/overview.md) | EtherCAT/IPC/axis diagnostics, live symbol I/O, I/O tree scanning and authoring |
| [**Doc generator**](capabilities/doc-generator/overview.md) | HTML or Markdown docs rendered from comments in the ST source |
| [**Docs search**](capabilities/docs-searcher/overview.md) | Fetch the one relevant Beckhoff infosys page on demand, no manual pre-loading |

Full tool list on the [architecture overview](architecture/overview.md).

## See it in action

The doc generator run against [TcUnit](https://github.com/tcunit/TcUnit) is published at [tckit.org/examples/tcunit/](https://tckit.org/examples/tcunit/). Under the hood, the same understanding of TwinCAT's XML powers the reader: an agent navigates a project the way you would, never loading more than the question needs.

## Quick start

See [Installation](getting-started/installation.md). Before pointing TcKit at a live PLC, set the [safety stance](getting-started/permissions.md).
