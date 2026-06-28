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

## On-machine spike (live 4026, same day)

Run against a live XAE (TcXaeShell 17.0, solution `C:\tckitdemo\T3TckitUtils.sln`)
with the bridge healthy on `:8765`. All from a net8 build:

- **DTE attach.** `Marshal.GetActiveObject` was removed from .NET Core/8, so net8
  attaches via a P/Invoke of `GetActiveObject` (oleaut32) + `CLSIDFromProgID`
  (ole32). Attached `TcXaeShell.DTE.17.0`, read the open solution and both
  TwinCAT projects. **Concrete requirement for the COM adapter.**
- **Tree read.** Walked the doubled-name path (`TIPC^<plc>^<plc> Project^POUs`)
  and listed the real POU tree.
- **`add_pou` authoring.** `pous.CreateChild("FB_CsSpike", 604, null, null)`
  authored the POU (verified PathName), then `DeleteChild` removed it; the
  solution was not saved, so disk was untouched. The hard, no-library lane works
  from net8 against the live project.
- **ADS.** The `Beckhoff.TwinCAT.Ads` client links and reaches the AMS router
  from net8 (proper ADS-level errors returned through a working client). A symbol
  value read was not possible because no PLC runtime is currently in Run on the
  target (`192.168.0.142.1.1:851` -> AdsErrorCode 6, port not found), the same
  precondition the bridge's reader requires. Not a feasibility gap.
- **MCP server.** Completes a real stdio `initialize` handshake (`serverInfo:
  TcKit.Server`, tools capability). Found and fixed a bug: the host logger wrote
  to stdout, which corrupts the JSON-RPC channel; logs now go to stderr.

## Remaining for full coverage

- ADS symbol value read end-to-end, blocked only on a runtime in Run (deploy + start).
- SSE transport, separate-machines.
- Typed `TCatSysManagerLib` interop (the spike used late-bound `dynamic`).

## Verdict

Feasibility is settled, including the hardest lane. COM authoring (DTE attach +
`add_pou`) is proven from net8 against a live 4026; ADS links and routes from
net8; the dependency stack builds; the MCP server completes a real stdio
handshake. The COM adapter must use the `GetActiveObject` P/Invoke on net8. What
remains is ordinary integration (a running runtime for ADS values, SSE wiring)
and the per-tool port, not open technical risk.
