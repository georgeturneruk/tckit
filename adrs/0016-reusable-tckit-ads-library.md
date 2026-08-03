---
adr: 0016
title: Reusable TcKit.Ads class library
status: Implemented
created: 2026-07-31
last_reviewed: 2026-08-03
issue:
pr: 134
related: [0006, 0011, 0015]
---

## Current state

**Decision (live):** Extract the generic ADS client layer into a standalone
class library `dotnet/src/TcKit.Ads` (net8.0, sole dependency
`Beckhoff.TwinCAT.Ads`), consumable by any .NET tool. `TcKit.Adapters.Ads`
becomes a thin shell over it that keeps the TcKit port contracts and Result
shaping. Public surface: symbol session with a stale-handle policy, typed
symbol read/write (owning the enum mapping), runtime state operations,
licence inventory reading (ADS port 30), a TwinCAT message-log stream reader
(logger port 100), and TcUnit results resolution + parsing. Distribution is a
local folder feed nupkg, SemVer from day one.

**Where it lives:** `dotnet/src/TcKit.Ads/` (`AdsSymbolSession` + channel
seam, `AdsRuntimeState`, `TcLicenses`, `TcLogStream` + `AdsLogEntry`,
`TcUnitResults`); adapter delegation in `dotnet/src/TcKit.Adapters.Ads/`
(`AdsNative.cs`, `AdsSymbolIo.cs`, `RuntimeOperations.cs`, `TcUnitMap.cs`);
packing via `dotnet/pack-tckit-ads.ps1` (default feed `C:\nuget-local`,
version from the csproj `<Version>`). Merged to main in PR #134.

**Settled during implementation:**

- Restart detection: every handle-lane operation reads the target's 1-byte
  symbol version (IG 0xF008) and drops the cache when it moved; a failed
  operation re-resolves its handle once and retries; still-failing surfaces
  as `TcSymbolException`. No notification subscription needed.
- Licence diagnosis keys on expiry, not the 0x254 trial status bit: real
  trials were observed reporting status 0 with an expiry set.
- Preflight wired into `StartRuntime`/`RunTests` failure paths only (fires
  when the final state is Config). `Deploy` reports success in this failure
  mode anyway, so it has nothing to append to.

**Open questions:**

- Whether the log stream deserves an MCP tool surface (library-only today).
- A `tckit doctor --target` verb composing state + licence + boot-project +
  route checks (TASKS task 6 "consider").

## Context

The ADS client mechanics (connections, variable handles, symbol I/O, runtime
state transitions, TcUnit XML handling) are embedded in
`TcKit.Adapters.Ads`, which is `internal`-heavy and referenced only by the
MCP server and CLI. Downstream .NET tools (test harnesses, diagnostics
dashboards, bench scripts) re-implement the same plumbing and re-learn the
same gotchas, two of which are demonstrably dangerous:

- **Stale variable handles.** Handles cached across polls silently break
  when the target runtime restarts, even though the ADS route stays
  connected; reads through a stale handle can return wrong data rather than
  an error (observed: a BOOL polled before a restart read `False` after it
  when its true value was almost certainly `True`, while fresh-path reads in
  the same call were sane).
- **Enum writes.** Writing an enum symbol requires a matching .NET enum
  type; Int16/string writes fail with an opaque marshalling error.

Separately, an expired runtime trial licence makes `StartRuntime`/`RunTests`
fail with only "final state 'Config'" (TASKS.md task 6); the licence
inventory is readable over ADS (licence server, AMS port 30), so the
diagnosis can be automated, but it has no home in the current adapter.

The strategic goal (TASKS.md): TcKit becomes the single engine a CI pipeline
calls, and other .NET consumers take a package dependency instead of copying
plumbing.

## Decision

New project `dotnet/src/TcKit.Ads`, namespace `TcKit.Ads`, targeting plain
`net8.0` (overriding the repo-wide `net8.0-windows`: nothing in the ADS
client lane is Windows-specific), depending only on `Beckhoff.TwinCAT.Ads`.
No reference to `TcKit.Core` — the library must be consumable without TcKit;
result-contract shaping stays in the adapter.

