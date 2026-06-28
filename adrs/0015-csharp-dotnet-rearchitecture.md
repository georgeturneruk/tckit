---
adr: 0015
title: C#/.NET rearchitecture (single in-process MCP server)
status: Accepted
created: 2026-06-28
last_reviewed: 2026-06-28
issue:
pr:
supersedes:
superseded_by:
related: [0006, 0009, 0011, 0014]
---

## Current state

**Decision (live):** Rearchitect TcKit as a single C#/.NET (net8) MCP server,
deleting both the Python package and the PowerShell COM bridge. ADS and
hardware go through `Beckhoff.TwinCAT.Ads` + TwinSharp; the Automation
Interface (project authoring) is hand-rolled against `TCatSysManagerLib` with
TcUnit-Runner as the reference for COM bring-up; MCP is the official .NET SDK
with stdio + SSE transports, and the project stays MIT. Sharp cutover on a branch, with the existing
Python stack kept as a parity oracle until every tool matches.

**Where it lives:** Committed; scaffold on `feat/csharp-rewrite`. Both off- and
on-machine feasibility now retired against a live 4026 (see
[finding](../bench/findings/2026-06-28-csharp-rewrite-feasibility.md)): net8 DTE
attach + tree read + self-cleaning `add_pou` authoring, the dependency stack, and
a real MCP stdio handshake all work. The per-tool port is the remaining work.

**Open questions:** Narrowed after the on-machine spike. Remaining:
- ADS symbol value read end-to-end (proven to link and route from net8; blocked
  only on a PLC runtime in Run on the target).
- SSE working for the separate-machines case.
- Typed `TCatSysManagerLib` interop (the spike used late-bound `dynamic`; the
  port moves to typed interop).

## Context

Today TcKit is a Python MCP server (ports + adapters, ~9.7k LOC across 45
files, ~57 tools) talking to a PowerShell HTTP bridge on `:8765` (~9k LOC
across 46 harness/module files), which in turn drives the COM Automation
Interface (`TcXaeShell.DTE.17.0`) and ADS (TcXaeMgmt + a P/Invoke shim).

The bridge exists for one reason: to span Python and the .NET/COM world.
TwinCAT's entire automation surface is .NET or COM. The Automation Interface
is COM (`TCatSysManagerLib` / EnvDTE); ADS ships as `Beckhoff.TwinCAT.Ads`;
the useful community tooling (TwinSharp, TcUnit-Runner, the Beckhoff AI
samples) is all .NET. Python is the only outsider, and the bridge is the tax.

The pain concentrates in that seam: the Windows PowerShell 5.1 vs 7 / net8
split, the HTTP serialisation hop, COM fragility expressed awkwardly in
PowerShell (stale `.~u` lock files, RPC-rejected retries), and the cost of
maintaining two languages for one product. Build 4026 is the 64-bit /
VS2022-generation shell (`TcXaeShell.DTE.17.0`), so a net8/x64 in-process
server now aligns cleanly with the platform, and the official .NET MCP SDK
provides stdio + HTTP/SSE transports, which makes a single-language server
viable for the first time.

## Decision

Rewrite TcKit as one C#/.NET server and retire the bridge and the Python
package entirely.

**Transport.** Official `ModelContextProtocol` SDK, both transports
first-class. stdio for the common case (Claude Code and XAE on the same
machine); SSE / streamable-HTTP to support running Claude Code and XAE on
separate machines, which is a required capability rather than a nice-to-have.
The SSE boundary replaces the bridge's HTTP boundary rather than adding a new
one; the network seam moves up a layer instead of disappearing, and that is a
better place for it.

**Dependency map (the core engineering content).** Per lane, depend vs lift
vs hand-roll:

| Lane | Strategy | Source |
|---|---|---|
| MCP framework + transport | depend | official `ModelContextProtocol` SDK |
| ADS read/write, RPC | depend | `Beckhoff.TwinCAT.Ads` 7.x (replaces the P/Invoke shim) |
| Hardware: EtherCAT / IPC / NC | depend / thin-wrap | TwinSharp (MIT) |
| COM attach + `IOleMessageFilter` + Error List read + TcUnit→xUnit | lift | TcUnit-Runner (MIT) |
| Config / CLI | depend | Tomlyn + System.CommandLine |
| Project authoring (POU/GVL/DUT/method/property + patch writes) | hand-roll | translate the existing harness against `TCatSysManagerLib`; no managed library exists |
| Build diagnostics (three-way) | hand-roll | keep ours; now native `System.Windows.Automation` |

The authoring lane is the only large piece with no library; it is a
translation of behaviour already banked in the harness and in ADRs 0009 /
0011 / 0014, not a rediscovery. Build diagnostics keep the three-way split
from ADR 0014 (DTE Error List on full shell, `devenv /log` XML headless, UI
Automation on Express); UI Automation is a first-class .NET API, so that path
gets cleaner in C#, not harder.

