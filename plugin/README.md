# TcKit (Claude Code plugin)

Connects Claude Code to TwinCAT 3 PLC projects. Read project structure, write Structured Text, build, deploy, run TcUnit tests, and inspect or author EtherCAT hardware.

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

The bundled `tc-config` skill sets your safety stance (mode and target NetId allow/block lists) in `~/.tckit/permissions.json`.

## Prerequisites

- **Claude Code** (CLI / IDE extension / desktop app).
- **.NET 8 SDK** — the plugin builds and runs the bundled C# MCP server on first use.
- **TwinCAT 3.1 Build 4026** + **TcXaeShell** on a Windows host. Writes, builds, and hardware authoring need TcXaeShell open; reads and docs work without it; runtime tools need an ADS route to the target.

## What you get

Seven skills that load on demand based on what you ask:

| Skill | Loads when |
|-------|------------|
| `tckit:tc-orient-project` | First touch on a TwinCAT project ("what's in this project") |
| `tckit:tc-read-project` | Inspecting / navigating a TwinCAT project |
| `tckit:tc-beckhoff-docs` | Researching a Beckhoff library FB or a hardware product by order number |
| `tckit:tc-write-st` | Writing or modifying Structured Text |
| `tckit:tc-build-test-loop` | Building, deploying, running TcUnit tests |
| `tckit:tc-hardware` | EtherCAT/IPC/axis diagnostics, live symbols, authoring the I/O tree |
| `tckit:tc-config` | Setting or editing the safety stance |

Plus the underlying MCP server exposing the full tool surface: reader, writer, build/deploy, TcUnit tests, live symbols, hardware diagnostics and authoring, Beckhoff infosys search, and the doc generator.

## Documentation

- Project docs: <https://tckit.org>
- Source: <https://github.com/georgeturneruk/tckit>
- Issues: <https://github.com/georgeturneruk/tckit/issues>

## Licence

MIT. See [LICENSE](LICENSE).
