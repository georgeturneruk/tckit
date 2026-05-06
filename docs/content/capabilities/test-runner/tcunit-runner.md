# tcunit runner

**Port:** `TestRunner`  
**Module:** `tckit.adapters.test_runners.tcunit_runner.TcUnitRunner`  
**Status:** Phase 3 — planned

Triggers TcUnit test execution via the bridge, polls for XML output file creation, and parses suite/test/pass/fail/message into structured `TestResults`.

## Configuration

```json
{ "test_runner": "tcunit" }
```

Set `TCUNIT_RESULTS_PATH` in `.env` to the path where TcUnit writes its XML output. Validate this path on your 4026 machine before implementation.

## Result format

```json
{
  "suites": [
    {
      "name": "FB_MotorControl_Tests",
      "tests": [
        { "name": "Test_SpeedLimit", "passed": true },
        { "name": "Test_ErrorState", "passed": false, "message": "Expected E_State.Error" }
      ]
    }
  ]
}
```
