---
adr: 0006
title: TestRunner adapter for TcUnit
status: Implemented
created: 2026-05-12
issue:
pr: 64, 65, 67, 68, 69
---

## Context

`tckit/ports/test_runner.py` defines a `TestRunner` port with `run_tests`,
`wait_complete`, `get_results`, `get_status`. The MCP tools `run_tests`
and `get_test_results` are wired through `tckit/server.py` and the config
registry knows about `TcUnitRunner`. The adapter at
`tckit/adapters/test_runners/tcunit_runner.py` and the bridge harness
scripts `Get-TcUnitResults.ps1` and `Invoke-TcRuntime.ps1` are stubs
returning `NotImplementedError` or `success: false`.

The bug-hunting bench (ADR-0007) is the immediate driver: closed-loop
testing requires the model to run tests, read results, patch, and re-run.
Open-loop validation for the vanilla arm also needs the harness to run
tests post-session and write `.test-result.json`. Without this work,
neither config can be graded. The existing `tc-build-test-loop` skill
also dead-ends at `run_tests`.

## Decision

Implement the runner end-to-end against TcUnit's XML export. Three pieces:
bridge harness, adapter, and a test-project convention for the XML output
path.

### Bridge harness scripts

- **`bridge/harness/Invoke-TcRuntime.ps1`**: build the active
  configuration, activate it on the target, switch the runtime to Run
  mode. Caller specifies `-Mode 'Run' | 'Config'`, `-PlcName` (per
  ADR-0005), and `-Wait`.
- **`bridge/harness/Invoke-TcUnitRun.ps1`**: run a test cycle to
  completion. Ensures target is in Run mode, polls the test-runner's
  finished flag via the COM symbol API until true or timeout, returns
  `{success, duration_seconds, summary}`.
- **`bridge/harness/Get-TcUnitResults.ps1`**: read the publisher's XML
  output, parse with `[System.Xml.XmlDocument]`, return a structured
  JSON shape matching `TestResults` in `tckit/ports/types.py`.

Routes added in `Start-Bridge.ps1`: `POST /runtime`, `POST /tcunit-run`,
`POST /results`.

### Adapter

`tckit/adapters/test_runners/tcunit_runner.py` becomes a thin route
caller, same shape as `automation_writer.py`. `run_tests(target_ams_id,
*, plc_name=None)` and `get_results(target_ams_id, *, plc_name=None)`
post to `/tcunit-run` and `/results` respectively, sharing the
`_with_target_and_plc` payload helper. `target_ams_id` is required and
positional; implicit "last deployed target" state would be brittle across
MCP calls. The exact surface lives in the code; the pseudocode in earlier
drafts of this ADR did not survive Phase 0 (see Status notes 2026-05-13).

### TestResults shape

Defined in `tckit/ports/types.py`. The XML maps one-to-one onto
`TestSuite` / `TestCase` / `AssertFailure`; no inference.

### XML output path

An earlier draft pinned the XML path via a `TcUnit_ResultExportXmlPath`
GVL constant declared by the consumer project. That constant proved
fictional (TcUnit never read it) and was retired across template /
fixtures / skill in PR #90. The runner now reads
`GVL_Param_TcUnit.xUnitFilePath` (defaulting to
`%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml`) and gates the publisher on
`GVL_Param_TcUnit.xUnitEnablePublish`. See Status notes 2026-05-15 and
ADR-0010 section C.3.

### Iteration discipline

The `tc-build-test-loop` skill documents a 5-iteration cap. The runner
itself does not enforce this; the cap lives at the skill level where the
model decides whether to re-run.

## Alternatives considered

- **Parse TcUnit console output.** Console output is fragile across
  TwinCAT and TcUnit versions; XML is the documented interface.
- **Read live TcCOM symbols for assertion details.** Tied to a specific
  build configuration, no failure-message text.
- **Run tests via msbuild outside the bridge.** Bypasses XAE; the bridge
  owns the runtime control surface and is the One Rule's enforcement
  point for runtime mutations.
- **One bridge route doing build + activate + run + read.** Build,
  activation, and test failures all need distinct error paths and the
  model needs to call each step independently.

## Consequences

**Enables:** closed-loop benching (ADR-0007), the `tc-build-test-loop`
skill's documented workflow, any operator wanting to run TcUnit suites
from Claude.

**Costs:** ~30 seconds of wall-clock per test cycle on a moderately-sized
solution.

**Risks:** TcUnit's XML write is not instantaneous; the harness must wait
for the file's mtime to update past the run start time before reading.
Mitigation: delete the XML file before invoking, then wait for it to
reappear.

## Status notes

- 2026-05-12: Drafted as `Proposed`. Depends on ADR-0005 (multi-project)
  for `plc_name`.
- 2026-05-12: Interface tightened during ADR-0005 implementation;
  `run_tests`, `wait_complete`, `get_results` now take a required
  `target_ams_id` first positional arg, matching `BuildRunner.deploy`.
- 2026-05-13: Port narrowed and types widened. Dropped `wait_complete`
  and `get_status` (both already lived server-side inside
  `Invoke-TcUnitRun.ps1`). Widened `TestResults` to carry `summary`,
  `asserts`, `failures`, `AssertFailure`. Extracted `_to_result` from
  `automation_writer.py` to `tckit/utils/results.py`. PRs #64, #65.
- 2026-05-14: Switched bridge to depend on Beckhoff's signed `TcXaeMgmt`
  PowerShell module (`Restart-TwinCAT`, `New-TcSession`, `Read-TcValue`)
  rather than loading `TwinCAT.Ads.dll` ourselves. `Invoke-TcRuntime.ps1`
  shrinks to a wrapper around `Restart-TwinCAT`. `tckit doctor` reads
  `/health` dependencies and prompts via a new `/install-dependency`
  route (allow-listed, `CurrentUser` scope). PRs #67, #68, #69.
  - **Lesson:** TC3 has no purely-COM Config-mode API. The original
    draft called `ITcSysManager.SetConfigMode()` which doesn't exist;
    an ADS-based fix initially used `AdsState.Config` (15, steady-state)
    instead of `AdsState.Reconfig` (16, the transition command). Use
    `TcXaeMgmt`'s `Restart-TwinCAT` rather than reinventing the
    WriteControl wire.
  - **Lesson:** `TcXaeMgmt` 7.x has a `-ThrowError` cast bug; read the
    `WriteControlInfo` object directly to work around it. PSGet 2.2.5+
    bootstrap is chicken-and-egg with PowerShellGet 1.0.0.1; install in
    a fresh subprocess.
- 2026-05-15: `TcUnit_ResultExportXmlPath` GVL convention retired (see
  ADR-0010 section C.3). Bench's `add_library_placeholder` call now
  passes `parameters={"xUnitEnablePublish": "TRUE"}` to flip the
  publisher on; `/results` defaults the XML path to the publisher's own
  default.
  - **Lesson:** TcUnit's xUnit publisher is gated on
    `GVL_Param_TcUnit.xUnitEnablePublish` (off by default) and writes to
    `GVL_Param_TcUnit.xUnitFilePath`. The "TcUnit-..." naming pattern is
    TcKit-side convention only; nothing in TcUnit reads it. Validate
    convention claims against the upstream source.
