---
adr: 0015
title: C#/.NET rearchitecture (single in-process MCP server)
status: Accepted
created: 2026-06-28
last_reviewed: 2026-07-02
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
Interface (project authoring) is hand-rolled against `TCatSysManagerLib`;
MCP is the official .NET SDK, and the project stays MIT.

**Where it lives:** The port is complete (every lane in
[dotnet/PORTING.md](../dotnet/PORTING.md) checked off) and the **cutover has
been executed** on `feat/csharp-rewrite`: the Python package, PowerShell
bridge, and Docker mode are deleted, and the parity oracle retired with them
(the live smoke harnesses remain in `dotnet/oracle/`). CI is
`.github/workflows/dotnet-ci.yml` (Linux build + xUnit + skills drift check);
the site pipeline (`scripts/build-docs.sh`) runs the C# doc generator; the
plugin builds and launches the C# server; README and docs describe the C#
surface. `tests/fixtures/` and `bench/fixtures/` survive the deletion because
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
- 2026-06-28: Dropped byte-for-byte parity as a goal. The rewrite may make
  deliberate, reviewed breaking improvements to the MCP surface; Python is a
  behavioural reference and the oracle a semantic cross-check (not a strict diff),
  with the xUnit suite as the per-tool gate. First reader `get_structure` ported
  on `feat/csharp-readers-get-structure` (offline XML in `TcKit.Adapters.Reader`,
  shared snake_case JSON contract, unified `{ "error": msg }` failure shape);
  oracle green on the sample, multi-PLC, and nested-folder bench fixtures.
- 2026-06-28: Reader lane completed on the same branch: `get_pou_interface`,
  `get_pou_declaration`, `get_pou_item` (methods/actions/property `.Get`/`.Set`),
  `get_gvl`, `get_dut` (kind + alias base_type). They share a stateful symbol index
  built by `get_structure` (per-PLC name -> path, .plcproj mtime staleness, ADR-0005);
  index hydration from the open-XAE solution is deferred to the COM lane. Exercising
  the breaking-changes latitude, the MCP surface now follows C# identifier
  conventions instead of the snake_case ecosystem default: PascalCase tool names
  (`GetStructure`, `GetPouInterface`, ...) and camelCase parameters; output JSON
  keys stay snake_case as the data contract. 20 xUnit tests pass; oracle green
  across all readers on the sample, multi-PLC, and T3 fixtures.
- 2026-06-28: Writer lane started behind an automation seam
  (`ITcSession`/`ITcSysManager`/`ITcTreeItem`): `ProjectAuthor` holds the COM-free
  authoring logic, `ComTc*` the live late-bound implementation, and an in-memory
  fake encodes AI behaviour for CI. Create family done (OpenProject, AddPou,
  AddFolder, AddGvl, AddDut, AddMethod, AddProperty), logic CI-tested against the
  fake (46 tests total). Resolves the typed-interop open question (dynamic + seam,
  not typed); the live COM wrapper still needs an on-XAE smoke against a
  throwaway/demo solution. update / delete / library verbs next.
- 2026-06-28: update + delete verbs added on the seam: update_pou_declaration /
  _implementation / _method_body and the three anchored _patch variants; delete_pou
  (with a file-side .TcTTO task-binding refusal for PROGRAMs), delete_method,
  delete_property (accessor cascade), delete_gvl, delete_dut, delete_folder
  (recursive + kind validation). 61 tests pass against the fake. Remaining writer
  verbs: add_variable / delete_variable, library refs/placeholders, add_plc_project,
  save_plc_as_library, create_project; then the live COM smoke.
- 2026-06-28: Live COM smoke passed against a real 4026 (throwaway temp solution):
  OpenProject -> AddPou (authored FB_TcKitSmoke.TcPOU to disk) -> DeletePou (removed
  it), both success. Two fixes the smoke surfaced and that are now in: an
  `IOleMessageFilter` registered on the STA thread (resolves RPC_E_CALL_REJECTED
  when XAE is busy, the canonical VS-automation fix) and capturing tree-item
  path/kind before navigating (TwinCAT AI invalidates a handle once you navigate
  away, which had made DeletePou report a spurious "invalidated" error). 61 fake
  tests green; the ComTc* layer is now live-proven.
- 2026-07-01: Config/CLI lane scoped down and the safety stance ported. Decided not to
  port the `init` / `config` / `doctor` subcommands or the layered TOML+JSON loader (mostly
  bridge-era knobs the rewrite deletes; remaining runtime defaults read from the environment).
  Ported the safety stance as a hot-reloaded permission gate instead: `IPermissionGate` →
  `FilePermissionGate` over `~/.tckit/permissions.json`, with a read/write/execute mode
  (every mutating tool declares its level) and allowed/blocked target NetIds gating execute-class
  calls (Deploy, StartRuntime, RunTests, WriteSymbols, InvokeRpc). Block is a hard guard (never
  lifted by a tool); `GetPermissions` / `SetPermissions` make the soft facets easy to swap
  mid-session. Missing file = permissive (opt-in), malformed = keep last good, typo'd mode = fall
  to read. 18 gate tests added; full suite 266 green. Remaining: a docs page (docs/content +
  tc-config skill still describe the Python config).
- 2026-07-02: Cutover executed. All lanes in PORTING.md complete (including the net-new
  hardware diagnostics, symbol I/O, I/O authoring, and `FindHardware` lanes); README, docs
  site, skills, and plugin rewritten for the C# surface (plugin now builds and launches
  `TcKit.Server` from its clone). Deleted: `tckit/`, `bridge/`, `docker/`, the Python
  tests (fixtures kept for xUnit), `pyproject.toml`, the Python CI + PyPI release
  workflows, and the parity-oracle `compare.ps1`. `scripts/build-docs.sh` now bootstraps
  the .NET 8 SDK and runs the C# doc generator. SSE explicitly left open without gating
  the cutover. Promote to Implemented (and fill `pr:`) when the branch merges to main.
