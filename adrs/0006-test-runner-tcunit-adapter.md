---
adr: 0006
title: TestRunner adapter for TcUnit
status: Implemented
created: 2026-05-12
last_reviewed: 2026-05-18
issue:
pr: 64, 65, 67, 68, 69
related: [0005, 0007, 0010, 0011]
---

## Current state

**Decision (live):** Three bridge routes (`POST /runtime`, `/tcunit-run`,
`/results`) plus a thin adapter that's a route caller. `run_tests` /
`get_results` take `target_ams_id` as the first positional arg (per
ADR-0005). The port is `run_tests` + `get_results` only; `wait_complete`
and `get_status` were dropped during implementation, and both now live
server-side inside `Invoke-TcUnitRun.ps1`. `TestResults` carries
`summary` + `asserts` + `failures` + `AssertFailure`. xUnit XML output is
gated on `GVL_Param_TcUnit.xUnitEnablePublish` (TcUnit's actual switch);
path is `GVL_Param_TcUnit.xUnitFilePath`, default
`%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml`. The fictional
`TcUnit_ResultExportXmlPath` convention is retired (ADR-0010 C.3). UmRT
path resolution and inline failures handled by ADR-0011.

**Where it lives:** `tckit/adapters/test_runners/tcunit_runner.py`,
`tckit/ports/test_runner.py`, `bridge/harness/{Invoke-TcRuntime,
Invoke-TcUnitRun, Get-TcUnitResults}.ps1`. Iteration discipline lives at the
`tc-build-test-loop` skill (5-iteration cap), not in the runner.

## Context

The port was stubbed and the bench (ADR-0007) needed real numbers. Without
an end-to-end runner, neither the closed-loop tckit arm nor the open-loop
vanilla arm could be graded.

## Decision

Implement bridge harness scripts (`Invoke-TcRuntime`, `Invoke-TcUnitRun`,
`Get-TcUnitResults`) against TcUnit's XML export, plus a thin adapter and a
test-project convention for the XML path.

## Alternatives considered

- Parse TcUnit console output: fragile across versions.
- Read live TcCOM symbols: build-config coupled, no failure-message text.
- msbuild outside the bridge: bypasses XAE.
- One mega-route doing build+activate+run+read: error paths can't be
  distinguished.

## Consequences

**Enables:** ADR-0007 closed-loop benching, `tc-build-test-loop` real
workflow.

**Costs:** ~30s wall per test cycle.

**Risks:** TcUnit's XML write isn't instantaneous; harness deletes the file
before invoking and waits for it to reappear.

## Status notes

- 2026-05-14: Implementation outcome (PRs #64, #65, #67, #68, #69). Bridge
  switched to Beckhoff's signed `TcXaeMgmt` PowerShell module
  (`Restart-TwinCAT`, `New-TcSession`, `Read-TcValue`) rather than loading
  `TwinCAT.Ads.dll` ourselves; `Invoke-TcRuntime.ps1` is a wrapper around
  `Restart-TwinCAT`. `tckit doctor` reads `/health` dependencies and prompts
  via a new `/install-dependency` route (allow-listed, `CurrentUser` scope).
  Lessons: TC3 has no purely-COM Config-mode API (use `Restart-TwinCAT`,
  not `ITcSysManager.SetConfigMode`); `TcXaeMgmt` 7.x has a `-ThrowError`
  cast bug (read `WriteControlInfo` directly).
- 2026-05-15: `TcUnit_ResultExportXmlPath` GVL convention retired (ADR-0010
  C.3). Bench's `add_library_placeholder` call passes
  `parameters={"xUnitEnablePublish": "TRUE"}` to flip the publisher on.
  TcUnit's xUnit publisher is gated on `GVL_Param_TcUnit.xUnitEnablePublish`
  (off by default) and writes to `GVL_Param_TcUnit.xUnitFilePath`. The
  "TcUnit-..." GVL-name pattern was TcKit-side convention only; nothing in
  TcUnit reads it.
- 2026-05-17: ADR-0011 layered UmRT auto-detect and inline failure
  reporting on top. The runner itself unchanged; the path-resolution helper
  and `run_tests` response shape did.
