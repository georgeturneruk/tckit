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

## Writer backend parity (live COM, ADR-0017)

With two writer backends, the automation lane doubles as an oracle for the xml
one. [`parity-writer.ps1`](parity-writer.ps1) scaffolds a scratch solution via
the automation backend, clones it, then runs each verb through both backends
(`--writer automation` against the clone XAE holds open, `--writer xml` +
`TCKIT_SOLUTION` against the other) and diffs the two trees after every verb.
The diff runs on canonicalised copies: BOM/EOL normalised, object `Id` GUIDs and
`LineIds` dropped, `.plcproj` `ProjectExtensions` dropped and ItemGroups sorted,
and only TwinCAT source files compared. First divergence stops the run (pass
`-Continue` to sweep everything); `-KeepScratch` keeps the trees for post-mortem.
This is the promotion gate for xml-backend verbs. Same warnings as the smoke:
needs a live 4026 with XAE attached, and clobbers the open solution.

```powershell
pwsh dotnet/oracle/parity-writer.ps1
pwsh dotnet/oracle/parity-writer.ps1 -Root C:\tmp\tckit-parity -KeepScratch -Continue
```

All harnesses drive the C# verbs through the `TcKit.Cli` write/read verb
surface, so none has to script the MCP stdio handshake.

Progress is tracked in [../PORTING.md](../PORTING.md).
