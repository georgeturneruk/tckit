<p align="center">
  <img src="https://raw.githubusercontent.com/georgeturneruk/tckit/main/docs/content/assets/logo-full.svg" alt="TcKit" width="100">
</p>

# TcKit

TwinCAT MCP server.

What can it do?

 - Read and write structured text code, and deploy it to a runtime
 - Read and write live variables over ADS
 - Write and run tests with TcUnit
 - More: see the [full tool reference](https://tckit.org/architecture/overview/)

**[tckit.org](https://tckit.org)** for full documentation.

---

## Why TcKit

TwinCAT programming isn't like other software development. Code is stored in a proprietary format and there's no command line runner. Everything has to go through the Windows-based XAE.

Agents can manually manipulate TwinCAT files, but it's inefficient and can cause project corruption and instability. TcKit's writer goes through Beckhoff's Automation Interface. It avoids manual edits. Instead it uses the exact same mechanism as the XAE.

The reader is more efficient than having an agent manually sift through large amounts of XML (the format of TwinCAT files). [Context rot](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) kills performance. TcKit prevents that.

And some of it simply isn't possible without extra tooling: reading live variables over ADS, EtherCAT diagnostics, running TcUnit suites and getting parsed results back.

## See it in action

The doc generator run against [TcUnit](https://github.com/tcunit/TcUnit) is published at **[tckit.org/examples/tcunit/](https://tckit.org/examples/tcunit/)**. Under the hood, the same understanding of TwinCAT's XML powers the reader: an agent navigates a project the way you would, never loading more than the question needs.

## Quick Start

> [!CAUTION]
> `Deploy`, `StartRuntime`, `RunTests`, `WriteSymbols`, and `InvokeRpc` act on a live PLC. Always verify the target NetId. The safety stance lives in `~/.tckit/permissions.json`: set `mode` (`read` / `write` / `execute`), and list targets in `blocked_net_ids` to permanently block them (e.g. a production PLC). Live writes additionally require explicit `confirmed=true`.

Requires Claude Code, plus TwinCAT 3.1 Build 4026 + TcXaeShell on a Windows host.

**Plugin (recommended).** In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled skills walk you through setup.

**From source.** With the .NET 8 SDK:

```
dotnet build dotnet/TcKit.sln -c Release
claude mcp add tckit -- <clone>\dotnet\src\TcKit.Server\bin\Release\net8.0-windows\TcKit.Server.exe
```

## Contributing

See [CONTRIBUTING.md](https://github.com/georgeturneruk/tckit/blob/main/CONTRIBUTING.md). MIT licence.
