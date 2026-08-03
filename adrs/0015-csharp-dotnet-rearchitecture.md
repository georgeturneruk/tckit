---
adr: 0015
title: C#/.NET rearchitecture (single in-process MCP server)
status: Implemented
created: 2026-06-28
last_reviewed: 2026-08-03
issue:
pr: 132
supersedes:
superseded_by:
related: [0006, 0009, 0011, 0014]
---

## Current state

**Decision (live):** Rearchitect TcKit as a single C#/.NET (net8) MCP server,
deleting both the Python package and the PowerShell COM bridge. ADS and
hardware go through `Beckhoff.TwinCAT.Ads` + TwinSharp; the Automation
Interface (project authoring) is hand-rolled against `TCatSysManagerLib`;
MCP is the official .NET SDK, and the project stays MIT.

**Where it lives:** The port is complete (every lane in
[dotnet/PORTING.md](../dotnet/PORTING.md) checked off) and the cutover
**merged to main in PR #132** (distribution follow-up in #133): the Python
package, PowerShell bridge, and Docker mode are deleted, and the parity
oracle retired with them
(the live smoke harnesses remain in `dotnet/oracle/`). CI is
`.github/workflows/dotnet-ci.yml` (Linux build + xUnit + skills drift check);
the site pipeline (`scripts/build-docs.sh`) runs the C# doc generator; the
plugin launcher runs a self-contained `tckit-server-win-x64.exe` (published to
a `v*` GitHub Release by `.github/workflows/release.yml` on a Windows runner)
or builds from source when the .NET 8 SDK is present, so end users need no
SDK; README and docs describe the C# surface. `tests/fixtures/` and `bench/fixtures/` survive the deletion because
the xUnit suite reads them.

**Safety stance:** the Python config CLI was not ported; the permission gate
(`~/.tckit/permissions.json`: read/write/execute mode + NetId allow/block,
block unbypassable, hot-reloaded, `GetPermissions`/`SetPermissions` tools)
replaces it. `Deploy`/`StartRuntime`/`RunTests` are gated by mode + NetId;
`WriteSymbols`/`InvokeRpc`/`DeleteIoDevice` additionally require
`confirmed=true`.

**Parity stance:** byte-for-byte parity was not a goal. The shipped surface
makes reviewed improvements: PascalCase tool names / camelCase parameters
(snake_case output JSON), a unified `{ "error": msg }` failure shape, and
net-new lanes (hardware diagnostics, symbol I/O, I/O authoring,
`FindHardware`). The verification gate is the xUnit suite.

**Open questions:**
- SSE / streamable-HTTP for the separate-machines case. Deliberately left
  open and **not gating the cutover**; stdio is the shipped transport.
- Formal live cross-check of the IPC hardware readings (CPU frequency units,
  router-memory mapping). The ADS lanes have had informal live testing on the
  bench; not CI-gated.

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
- *Parity oracle (behavioural, not byte-for-byte).* Keep the Python stack
  runnable and cross-check C# tool outputs against it on the same project, tool
  by tool. The rewrite is free to make deliberate, reviewed improvements to the
  surface where they make sense; the oracle surfaces *semantic* differences so
  intended changes read as expected and genuine translation drift (a missing
  POU, a mis-detected type) stands out. The per-tool verification gate is the C#
  xUnit suite; the oracle is a supplementary review aid, available only because
  we are porting, not greenfielding. Do not delete Python until every tool has
  reached behavioural parity (or a documented, intended divergence).
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

- 2026-08-03: Implementation outcome (compacted; the dated walk-through lives
  in git history). Drafted, spiked (off-machine + on-machine against a live
  4026), and Accepted on 2026-06-28; ported lane by lane with the Python
  parity oracle as a semantic cross-check; cutover PR #132 merged 2026-07-03,
  deleting the Python package, PowerShell bridge, Docker mode, and PyPI
  pipeline. Distribution landed in #133: a `v*` tag publishes a
  self-contained `tckit-server-win-x64.exe` (+SHA256) and the plugin launcher
  resolves override -> cache -> build-from-source -> checksum-verified
  download. Deviations that matter: byte-for-byte parity dropped in favour of
  reviewed improvements (PascalCase tools / camelCase params, unified error
  shape, net-new hardware/symbol/I-O lanes); the Python config CLI was not
  ported — the hot-reloaded `permissions.json` gate replaces it; SSE left
  open, non-gating, stdio is the shipped transport.
