---
adr: 0014
title: Build diagnostics source and the TcXaeShell Express limitation
status: Accepted
created: 2026-06-14
last_reviewed: 2026-06-14
issue: 110
pr: 123
related: [10]
---

## Current state

**Decision (live):** `build` reads PLC compile diagnostics from the Visual
Studio DTE Error List (`dte.ToolWindows.ErrorList.ErrorItems` →
severity / code / description / file / line / project), the method Beckhoff
documents. This works against TwinCAT integrated into full Visual Studio.
**TcXaeShell Express exposes no EnvDTE tool-window automation at all** —
`ToolWindows.ErrorList`, `ToolWindows.OutputWindow`, and even `Windows.Count`
return null/empty — and there is no documented or empirical alternative, so on
Express `build` returns the correct pass/fail with an honest "per-error detail
not available on this edition" message and nothing finer.

**Where it lives:** `bridge/harness/Invoke-TcBuild.ps1` (Tier 1
`CheckAllObjects` for pass/fail, Tier 2 `Read-TcErrorList`, Express
short-circuit); `Read-TcErrorList` / `Read-TcBuildOutput` in
`bridge/harness/_TcDte.psm1`; `code`/`project` on `BuildError` and `infos` on
`BuildResult` in `tckit/ports/types.py`. Shipped in PR #123. Verified live this
session against TcXaeShell Express on `C:\tckitdemo\T3TckitUtils.sln`.

**Open questions:**
- Add a configurable DTE ProgID so the bridge can attach to
  `VisualStudio.DTE.<ver>` (full VS) instead of `TcXaeShell.DTE.<ver>`. That is
  the supported path to real build-error detail; not yet implemented.
- Whether decoding the binary `_CompileInfo/*.compileinfo` is worth a try as an
  Express-only fallback (undocumented; low confidence).
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
- Decode `_CompileInfo/*.compileinfo` — undocumented binary; not attempted.

## Consequences

- On full Visual Studio, `build` returns real file/line/code/project
  diagnostics (and licence faults) via the documented API — matching standard
  TwinCAT CI (AllTwinCAT / Jenkins).
- On TcXaeShell Express, `build` is honest pass/fail only; errors are read in
  the XAE GUI. This is an edition limitation, not a TcKit bug, and is now
  recorded so future sessions don't re-investigate the same dead ends.
- A VS-DTE attach can be added later with no rework — the harness and Error
  List code already behave correctly there.

## Status notes

- 2026-06-14: Drafted post-implementation. The ErrorList read + Express-honest
  fallback shipped in PR #123. This session verified live against TcXaeShell
  Express that the whole EnvDTE tool-window surface is null, cross-checked the
  Beckhoff infosys Error List page (242743179) confirming the documented method
  is exactly the ErrorList read, and ruled out the OutputWindow-panes and
  `GetLastErrorMessages` workarounds. VS-DTE attach recorded as the open
  follow-up.
