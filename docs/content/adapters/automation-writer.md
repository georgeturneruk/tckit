# automation writer

**Port:** `ProjectWriter`  
**Module:** `tckit.adapters.writers.automation_writer.AutomationWriter`  
**Status:** Phase 2 — planned

Writes ST code and structural elements via the Windows bridge → `TcXaeShell.DTE.17.0` COM automation interface.

## Configuration

```json
{ "writer": "automation_interface" }
```

Requires the bridge service to be running. Set `BRIDGE_URL` in `.env`.

## How it works

Calls `CreateChild()` (new POUs/methods) and `ConsumeXml()` (code updates) on the `ITcSmTreeItem` automation interface. GUIDs are assigned automatically by XAE — never generated manually.
