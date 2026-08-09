# Installation

## Requirements

- [Claude Code](https://docs.claude.com/en/docs/claude-code) (or any MCP client)
- For the full stack: a Windows host with **TwinCAT 3.1 Build 4026** and **TcXaeShell**

Reading projects, generating docs, searching infosys, and authoring ST (via the [xml writer backend](../capabilities/project-writer/overview.md)) work without TwinCAT installed, on Windows or Linux. Builds and hardware authoring need TcXaeShell open with the solution loaded (on Windows the writer defaults to the same Automation Interface route); runtime tools (deploy, tests, symbols, diagnostics) need an ADS route to the target.

## Plugin (recommended)

In Claude Code:

```
/plugin marketplace add georgeturneruk/tckit
/plugin install tckit@tckit
> Set me up for TcKit.
```

The bundled skills walk you through setup. No .NET SDK is required: if the SDK is present the plugin builds the server from source, otherwise it downloads a self-contained server (~74 MB, cached per version) from the [releases page](https://github.com/georgeturneruk/tckit/releases).

## Prebuilt binary

For MCP clients other than the plugin, or for offline and locked-down machines, download `tckit-server-win-x64.exe` from the [releases page](https://github.com/georgeturneruk/tckit/releases). It is a self-contained build with no .NET runtime or SDK dependency. `tckit-server-linux-x64` is the Linux equivalent (readers, ADS, and the xml writer backend; no COM-backed lanes).

```
claude mcp add tckit -- <path>\tckit-server-win-x64.exe
```

To point the plugin launcher at a pre-placed copy instead of letting it download, set the `TCKIT_SERVER_EXE` environment variable to the exe's full path.

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
