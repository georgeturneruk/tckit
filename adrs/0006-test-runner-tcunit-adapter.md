---
adr: 0006
title: TestRunner adapter for TcUnit
status: Implemented
created: 2026-05-12
issue:
pr: 64, 65, 67, 68, 69
---

## Context

`tckit/ports/test_runner.py` defines a `TestRunner` port with
`run_tests`, `wait_complete`, `get_results`, `get_status`. The MCP
tools `run_tests` and `get_test_results` are wired through
`tckit/server.py` (lines 537-538) and the config registry knows
about `TcUnitRunner`. But the adapter at
`tckit/adapters/test_runners/tcunit_runner.py` is a complete stub:

```python
def run_tests(self) -> Result:
    raise NotImplementedError("tcunit_runner.run_tests() not yet implemented")
```

The bridge harness `bridge/harness/Get-TcUnitResults.ps1` is also a
stub returning `{success: false, error: 'not yet implemented'}`.
`bridge/harness/Invoke-TcRuntime.ps1` (which `run_tests` depends on)
is in the same state.

The bug-hunting bench (ADR-0007) is the immediate driver: closed-loop
testing requires the model to run tests, read results, patch, and
re-run. Open-loop validation for the vanilla arm also needs the
harness to run tests post-session and write a sibling
`.test-result.json`. Without this work, neither config can be graded.

The work also unblocks the existing `tc-build-test-loop` skill,
which documents a build-deploy-test workflow that currently dead-ends
at `run_tests` returning `NotImplementedError`.

## Decision

Implement the runner end-to-end against TcUnit's XML export. Three
pieces: bridge harness, adapter, and a fixed test-project
convention.

### TcUnit-ResultExportXmlPath convention

TcUnit ships a `TcUnit-ResultExportXmlPath` GVL constant that
controls where the test runtime writes its XML output. The bench
fixtures (ADR-0007) and any downstream test project that wants
TcKit's runner pin this to a deterministic absolute path:

```pascal
VAR_GLOBAL CONSTANT
    TcUnit_ResultExportXmlPath : T_MaxString :=
        'C:\TwinCAT\3.1\Boot\Plc\TcUnitResults.xml';
END_VAR
```

The path is read from the test project's GVL when the runner needs
to fetch results; no env config required. Documented in the
portable CLAUDE.md template (ADR-0008) as a project convention
for any team that wants TcKit's runner.

### Bridge harness scripts

Three scripts, two new, one promoted from stub:

- **`bridge/harness/Invoke-TcRuntime.ps1`** (stub today): build the
  active configuration, activate it on the target, switch the
  runtime to Run mode. Caller specifies `-Mode 'Run' | 'Config'`,
  `-PlcName` (per ADR-0005), and `-Wait` (block until target is in
  the requested mode, with a timeout). Returns `{success, mode,
  details}`.

- **`bridge/harness/Invoke-TcUnitRun.ps1`** (new): run a test
  cycle to completion. Sequence: ensure target is in Run mode (call
  `Invoke-TcRuntime` with `-Mode Run -Wait`), poll the
  `TcUnit.G_TestRunner.bTestSuitesFinished` symbol (or equivalent)
  via the COM symbol-browser interface until true or the timeout
  expires, return `{success, duration_seconds, summary: {suites,
  tests, asserts, failures, errors}}` derived from the runner's
  in-memory counters. The XML file is written by TcUnit at the
  same moment, so a subsequent `Get-TcUnitResults` returns the
  full structured shape.

- **`bridge/harness/Get-TcUnitResults.ps1`** (promote from stub):
  read the XML path from the test PLC project's
  `TcUnit-ResultExportXmlPath` constant (resolved via the COM
  symbol API), parse the XML with `[System.Xml.XmlDocument]`,
  return a structured JSON shape matching the `TestResults`
  dataclass in `tckit/ports/types.py`.

Routes added in `Start-Bridge.ps1`:

- `POST /runtime` -> `Invoke-TcRuntime.ps1`
- `POST /tcunit-run` -> `Invoke-TcUnitRun.ps1` (new)
- `POST /results` -> `Get-TcUnitResults.ps1` (the `/results` route
  exists today; the script behind it is the new one)

### Adapter implementation

`tckit/adapters/test_runners/tcunit_runner.py` becomes a thin
route caller, same shape as `automation_writer.py`:

