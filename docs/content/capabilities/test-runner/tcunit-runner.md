# tcunit runner

**Port:** `TestRunner`
**Module:** `tckit.adapters.test_runners.tcunit_runner.TcUnitRunner`
**Requires:** the [TcUnit library](https://github.com/tcunit/TcUnit) installed in the system library repository (distributor `www.tcunit.org`). Without it, projects that reference TcUnit will fail to build with "library not found".

End-to-end TcUnit test driver: orchestrates a run against a target
runtime, polls for completion via ADS, and parses TcUnit's
JUnit-shaped XML output into a structured `TestResults`. Implemented
under [ADR-0006](https://github.com/georgeturneruk/tckit/blob/main/adrs/0006-test-runner-tcunit-adapter.md).

## Configuration

```json
{ "test_runner": "tcunit" }
```

The runner reads no env config of its own; it picks up `BRIDGE_URL`,
`PLC_PROJECT_PATH`, `PLC_PROJECT_NAME`, and `TARGET_AMS_ID` from the
shared environment along with the rest of TcKit. The optional
`TCKIT_TEST_RUN_TIMEOUT` env var (default 180 seconds) caps the HTTP
timeout on the `/tcunit-run` call when the test suites take a long
time to complete.

## xUnit publisher output

TcUnit ships an xUnit publisher gated on `GVL_Param_TcUnit.xUnitEnablePublish`
(a library parameter on the TcUnit placeholder). When TRUE, the publisher
writes JUnit-style XML to the path on `GVL_Param_TcUnit.xUnitFilePath`,
which defaults to `%TC_BOOTPRJPATH%tcunit_xunit_testresults.xml` —
i.e. `C:\TwinCAT\3.1\Boot\Plc\Port_851\tcunit_xunit_testresults.xml`
for a PLC running at the standard runtime port.

Override the publisher flag through the writer port:

```python
writer.add_library_placeholder(
    "TestsPlc", "TcUnit", "TcUnit",
    distributor="www.tcunit.org",
    parameters={"xUnitEnablePublish": "TRUE"},
)
```

Without the override, suites still run and the runner state machine
completes correctly, but no XML lands and `/results` has nothing to
parse. The bridge's `/tcunit-run` reports `xml_published=false` in
that case so callers can tell why the file isn't there.

If the consumer also overrides `xUnitFilePath`, pass the resolved
path back to `/results` explicitly via `XmlPath` — the bridge does
not introspect the runtime to discover overrides today.

## Bridge routes

The adapter is a thin route-caller over three Windows bridge
endpoints (all `POST`):

| Route | Script | Purpose |
|-------|--------|---------|
| `/tcunit-run` | `Invoke-TcUnitRun.ps1` | Delete stale XML, ensure runtime in Run mode, poll `TcUnit.G_TestRunner.bTestSuitesFinished` via ADS, wait for the XML to land, return live summary counters. |
| `/results` | `Get-TcUnitResults.ps1` | Read the XML at the resolved path, parse `<testsuites>`/`<testsuite>`/`<testcase>`/`<failure>` into the structured shape below. |
| `/runtime` | `Invoke-TcRuntime.ps1` | Mode-aware runtime transition. `Mode = "Run"` is what the test run uses; `start_runtime` also reaches it. |

Stale XML is deleted at the start of every run; the script records a
start epoch and verifies the new XML's mtime is strictly greater
before returning. This is the only safeguard against reading
yesterday's results.

## TestResults JSON shape

```json
{
  "suites": [
    {
      "name": "FB_Adder_Suite",
      "tests": [
        {
          "name": "Adds_TwoPositives",
          "passed": true,
          "asserts": 1,
          "failures": [],
          "duration_seconds": 0.04
        },
        {
          "name": "Subtracts_TwoPositives",
          "passed": false,
          "asserts": 1,
          "failures": [
            {
              "message": "AssertEquals_INT failed",
              "expected": "1",
              "actual": "2",
              "line": 42
            }
          ],
          "duration_seconds": 0.32
        }
      ]
    }
  ],
  "summary": {
    "suites": 1,
    "tests": 2,
    "asserts": 2,
    "failures": 1,
    "errors": 0,
    "duration_seconds": 0.36
  }
}
```

`expected`, `actual`, and `line` are extracted from the failure body
on a best-effort basis using common TcUnit assertion message
patterns. If a particular assertion's body doesn't match, the
`message` field still carries the full failure text verbatim.

## Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| `Tests did not finish within Ns` | The suites are slower than `TimeoutSeconds`; raise via `TCKIT_TEST_RUN_TIMEOUT` (HTTP side) and re-deploy with a higher `-TimeoutSeconds` if necessary. |
| `TcUnit results XML not found at <path>` | Either `xUnitEnablePublish` wasn't set TRUE on the TcUnit placeholder (publisher off; no XML ever written), or the boot project hasn't activated yet — re-`deploy` then re-`run_tests`. |
| `XML not refreshed within 5s of suites finishing` | TcUnit emitted nothing despite the suites-finished flag flipping; usually means a path permission issue or the file was locked by another reader. |
| `TcXaeMgmt module not found` | The bridge depends on Beckhoff's `TcXaeMgmt` PowerShell module for ADS operations. Easiest fix: run `tckit doctor` and accept the install prompt. Manual: `Install-Module -Name TcXaeMgmt -Scope CurrentUser -Force`. See [bridge setup](../../getting-started/bridge-setup.md#tcxaemgmt-powershell-module) for offline / TwinCAT-bundled alternatives. |
