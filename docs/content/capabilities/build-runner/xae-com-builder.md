# xae_com builder

**Port:** `BuildRunner`
**Module:** `tckit.adapters.builders.xae_com_builder.XaeComBuilder`
**Status:** Phase 2 — bridge + adapter validated end-to-end on TwinCAT 4026 (TcXaeShell Express).

Builds TwinCAT projects via the Windows bridge → COM automation interface and returns structured errors with file, line, message, and severity.

## Configuration

```json
{
  "builder": "xae_com",
  "xae_mode": "attach",
  "com_version": "17.0"
}
```

Required environment variables: `BRIDGE_URL`, `PLC_PROJECT_PATH`. Optional: `PLC_PROJECT_NAME`, `TCKIT_BUILD_TIMEOUT` (seconds, default 600), `DEVENV_PATH` (path to `TcXaeShell.exe` if not at the default install location), `TC_BUILD_CONFIG` (default `Release`), `TC_BUILD_PLATFORM` (default `TwinCAT RT (x64)`).

## How it works

Two-tier build inside `Invoke-TcBuild.ps1`:

1. **Tier 1 — fast in-process compile check.** Calls `ITcPlcIECProject2.CheckAllObjects()` on the PLC project node. Returns `True` when the PLC source compiles cleanly, `False` when there are errors. This is the happy-path signal — no extra processes spawned.
2. **Tier 2 — structured error retrieval.** When tier 1 returns `False` (or `ForceLog` is set), the harness shells out to:

    ```powershell
    TcXaeShell.exe <sln> /rebuild "Release|TwinCAT RT (x64)" /log <Log.xml>
    ```

    and parses the resulting log into structured `BuildError` rows. This is the only way to get per-error file/line/message detail on TcXaeShell **Express**, which does not expose `DTE.ToolWindows.ErrorList`.

## Error format

```json
{
  "success": false,
  "errors": [
    {
      "file": "FB_MotorControl.TcPOU",
      "line": 42,
      "message": "'nSpeed' is not declared",
      "severity": "error"
    }
  ],
  "warnings": [],
  "duration_seconds": 5.6,
  "details": { "plc": "MyPlc", "check_all_objects": false }
}
```

`deploy()` and `start_runtime()` route through the same bridge to `Invoke-TcDeploy.ps1` / `Invoke-TcRuntime.ps1`, which call `ActivateConfiguration()` and `StartRestartTwinCAT()` respectively after `SetTargetNetId(<TargetAmsId>)`.
