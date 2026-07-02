# Build & deploy

Build and deploy operate on the solution open in TcXaeShell, via the Automation Interface. Build before deploy, always.

| Tool | Purpose |
|---|---|
| `Build(forceLog?, plcName?)` | Compile check (`CheckAllObjects`) with structured diagnostics |
| `Deploy(targetAmsId, bootAutostart?, plcName?)` | Activate the configuration on a target runtime |

## Build

Fast path first: a clean compile returns success without reading anything else. On failure (or with `forceLog=true`, useful for warnings), the diagnostics come from the IDE's Error List:

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
  "infos": []
}
```

Never a raw log blob. A 2,000-line build log forces the model to scan and guess; a list of structured diagnostics lets it act — the [high-signal tool result](https://www.anthropic.com/engineering/writing-tools-for-agents) principle.

## Deploy

`Deploy` sets the target NetId and activates the configuration. `bootAutostart` (default true) regenerates the boot project with autostart, so the PLC actually runs and serves ADS symbols after the restart.

Deploy acts on a live target: it is execute-class and gated by the [safety stance](../../getting-started/permissions.md) (mode + NetId allow/block). To bring the target into Run mode afterwards, see [Testing](../test-runner/overview.md) for `StartRuntime`.
