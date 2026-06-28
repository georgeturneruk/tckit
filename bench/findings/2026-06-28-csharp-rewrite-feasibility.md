---
date: 2026-06-28
status: Current
related_adrs: [15]
---

# C#/.NET rewrite feasibility spike

Off-machine feasibility probe for the
[ADR-0015](../../adrs/0015-csharp-dotnet-rearchitecture.md) rewrite. Goal:
retire the language / platform / dependency unknowns before committing to the
on-machine Phase 0 spike. Run on the dev box with no running XAE and no open
project, so it covers the COM *mechanism* and the dependency assembly, not live
authoring.

## What was tested

| Check | Result |
|---|---|
| COM Automation Interface from C# (Framework 4.8 `csc`, x64, late-bound `dynamic`) | Pass |
| .NET 8 SDK user-local install (no admin) | Pass: 8.0.422 at `C:\dn8` |
| Dependency stack restores + builds on net8 | Pass, no version conflict |
| COM Automation Interface from C# (net8 SDK, x64) | Pass |
| Behavioural parity with the PowerShell bridge | Pass (same RM boundary) |

Both COM probes reproduce the PowerShell `TcSysManagerRM` result exactly:
instantiate -> `CreateSysManager15` -> `NewConfiguration` -> `LookupTreeItem`
-> `CreateChild` a task under `TIRT`. PLC-project `CreateChild` fails headless
with the same `Cannot create new PLC project` COMException, the
RM-cannot-author-PLC boundary, unchanged from the earlier PowerShell probe.

## Dependency versions (resolved together, net8)

- `Beckhoff.TwinCAT.Ads` 7.0.172
- `TwinSharp.Core` 1.2.0
- `ModelContextProtocol` 2.0.0-preview.1

No `NU1605` / downgrade. The "TwinSharp pins `Beckhoff.TwinCAT.Ads` 6.1.290"
concern did not bite: NuGet unifies to 7.x and the project builds.

## Practical setup notes (for the on-machine spike)

- The box had the net8 **runtime** but no **SDK**: `dotnet --version` /
  `--list-sdks` were empty while `--list-runtimes` showed 8.0.26. Installing the
  SDK is a prerequisite; `dotnet-install.ps1` does it user-local, no admin.
- Install to a **short path**. Extraction into the deep scratchpad path failed
  on the Windows 260-char limit (the SDK ships deeply-nested `BuildHost-net472`
  files); `C:\dn8` worked.
- COM probes need `[STAThread]` and x64 (the AI COM server is registered in the
  64-bit hive); late-bound `dynamic` needs no interop assembly.

## Not covered here (needs a live 4026 + open project)

- DTE attach (`TcXaeShell.DTE.17.0`) + a real `add_pou`. The COM mechanism is
  proven via the headless RM path; the authoring lane uses DTE (same IDispatch
  plumbing) but is unproven end-to-end.
- `read_symbols` via `Beckhoff.TwinCAT.Ads` / TwinSharp against a running
  runtime.
- Typed `TCatSysManagerLib` interop (production form; the probe used `dynamic`).
- SSE transport, separate-machines.

## Verdict

The language / platform / dependency risk for ADR-0015 is retired: COM drives
from C#/net8 with bridge-parity behaviour, and the three core dependencies
coexist on net8. What remains is integration against live hardware, not
feasibility. Confidence is high; the dominant remaining variable is constant
access to a 4026 system for per-tool validation, not any open technical
question.
