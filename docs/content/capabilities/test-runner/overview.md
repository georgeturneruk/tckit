# Testing

Runtime control and TcUnit test orchestration over ADS. These target a runtime by AMS Net ID; no XAE needed.

| Tool | Purpose |
|---|---|
| `StartRuntime(targetAmsId)` | Restart the target into Run mode and wait until it's reached |
| `RunTests(targetAmsId, waitForResults?, timeoutSeconds?, plcName?)` | Run the TcUnit suites to completion; inlines failures-only results |
| `GetTestResults(targetAmsId, plcName?, xmlPath?)` | Parse the full results (passes included) from the published xUnit XML |

`StartRuntime` and `RunTests` act on a live target: execute-class, gated by the [safety stance](../../getting-started/permissions.md). `GetTestResults` only parses XML.

## Requirements

- The [TcUnit library](https://github.com/tcunit/TcUnit) installed (distributor `www.tcunit.org`), referenced via a placeholder.
- TcUnit's xUnit publisher enabled, or no XML is ever written:

```
SetPlaceholderParameters("TcUnit", '{"GVL_Param_TcUnit": {"xUnitEnablePublish": "TRUE"}}')
```

The XML lands at TcUnit's default path under the boot directory. If the project overrides `xUnitFilePath`, pass the resolved path to `GetTestResults` via `xmlPath`.

## Result shape

A parsed suite/test tree, not a console scrape:

```json
{
  "suites": [
    {
      "name": "FB_Adder_Suite",
      "tests": [
        {
          "name": "Subtracts_TwoPositives",
          "passed": false,
          "failures": [
            { "message": "AssertEquals_INT failed", "expected": "1", "actual": "2", "line": 42 }
          ]
        }
      ]
    }
  ],
  "summary": { "suites": 1, "tests": 2, "failures": 1, "errors": 0 }
}
```

`expected` / `actual` / `line` are extracted from TcUnit's assertion messages on a best-effort basis; the full failure text is always in `message`.

## Why this shape

The point of a test loop is feedback the model can act on without re-reading everything. A structured result tree lets it locate the one failing assertion and jump straight to that POU item via the [reader](../project-reader/overview.md).
