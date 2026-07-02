# Installation

## Requirements

- [Claude Code](https://docs.claude.com/en/docs/claude-code) (or any MCP client)
- A Windows host with **TwinCAT 3.1 Build 4026** and **TcXaeShell**

Reading projects, generating docs, and searching infosys work without TwinCAT installed. Writes, builds, and hardware authoring need TcXaeShell open with the solution loaded; runtime tools (deploy, tests, symbols, diagnostics) need an ADS route to the target.

## Plugin (recommended)

In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled skills walk you through setup.

## From source

With the .NET 8 SDK:

```
dotnet build dotnet/TcKit.sln -c Release
claude mcp add tckit -- <clone>\dotnet\src\TcKit.Server\bin\Release\net8.0-windows\TcKit.Server.exe
```

The server speaks MCP over stdio; any MCP client can register the built exe the same way.

## Verify

Ask your MCP client to call `Ping`, then `GetStructure` against a TwinCAT project path.

## Next

Set the [safety stance](permissions.md) before pointing TcKit at a live PLC.
