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

`build` takes the project path as an explicit argument; `deploy` and `start_runtime` operate on the solution open in the attached XAE. Required environment variables: `BRIDGE_URL`. Optional: `PLC_PROJECT_NAME`, `TCKIT_BUILD_TIMEOUT` (seconds, default 600), `DEVENV_PATH` (path to `TcXaeShell.exe` if not at the default install location), `TC_BUILD_CONFIG` (default `Release`), `TC_BUILD_PLATFORM` (default `TwinCAT RT (x64)`).

## How it works

Two-tier build inside `Invoke-TcBuild.ps1`:

1. **Tier 1 — fast in-process compile check.** Calls `ITcPlcIECProject2.CheckAllObjects()` on the PLC project node. Returns `True` when the PLC source compiles cleanly, `False` when there are errors. This is the happy-path signal — no extra processes spawned.
2. **Tier 2 — structured diagnostics.** When tier 1 returns `False` (or `ForceLog` is set), the harness reads the IDE **Error List** (`DTE.ToolWindows.ErrorList`), which holds the actual PLC errors, warnings and info messages with file, line, compiler `code` and `project`. On TcXaeShell **Express**, which doesn't expose the Error List, it falls back to a `/rebuild … /out <build.txt>` build-output parse:

    ```powershell
    TcXaeShell.exe <sln> /rebuild "Release|TwinCAT RT (x64)" /out <build.txt>
    ```

## Error format

```json
{
  "success": false,
  "errors": [
    {
      "file": "FB_MotorControl.TcPOU",
      "line": 42,
      "message": "'nSpeed' is not declared",
      "severity": "error",
      "code": "C0046",
      "project": "MyPlc"
    }
  ],
  "warnings": [],
  "infos": [],
  "duration_seconds": 5.6,
  "details": { "plc": "MyPlc", "check_all_objects": false }
}
```

`deploy()` and `start_runtime()` route through the same bridge to `Invoke-TcDeploy.ps1` / `Invoke-TcRuntime.ps1`, which call `ActivateConfiguration()` and `StartRestartTwinCAT()` respectively after `SetTargetNetId(<TargetAmsId>)`.