**Migration: sharp cutover on a branch, de-risked by a spike and an oracle.**

- *Phase 0 spike (go/no-go).* Solution + MCP SDK + SSE proven end to end; lift
  TcUnit-Runner's COM bring-up to attach `TcXaeShell.DTE.17.0`; two proof
  tools working against real 4026: `add_pou` (authoring, riskiest) and
  `read_symbols` (ADS, easiest). One week retires all three unknowns at once.
- *Golden-master oracle.* Keep the Python stack runnable and diff C# tool
  outputs against it on the same project, tool by tool. Do not delete Python
  until every tool passes the diff. This is available only because we are
  porting, not greenfielding, and it is worth more than any new unit test for
  catching translation drift.
- *Order.* Readers and ADS/hardware first (most dependency coverage), the COM
  authoring lane last (hardest, most behaviour to preserve).

**Licence.** Stays MIT, consistent with the existing project and with the MIT
dependencies it leans on (TwinSharp, TcUnit-Runner).

**What survives intact.** The 8 skills, 16 ADRs, and 21 docs pages are largely
language-agnostic. The port/adapter pattern maps to C# interfaces + DI. CI's
`check-adapter-isolation.py` is replaced by project-reference discipline.

## Alternatives considered

- **Status quo (Python + PowerShell bridge):** the bridge is the dominant
  fragility and blocks native use of the .NET ecosystem; every feature pays
  the cross-language tax.
- **Rewrite the bridge in C# but keep the Python MCP server (integration axis
  only):** kills the PowerShell fragility while preserving the MCP layer, but
  leaves a permanent two-process / two-language seam and never unlocks
  in-process COM + ADS. Reasonable interim; rejected in favour of going all
  the way, given how small the codebase is.
- **Big-bang rewrite with no spike or oracle:** too much hard-won COM
  behaviour to stake on one cutover; the spike + oracle make it safe at low
  cost.

## Consequences

- **Enables:** bridge deleted; net8 end to end; in-process typed COM + ADS
  (real exception types, step-through debugging); direct dependency on the
  .NET TwinCAT ecosystem; a more contributable codebase for a .NET audience;
  SSE for clean remote driving.
- **Costs:** a working, recently-extended system is rewritten and re-tested.
  Estimated ~7-10 weeks calendar to confident parity for a solo dev,
  AI-accelerated, with constant 4026 access (usable-but-incomplete branch in
  ~2-3 weeks; roughly double if part-time). The dominant cost and the main
  variance driver is hardware-gated validation of COM authoring edge cases
  (ADRs 0009 / 0011 / 0014), which neither libraries nor AI compress.
- **Constrains:** in-process COM means the server runs on Windows (mitigated
  by SSE: Claude Code can run anywhere and connect). TcAutomation stays
  reference-only (no licence) even in C#; TwinSharp (MIT) and TcUnit-Runner
  (MIT) are real dependencies / liftable.
- **Forward note:** pending TwinCAT platform shifts (e.g. PLC++) would change
  the build / read / write surface. Keeping authoring, build, and read/write
  behind clean adapter boundaries preserves the ability to adapt when they
  land. Not a blocker; flagged so the boundaries are drawn with it in mind.

## Status notes

- 2026-06-28: Drafted as `Proposed`. Direction crystallised from an ecosystem
  survey (`Beckhoff.TwinCAT.Ads`, TwinSharp, TcAutomation, TcOpen's CI
  pipelines, TcUnit-Runner). Phase 0 spike plus the Python parity oracle gate
  the commitment before any code lands.
- 2026-06-28: Off-machine feasibility spike (see
  [finding](../bench/findings/2026-06-28-csharp-rewrite-feasibility.md)). COM
  Automation Interface drives from C#/net8 with bridge-parity behaviour, and
  `Beckhoff.TwinCAT.Ads` 7.0.172 + `TwinSharp.Core` 1.2.0 +
  `ModelContextProtocol` 2.0.0-preview.1 build together on net8 with no
  conflict. Language/platform/dependency risk retired; DTE-attach `add_pou`,
  ADS `read_symbols`, typed interop, and SSE remain for the on-machine spike.
- 2026-06-28: Promoted to `Accepted`. The rewrite is committed; the on-machine
  Phase 0 spike is now the first execution step (confirmation of DTE-attach
  authoring, ADS reads, and SSE), not a go/no-go that could shelve the work.
- 2026-06-28: On-machine spike passed against a live 4026 (see finding). net8 DTE
  attach (via a `GetActiveObject` P/Invoke, since `Marshal.GetActiveObject` is
  gone from .NET Core/8), tree read, and self-cleaning `add_pou` authoring all
  work against the open solution; ADS links and routes from net8; the MCP server
  completes a real stdio handshake. The highest-risk lane (COM authoring) is
  proven; scaffold committed on `feat/csharp-rewrite`.