```python
class TcUnitRunner(TestRunner):
    def __init__(self, bridge_url: str = ...):
        self._client = BridgeClient(bridge_url)

    def run_tests(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> Result:
        return self._client.post(
            "/tcunit-run",
            self._with_target_and_plc({...}, target_ams_id, plc_name),
        )

    def get_results(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> TestResults:
        raw = self._client.post(
            "/results",
            self._with_target_and_plc({}, target_ams_id, plc_name),
        )
        return _parse_test_results(raw)

    def get_status(self) -> TestStatus:
        # Polls /tcunit-run/status (lightweight; checks bTestSuitesFinished)
        ...

    def wait_complete(
        self,
        target_ams_id: str,
        timeout_seconds: int = 60,
        *,
        plc_name: str | None = None,
    ) -> Result:
        # Server-side polling already inside Invoke-TcUnitRun.ps1;
        # this method becomes a passthrough that surfaces the
        # bridge's wait result.
        ...
```

`plc_name` per ADR-0005. ``target_ams_id`` is a required first
positional argument on every test-execution method, matching the IDE
workflow where the operator selects both the test PLC project and the
target runtime before running. Implicit "last deployed target" state
would be brittle across MCP calls. ``Result`` and ``TestResults`` shapes
come from the existing port; no new dataclasses needed.

### TestResults shape

The port already defines `TestResults`. Re-state the contract
here for completeness so the adapter has a clear target:

- `success: bool` - all assertions passed, no errors.
- `summary: {suites: int, tests: int, asserts: int, failures: int, errors: int, duration_seconds: float}`.
- `suites: list[TestSuite]` where each suite carries
  `{name, success, tests: list[TestCase]}`.
- Each `TestCase` carries
  `{name, success, asserts, failures: list[AssertFailure]}`.
- Each `AssertFailure` carries `{message, expected, actual, line}`
  matching TcUnit's XML schema.

The XML parser maps fields one-to-one; no inference.

### Iteration discipline

The `tc-build-test-loop` skill already documents a 5-iteration
cap for build-test loops. The runner itself does not enforce
this; the cap lives at the skill level where the model decides
whether to re-run. ADR-0007's prompt is silent on iteration count;
the skill carries the guard.

## Alternatives considered

- **Parse TcUnit console output instead of XML.** Rejected:
  console output is fragile across TwinCAT and TcUnit versions,
  and the XML is the documented machine-readable interface.
- **Read live TcCOM symbols for assertion details rather than
  XML.** Rejected: tied to a specific build configuration, harder
  to keep stable, no failure-message text.
- **Run tests via msbuild + a runtime invocation outside the
  bridge.** Rejected: bypasses XAE, the bridge's COM handle
  already owns the runtime control surface, and the harness is
  the One Rule's enforcement point for runtime mutations.
- **One bridge route that does build + activate + run + read in
  one POST.** Rejected: violates the per-step retry model.
  Build failures, activation failures, and test failures all need
  distinct error paths and the model needs to be able to call
  each step independently.

## Consequences

**Enables:** closed-loop benching (ADR-0007), the `tc-build-test-loop`
skill's documented workflow, any operator wanting to run TcUnit
suites from Claude.

**Costs:** ~30 seconds of wall-clock per test cycle on a
moderately-sized solution (build + activate + run). For the
bug-hunting bench at 6 tasks x 2 configs with a 5-cap, worst-case
is ~30 minutes of runtime cycling per bench round. Acceptable.

**Locks in:** the `TcUnit-ResultExportXmlPath` convention. Any
downstream test project that wants TcKit's runner has to set this
GVL constant. Documented in the portable CLAUDE.md template
(ADR-0008) and the test-runner docs page.

**Risks:** TcUnit's XML export is synchronous with the test
program but file-system writes are not instantaneous. The harness
must wait for the file's mtime to update past the `Invoke-TcUnitRun`
start time before reading; otherwise it can read stale XML from a
previous run. Mitigation: the runner deletes the XML file before
invoking the run, then waits for it to reappear.

## Status notes

- 2026-05-12: Drafted as `Proposed`. Implementation depends on
  ADR-0005 (multi-project) landing first so `plc_name` is
  available on the new tools.
- 2026-05-12: Interface tightened during ADR-0005 implementation.
  ``run_tests``, ``wait_complete``, and ``get_results`` now take a
  required ``target_ams_id`` as the first positional argument
  (matching ``BuildRunner.deploy`` shape). The original draft had
  no target argument and implied a stateful "last deployed target"
  model; that's brittle in an MCP session and asymmetric with the
  rest of the BuildRunner port. The stub signatures already carry
  the parameter; this ADR's implementation just fills in the bodies.
