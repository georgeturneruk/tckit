# Architecture Overview

TcKit is a single .NET 8 process with a **ports & adapters** layout. The MCP server calls port interfaces; adapters implement them against the external tool.

```
AI agent (any MCP client)
    │  MCP (stdio)
    ▼
TcKit.Server ── permission gate (~/.tckit/permissions.json)
    │  port interfaces (TcKit.Core)
    ▼
Reader        offline XML parse of .TcPOU / .TcGVL / .TcDUT
Automation    COM Automation Interface → TcXaeShell (writes, builds, I/O config)
Ads           Beckhoff.TwinCAT.Ads + TwinSharp → live runtime (deploy, tests, symbols, diagnostics)
Docs          Beckhoff infosys over HTTP, disk-cached
DocGen        doc site renderer, local source only
```

## Source tree

```
dotnet/
├── src/
│   ├── TcKit.Core/                 ← ports (interfaces), models, permission gate
│   ├── TcKit.Server/               ← MCP server + tool definitions
│   ├── TcKit.Cli/                  ← dev CLI driving the same adapters
│   ├── TcKit.Adapters.Xml/
│   ├── TcKit.Adapters.Automation/
│   ├── TcKit.Adapters.Ads/
│   ├── TcKit.Adapters.Docs/
│   └── TcKit.Adapters.DocGen/
└── tests/TcKit.Tests/
```

## The adapter isolation rule

**Adapters reference only `TcKit.Core` (and external packages), never each other.** The project graph enforces it. Shared logic belongs in `TcKit.Core`, not in a sibling adapter.

## Tools at a glance

| Capability | Tools |
|---|---|
| [Reader](../capabilities/project-reader/overview.md) | `GetStructure`, `GetPouInterface`, `GetPouDeclaration`, `GetPouItem`, `GetGvl`, `GetDut` |
| [Writer](../capabilities/project-writer/overview.md) | `OpenProject`, `CreateProject`, `AddPlcProject`, `AddPou`, `AddGvl`, `AddDut`, `AddMethod`, `AddProperty`, `AddVariable`, `AddFolder`, `UpdatePouDeclaration`, `UpdatePouImplementation`, `UpdateMethodBody` (+ `...Patch` variants), `Delete...` counterparts, `SavePlcAsLibrary`, `AddLibraryReference`, `AddLibraryPlaceholder`, `SetPlaceholderParameters`, `DeleteLibraryReference`, `DeletePlaceholder` |
| [Build & deploy](../capabilities/build-runner/overview.md) | `Build`, `Deploy` |
| [Testing](../capabilities/test-runner/overview.md) | `StartRuntime`, `RunTests`, `GetTestResults` |
| [Hardware](../capabilities/hardware/overview.md) | `ListEtherCatMasters`, `GetEtherCatStatus`, `GetIpcHardware`, `ListAxes`, `GetAxisState`, `ReadSymbols`, `WriteSymbols`, `InvokeRpc`, `ScanHardware`, `ScaffoldHardwareCode`, `AddEtherCatMaster`, `AddEtherCatBox`, `DeleteIoDevice` |
| [Docs search](../capabilities/docs-searcher/overview.md) | `FindFb`, `SearchDocs`, `FindHardware`, `GetDocPage` |
| [Doc generator](../capabilities/doc-generator/overview.md) | `GenerateDocs`, `GetDocStatus` |
| Safety / diagnostics | `GetPermissions`, `SetPermissions`, `Ping` |

Tool names are PascalCase, parameters camelCase, result JSON snake_case. Every result is structured JSON; failures come back as `{"error": "..."}`, never a raw log.

## Adding a new adapter or port

See [CONTRIBUTING.md](https://github.com/georgeturneruk/tckit/blob/main/CONTRIBUTING.md) in the repo root for the contributor guide.
