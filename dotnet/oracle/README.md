# Parity oracle

The Python TcKit is the **behavioural reference** for the rewrite, not a
byte-for-byte golden master (ADR-0015). For every ported tool, run the same
operation through both implementations against the same fixture and compare the
*meaning* of the result. The C# port is free to make deliberate, reviewed
improvements to the surface; this harness surfaces semantic differences so
intended changes read as expected and genuine translation drift (a missing POU,
a mis-detected type, a bad task merge) stands out. The per-tool verification
**gate** is the C# xUnit suite; the oracle is a supplementary review aid.

Two comparison levels:

- **Tool output**, compare the JSON a tool returns (readers, docs, hardware
  reads), canonicalised: object keys sorted, array order preserved, GUIDs masked,
  path-valued fields lower-cased.
- **Project artefact**, for authoring tools, compare the resulting `.TcPOU` /
  `.plcproj` XML the operation writes into the project tree, normalised so
  volatile fields (GUIDs, timestamps) do not cause false diffs.

[`compare.ps1`](compare.ps1) implements the tool-output level for `get_structure`
(run both, canonicalise, report `PASS`/`REVIEW`); the flow generalises to later
readers. Drive it per fixture, e.g.:

```powershell
pwsh dotnet/oracle/compare.ps1 -Fixture C:\tckit\tests\fixtures\sample_project
pwsh dotnet/oracle/compare.ps1 -Fixture <multi-PLC .sln> -Plc <PlcName>
```

## Writer smoke (live COM)

The authoring lane cannot be cross-checked against a fixture on disk: the verbs
mutate the solution open in TcXaeShell over COM. [`smoke-writer.ps1`](smoke-writer.ps1)
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

Both harnesses drive the C# verbs through the `TcKit.Cli` write/read verb
surface, so neither has to script the MCP stdio handshake.

Progress is tracked in [../PORTING.md](../PORTING.md).
