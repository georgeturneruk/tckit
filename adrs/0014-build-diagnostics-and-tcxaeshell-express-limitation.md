---
adr: 0014
title: Build diagnostics source and the TcXaeShell Express limitation
status: Accepted
created: 2026-06-14
last_reviewed: 2026-07-31
issue: 110
pr: 123
related: [10]
---

## Current state

**Decision (live):** `build` reads PLC compile diagnostics from the Visual
Studio Error List. On full Visual Studio that is the documented EnvDTE path
(`dte.ToolWindows.ErrorList.ErrorItems` → severity / code / description /
file / line / project). **TcXaeShell Express exposes no EnvDTE tool-window
automation** (`ToolWindows.ErrorList`, `OutputWindow`, `Windows.Count` all
null), but the rendered Error List is still a live WPF grid, so on Express
`build` reads it via **UI Automation** (`Read-TcErrorListUia`): it activates the
Error List tab, walks the "Results" ListView's GridPattern, and returns real
file / line / code / description / project per row. The honest "open it in XAE"
message now fires only when the GUI can't be reached (no window on the
interactive desktop, or the compile failed yet no error rows could be read).

**Express severity is inferred, not read.** Every row's severity-column image
reports the same static `Name='Error'` to UI Automation, so true severity is
unavailable. It is inferred from the reliable columns instead: a row with a
compiler code (e.g. `C0046`) is a compile diagnostic — an error when
`CheckAllObjects` failed, a warning when it passed — and every code-less row
(TwinCAT deploy / licence / test-log messages) is an info. This keeps
`errors`/`warnings` limited to real compile diagnostics so the build's pass/fail
stays honest, while TwinCAT messages are still surfaced as infos. Tier 2 only
runs on failure or `-ForceLog`, so a clean build never reads the noisy list.

**Where it lives (C# rewrite):** `ProjectBuilder.Build` (Tier 1
`CheckAllObjects` for pass/fail; Tier 2 tries `ITcSession.ReadErrorList`
(EnvDTE), then `ReadErrorListUia`) and `ErrorListUia.cs`, both in
`dotnet/src/TcKit.Adapters.Automation/`. The UIA read was LOST in the ADR-0015
rewrite (it lived in the deleted PowerShell bridge) and restored 2026-07-31
with two hardenings learned live: (1) the Error List's own severity toggles
filter runtime message rows before the read — ADSLOGSTR output floods the
list with thousands of rows on a logging target, burying the compile
diagnostics beyond any row cap; (2) rows are read by walking the realised
ListItems page by page (their descendant Text elements arrive in column
order), NOT via `GridPattern.GetItem`, which hands back recycled containers
with stale text after a refilter. Verified live against TcXaeShell on the
GuardCheck scratch solution: a deliberate undefined symbol returned `C0046` +
`C0032` with file/line/project; a clean build passed with no errors.

**Open questions:**
- A configurable DTE ProgID to attach to `VisualStudio.DTE.<ver>` (full VS) is
  still a nice-to-have, but no longer the *only* route to per-error detail on
  Express now that the UIA read works.
- The UIA read needs the XAE GUI open on the interactive desktop (same
  precondition as attach-mode COM); it can't help a truly headless / SYSTEM
  build. The severity icon stays unreadable on Express, so error-vs-warning is
  inferred from `CheckAllObjects` rather than read.
- On full VS the Error List also carries licence faults ("No license found"),
  which would feed issue #112's activate/deploy hints for free.

## Context

Issue #110: `build` returned no usable diagnostics. The original Tier 2 parsed
the TcXaeShell `/rebuild /log` *activity* log (IDE startup events such as
`StubSyncLock`), never the PLC compiler output.

