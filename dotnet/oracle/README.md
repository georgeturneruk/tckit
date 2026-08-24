# Live smoke harnesses

During the port (ADR-0015), the Python TcKit served as the behavioural
reference: `compare.ps1` ran each reader through both implementations and
diffed the canonicalised output. The port is complete and the Python stack
deleted, so the parity oracle retired with it; what remains here are the live
smoke harnesses that exercise the C# verbs against a real 4026, plus the
section-index regeneration script for the infosys hardware docs
([`regen-hardware-sections.ps1`](regen-hardware-sections.ps1)).

## Writer smoke (live COM)

The automation authoring lane cannot be cross-checked against a fixture on disk:
the verbs mutate the solution open in TcXaeShell over COM. [`smoke-writer.ps1`](smoke-writer.ps1)
is the live gate instead, run on the bench box with XAE attached. It scaffolds a
throwaway solution + two PLCs, drives every authoring verb in dependency order
(author -> update/patch -> save-as-library -> reference/placeholder -> delete in
reverse), asserts each `Result.success`, and tears the scratch project down. A
verb is only promoted from `[~]` to `[x]` in [../PORTING.md](../PORTING.md) once
it passes here. Destructive to the current XAE solution state; run it knowingly.

```powershell
pwsh dotnet/oracle/smoke-writer.ps1
pwsh dotnet/oracle/smoke-writer.ps1 -Root C:\tmp\tckit-smoke -KeepScratch
```

All harnesses drive the C# verbs through the `TcKit.Cli` write/read verb
surface, so none has to script the MCP stdio handshake.

Progress is tracked in [../PORTING.md](../PORTING.md).
