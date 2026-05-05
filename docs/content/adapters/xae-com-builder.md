# xae_com builder

**Port:** `BuildRunner`  
**Module:** `tckit.adapters.builders.xae_com_builder.XaeComBuilder`  
**Status:** Phase 2 — planned

Builds and deploys TwinCAT projects via the Windows bridge → COM automation interface. Returns structured build errors with file, line, message, and severity.

## Configuration

```json
{
  "builder": "xae_com",
  "xae_mode": "attach",
  "com_version": "17.0"
}
```

## Error format

```json
{
  "success": false,
  "errors": [
    {
      "file": "FB_MotorControl.TcPOU",
      "line": 42,
      "message": "Identifier 'nSpeed' not declared",
      "severity": "error"
    }
  ]
}
```