The Beckhoff-documented method is the Visual Studio Error List — infosys
[242743179](https://infosys.beckhoff.com/content/1033/tc3_automationinterface/242743179.html),
"Accessing the Error List window of Visual Studio": `dte.ToolWindows.ErrorList.ErrorItems`
with `Description` / `FileName` / `Line` / `Column` / `Project`, explicitly
covering compile errors *and* licence problems. (Beckhoff's own sample loops
`for i = 1; i < Count` — an off-by-one that drops the last item; our harness
uses `i <= count`.)

Live findings on TcXaeShell Express (DTE `Edition` reports `Express`), attached
to the running GUI instance:

- `ToolWindows.ErrorList` = null, even after `ExecuteCommand("View.ErrorList")`
  and a compile.
- `ToolWindows.OutputWindow` = null, even after `ExecuteCommand("View.Output")`.
- `dte.Windows.Count` = empty; `Windows.Item(<ErrorList GUID>).Object` = null.
- `devenv/TcXaeShell /rebuild /out <file>` writes no file; `/log` carries only
  IDE noise, no PLC error text.
- `ITcSysManager.GetLastErrorMessages()` exists (returns a `string`) but is
  empty after a failed PLC compile *and* after an unreachable-target
  `ActivateConfiguration`. The native `ITcSysLog` / `new TcSystemManager()`
  surface suggested by an LLM does not exist for TC3 (no such coclass
  registered).
- The PLC project tree item exposes only generic `ConsumeXml` / `GetLastXmlError`,
  no compile-message accessor.

The probes ran with a live desktop GUI present, so the "headless / SYSTEM, no
desktop interaction" explanation does not apply — the null surface is the
Visual Studio Isolated Shell (Express) restricting tool-window automation.

## Decision

Two-tier build in `Invoke-TcBuild.ps1`:

1. **Tier 1:** `ITcPlcIECProject2.CheckAllObjects()` → bool pass/fail. Fast,
   works on every edition.
2. **Tier 2** (on failure, or `ForceLog`): `Read-TcErrorList` reads the DTE
   Error List into structured rows. If available (full VS), return real
   errors / warnings / infos. If `Edition == 'Express'`, short-circuit with an
   honest message (skip the slow, pointless `/out` rebuild). Otherwise
   (non-Express but no Error List) fall back to a `devenv /out` build-output
   parse.

The supported route to full diagnostics is TwinCAT integrated into full Visual
Studio; attaching the bridge to a VS DTE is the open follow-up. `BuildError`
gains optional `code` / `project`; `BuildResult` gains `infos`; all defaulted so
existing callers and the fallback path stay compatible.

## Alternatives considered

- Parse the `/log` activity log — wrong source (IDE events); produced only
  `StubSyncLock` noise. Removed.
- `devenv /out` build-output parse — works only where `/out` is honoured; on
  TcXaeShell it writes nothing. Kept as a non-Express fallback only.
- Read the "Build" / "TwinCAT" `OutputWindowPanes` (community workaround) —
  relies on `ToolWindows.OutputWindow`, null on Express; not Beckhoff-documented.
- `ITcSysManager.GetLastErrorMessages()` / native `ITcSysLog` — coclass absent
  for TC3; the method is empty for PLC compile errors.
- `ITcPlcProject.GenerateBootProject()` + check for a missing `.app` — only a
  pass/fail signal, the same as Tier 1 `CheckAllObjects`; no per-error detail.
- Decode `_CompileInfo/*.compileinfo` — probed this session: it's the CODESYS
  post-compile symbol/codegen graph for a *successful* build (CRCs, external
  names, FB_INIT / __MAIN link records), not a diagnostics log. Dead end.
- Build via MSBuild + Beckhoff PLC targets — no `Beckhoff.TwinCAT.Build.targets`
  exists; the `.plcproj` imports none and the `.tsproj` is the `<TcSmProject>`
  schema, not MSBuild, so `msbuild <sln>` compiles no PLC and emits no errors.
  Consistent with TwinCAT CI driving the automation interface, not MSBuild.
- Read the Error List **UI Automation grid** (chosen for Express) — the
  rendered WPF grid is readable even though EnvDTE is null; the only data it
  won't surface is per-row severity (static icon name), which we infer.

## Consequences

- On full Visual Studio, `build` returns real file/line/code/project
  diagnostics (and licence faults) via the documented API — matching standard
  TwinCAT CI (AllTwinCAT / Jenkins).
- On TcXaeShell Express, `build` now returns real file/line/code/project
  diagnostics too, read from the GUI Error List via UI Automation, whenever the
  XAE solution is open on the interactive desktop. Severity is inferred
  (compiler code + `CheckAllObjects`) since the Express Error List doesn't
  expose per-row severity to UIA. When the GUI can't be reached it falls back to
  the honest pass/fail message. The earlier "edition limitation" is lifted for
  the common case of an operator working with XAE open.
- A VS-DTE attach can still be added later with no rework — the harness and
  Error List code already behave correctly there.

## Status notes

- 2026-06-14: Drafted post-implementation. The ErrorList read + Express-honest
  fallback shipped in PR #123. This session verified live against TcXaeShell
  Express that the whole EnvDTE tool-window surface is null, cross-checked the
  Beckhoff infosys Error List page (242743179) confirming the documented method
  is exactly the ErrorList read, and ruled out the OutputWindow-panes and
  `GetLastErrorMessages` workarounds. VS-DTE attach recorded as the open
  follow-up.
- 2026-06-14: Express now reads real diagnostics via UI Automation. Probed the
  two leftover leads and recorded both as dead ends in Alternatives:
  `_CompileInfo/*.compileinfo` is a successful-compile symbol graph (no
  diagnostics), and MSBuild has no Beckhoff PLC build targets installed.
  Discovered the rendered Error List is a live WPF grid UIA can read on Express
  even though EnvDTE is null: `Read-TcErrorListUia` selects the Error List tab,
  walks the "Results" ListView (GridPattern/TablePattern), realises each
  virtualised row via ScrollItemPattern and reads the columns. Severity is *not*
  readable — every row's severity-column image reports a static `Name='Error'`
  to UIA, and `LegacyIAccessible` isn't reachable — so it is inferred from the
  compiler code plus the `CheckAllObjects` result; that classification lives in
  the unit-tested pure helper `ConvertTo-TcErrorRow`. Wired into the Express
  branch of `Invoke-TcBuild.ps1`. Verified live on
  `C:\tckitdemo\T3TckitUtils.sln`: an undefined-symbol POU returned `C0018` +
  `C0046` with file + line + project; a clean build was a fast pass with no
  errors and no list read (Tier 2 runs only on failure / `-ForceLog`).
- 2026-07-31: The UIA read had been lost in the ADR-0015 C# rewrite (it lived
  in the deleted PowerShell bridge), silently degrading Express builds back to
  the honest-message fallback. Ported to `ErrorListUia.cs` with two hardenings
  found live on a target that had been logging for hours: the severity toggle
  buttons ("N Errors / N Warnings / N Messages") filter runtime message rows
  before the read (thousands of ADSLOGSTR rows otherwise bury the diagnostics
  past any row cap), and the rows are read from the realised ListItems
  (descendant Text elements in column order) rather than GridPattern.GetItem,
  which returns recycled containers with stale text after a refilter. Current
  state updated in the same edit.