- 2026-05-13: Port narrowed during Phase 0 implementation. Dropped
  ``wait_complete`` and ``get_status`` from the ``TestRunner`` ABC,
  and removed the ``TestStatus`` enum. Neither was wired through
  ``tckit/server.py`` and the wait already lives server-side inside
  ``Invoke-TcUnitRun.ps1``, so both abstract methods were dead by
  design. The two-method ABC (``run_tests`` + ``get_results``) is
  the honest shape; this ADR's pseudocode for "Adapter implementation"
  is now larger than what shipped — read the code, not this section,
  for the exact surface.
- 2026-05-13: ``TestResults`` widened to the shape this ADR specifies
  (``AssertFailure``, ``TestResultsSummary``, ``summary`` block,
  ``asserts`` / ``failures`` on ``TestCase``). ``TestCase.message``
  dropped because ``AssertFailure.message`` carries the same detail
  per failure.
- 2026-05-13: ``_to_result`` extracted from ``automation_writer.py``
  to ``tckit/utils/results.py``. New ``tcunit_runner`` adapter uses
  the shared helper without violating the One Rule.
- 2026-05-14: Bench validation surfaced two real bugs. (1) The
  original Phase 1 draft called ``ITcSysManager.SetConfigMode()``
  which doesn't exist — TC3 has no purely-COM Config-mode API. (2)
  An ADS-based fix initially used ``AdsState.Config`` (15, steady-state)
  instead of ``AdsState.Reconfig`` (16, the transition command).
  Both were rendered moot by the next item.
- 2026-05-14: Operator-side rethink. Instead of loading
  ``TwinCAT.Ads.dll`` ourselves and reinventing the runtime-mode
  WriteControl wire, the bridge depends on Beckhoff's signed
  ``TcXaeMgmt`` PowerShell module. ``Invoke-TcRuntime.ps1`` shrinks
  to a wrapper around ``Restart-TwinCAT``; ``Invoke-TcUnitRun.ps1``
  uses ``New-TcSession`` + ``Read-TcValue`` for symbol polling.
  ``_TcUnit.psm1`` shrinks from ~150 lines to ~60. ``tckit doctor``
  reads the bridge's new ``/health`` dependencies block and prompts
  the operator to install missing modules via a new bridge
  ``/install-dependency`` route (allow-listed, ``CurrentUser`` scope).
- 2026-05-14: Bench-validated end-to-end on a local 4026 runtime:
  ``tckit doctor`` -> install prompt -> ``TcXaeMgmt`` 7.0.54 installed
  via the bridge route -> ``Invoke-TcRuntime -Mode Config`` then
  ``-Mode Run`` round-tripped against ``192.168.0.142.1.1``,
  confirmed via ``Get-AdsState``. Caught and fixed during the
  bench session: ``-AcceptLicense`` parameter compatibility with
  PowerShellGet 1.0.0.1, the PSGet 2.2.5+ bootstrap chicken-and-egg
  (run install in a fresh subprocess), and ``TcXaeMgmt`` 7.x's
  ``-ThrowError`` cast bug (work around by reading the
  ``WriteControlInfo`` object directly).
- 2026-05-14: Marked ``Implemented`` once #68 landed (covering the
  TcXaeMgmt refactor on top of #65). Full PR set:
  [#64](https://github.com/georgeturneruk/tckit/pull/64) port +
  schema, [#65](https://github.com/georgeturneruk/tckit/pull/65)
  bridge harness, [#67](https://github.com/georgeturneruk/tckit/pull/67)
  docs + template, [#68](https://github.com/georgeturneruk/tckit/pull/68)
  TcXaeMgmt refactor, [#69](https://github.com/georgeturneruk/tckit/pull/69)
  Python adapter.
- 2026-05-15: The ``TcUnit-ResultExportXmlPath`` GVL convention
  documented above turned out to be a TcKit-side fiction — TcUnit
  never read that name. The actual publisher is gated on
  ``GVL_Param_TcUnit.xUnitEnablePublish`` (off by default) and
  writes to ``GVL_Param_TcUnit.xUnitFilePath``, defaulting to
  ``%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml``. Per ADR-0010
  §C, the bridge's ``Get-TcUnitXmlPath`` greper has been retired,
  the GVL constant has been removed from the template / fixtures /
  skill, and the bench's ``add_library_placeholder`` call now
  passes ``parameters={"xUnitEnablePublish": "TRUE"}`` to flip the
  publisher on. The route now defaults the XML path to the
  publisher's own default for a PLC at runtime port 851.
