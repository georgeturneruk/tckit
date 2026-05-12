---
adr: 0006
title: TestRunner adapter for TcUnit
status: Proposed
created: 2026-05-12
issue:
pr:
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

    def run_tests(self, plc_name: str | None = None) -> Result:
        return self._client.post("/tcunit-run", self._with_plc({...}, plc_name))

    def get_results(self, plc_name: str | None = None) -> TestResults:
        raw = self._client.post("/results", self._with_plc({}, plc_name))
        return _parse_test_results(raw)

    def get_status(self) -> TestStatus:
        # Polls /tcunit-run/status (lightweight; checks bTestSuitesFinished)
        ...

    def wait_complete(self, timeout_seconds: int = 60) -> Result:
        # Server-side polling already inside Invoke-TcUnitRun.ps1;
        # this method becomes a passthrough that surfaces the
        # bridge's wait result.
        ...
```

`plc_name` per ADR-0005. `Result` and `TestResults` shapes come
from the existing port; no new dataclasses needed.

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