Public surface (one type per concern, mirroring what the adapter and the
donor consumer both need):

- `AdsSymbolSession` — long-lived connection to a PLC runtime port with a
  variable-handle cache and the stale-handle policy baked in: invalidate the
  cache when the connection is rebuilt or the target is observed to have
  restarted; on any read/write failure through a cached handle, re-resolve
  once and retry before surfacing a typed error. Reads never return
  silently-stale values: a handle that cannot be revalidated is an error,
  not a default. Typed read/write including the enum mapping (resolve the
  symbol's declared enum type and coerce Int/string values to it).
- `AdsRuntimeState` — liveness probe (TryReadState-style), state query, and
  restart-to-run with reconnect polling (what
  `RuntimeOperations.StartRuntime` does today).
- `TcLicenses` — licence inventory from the licence server (AMS port 30):
  entry list + name resolution, expiry/status decoding, and a preflight
  helper that answers "is there an expired trial that explains a target
  stuck in Config?" in one call.
- `TcLogStream` — subscribe to the TwinCAT message-log stream (logger
  service, AMS port 100, the LogView wire format) and yield structured
  entries; ring-buffered with rebuild-on-restart, donated from the
  downstream consumer and generalised.
- `TcUnitResults` — results-path resolution (per ADR-0011, target-aware per
  the UmRT TcRegistry.xml mapping) and xUnit XML parsing, moved from the
  adapter. The result records move with it (they lose the TcKit.Core
  dependency; the adapter maps them onto the TcKit contracts).

`TcKit.Adapters.Ads` keeps: the port implementations (`ITestRunner`,
`ISymbolIo`, `IRuntimeControl`, hardware inspectors), permission-gate
integration, `Result`/`TestRunResult` shaping, and the TwinSharp hardware
lane (TwinSharp stays an adapter dependency, not a library one). Its
orchestration (`RuntimeOperations`, `SymbolOperations`) migrates to library
calls; the fake-seam unit tests move with the logic they test.

Licence preflight (task 6): when `StartRuntime` or `RunTests` fails to reach
Run mode, the adapter calls the preflight and, if an expired trial licence is
found, appends the one-line actionable diagnosis (expiry date + pointer at
the XAE renewal dialog) to the error.

Distribution: `GeneratePackageOnBuild` producing `TcKit.Ads.<semver>.nupkg`,
published to a local folder feed (default `C:\nuget-local`) by a script under
`dotnet/`; consumers pin via `nuget.config` + `PackageReference`. Going
non-local later is pushing the same nupkg somewhere else.

Out of scope: anything domain-specific (application contracts, fleet models,
project-specific symbol paths), the COM/XAE lane, and MCP/CLI concerns.

## Alternatives considered

- Pack `TcKit.Adapters.Ads` itself — rejected: drags in TcKit.Core + TwinSharp.
- Separate repo — rejected for now: same-repo keeps refactor + library in one
  PR cycle; revisit if external consumers appear.
- Session-per-call (no handle cache) — rejected: measurable at polling rates;
  the point is to own the hard policy once.

## Consequences

- Downstream .NET tools get connections, typed symbol I/O, licence and log
  readers, and TcUnit parsing from one pinned package; the gotchas (stale
  handles, enum writes) are solved once.
- TcKit's own ADS behaviour becomes better-tested: the policy logic lands in
  a library with no COM or MCP entanglement.
- Cost: a public API surface to keep stable (SemVer discipline from day
  one), and one more project in the solution.
- The adapter keeps its seams and Result shaping, so MCP/CLI contracts do
  not change in this extraction; behaviour-visible changes ride separately.

## Status notes

- 2026-08-03: Implementation outcome. Drafted, implemented, and live-verified
  in one session (2026-07-31); merged in PR #134. Deviations that matter:
  the stale-handle policy settled as symbol-version check + retry-once (no
  notification subscription needed); the licence diagnosis keys on expiry,
  not the 0x254 trial bit (real trials report status 0 with expiry); the
  preflight wires into StartRuntime/RunTests only (Deploy reports success in
  that failure mode). All lanes verified live against a UmRT (state,
  licences, handle-lane symbol read, log subscription).
