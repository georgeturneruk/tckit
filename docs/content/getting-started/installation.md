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

The bundled skills walk you through setup. No .NET SDK is required: if the SDK is present the plugin builds the server from source, otherwise it downloads the self-contained server for your platform (~75 MB, cached per version) from the [releases page](https://github.com/georgeturneruk/tckit/releases).

The launcher runs on Node, which Claude Code already needs, so the plugin installs the same way on Windows and Linux. On a host without `node` on the `PATH`, set `TCKIT_SERVER_EXE` (below) and the launcher is bypassed entirely.

## Prebuilt binary

For MCP clients other than the plugin, or for offline and locked-down machines, download the server for your platform from the [releases page](https://github.com/georgeturneruk/tckit/releases). Both are self-contained, with no .NET runtime or SDK dependency.

| Asset | Host | Lanes |
|---|---|---|
| `tckit-server-win-x64.exe` | Windows x64 | everything, including the COM-backed build and hardware lanes |
| `tckit-server-linux-x64` | Linux x64 | readers, analysis, ADS, docs, and the xml writer backend; no COM-backed lanes |

```
claude mcp add tckit -- <path>\tckit-server-win-x64.exe
```

```
chmod +x tckit-server-linux-x64
claude mcp add tckit -- <path>/tckit-server-linux-x64
```

To point the plugin launcher at a pre-placed copy instead of letting it download, set the `TCKIT_SERVER_EXE` environment variable to its full path.

## From source

With the .NET 8 SDK:

```
dotnet build dotnet/TcKit.sln -c Release
claude mcp add tckit -- <clone>\dotnet\src\TcKit.Server\bin\Release\net8.0-windows\TcKit.Server.exe
```

On Linux, build the plain `net8.0` flavour; the `net8.0-windows` one carries the COM lanes and only builds a Windows artefact:

```
dotnet build dotnet/src/TcKit.Server -c Release -f net8.0
claude mcp add tckit -- <clone>/dotnet/src/TcKit.Server/bin/Release/net8.0/TcKit.Server
```

The server speaks MCP over stdio; any MCP client can register the built binary the same way.

## Verify

Ask your MCP client to call `Ping`, then `GetStructure` against a TwinCAT project path.

## Next

Set the [safety stance](permissions.md) before pointing TcKit at a live PLC.
