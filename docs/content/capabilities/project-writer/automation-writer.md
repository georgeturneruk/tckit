# automation writer

**Port:** `ProjectWriter`
**Module:** `tckit.adapters.writers.automation_writer.AutomationWriter`
**Status:** Phase 2 — bridge + adapter validated end-to-end on TwinCAT 4026 (TcXaeShell Express).

Writes ST code and structural elements via the Windows bridge → `TcXaeShell.DTE.17.0` COM automation interface.

## Configuration

```json
{ "writer": "automation_interface" }
```

Required environment variables:

| Variable | Purpose |
|----------|---------|
| `BRIDGE_URL` | Where the bridge service listens (default `http://localhost:8765`). |
| `PLC_PROJECT_PATH` | Absolute path to the active `.sln`. |
| `PLC_PROJECT_NAME` | (Optional) Name of the PLC sub-project under `TIPC`. Auto-resolved when there's only one. |

## How it works

For each MCP write operation, the adapter posts to a bridge endpoint:

| MCP tool | Bridge route | Harness script |
|----------|--------------|----------------|
| `open_project`     | `POST /open`   | `Open-TcProject.ps1` |
| `create_project`   | `POST /create` | `New-TcProject.ps1` |
| `add_pou`          | `POST /pou`    | `Add-TcPou.ps1` |
| `add_method`       | `POST /method` | `Add-TcMethod.ps1` |
| `update_pou_item`  | `POST /item`   | `Update-TcPouItem.ps1` |

The harness scripts navigate to the source items via the system manager tree (`TIPC^<plc>^<plc> Project^POUs^...`), call `ITcSmTreeItem.CreateChild` for new POUs / methods, then write source via the `DeclarationText` and `ImplementationText` properties (from `ITcPlcDeclaration` / `ITcPlcImplementation`). PowerShell's COM dispatch resolves these properties without explicit interface casts. GUIDs are assigned by XAE — never generated manually.

For the kind constants used by `CreateChild` (function block = 604, method = 609, property = 611, etc.) see [`scripts/SPIKE_NOTES.md`](https://github.com/georgeturneruk/tckit/blob/main/scripts/SPIKE_NOTES.md).
