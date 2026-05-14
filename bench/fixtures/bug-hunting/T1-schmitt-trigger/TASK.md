# T1 — TDD: implement `FB_SchmittTrigger.Step`

The TwinCAT solution in this directory has two PLC projects:

- `T1SchmittTrigger_Plc` — the **library under test**. Contains
  `FB_SchmittTrigger` with a complete declaration but an empty
  `Step` method body (`;`).
- `SchmittTriggerTests` — the **test project**. References the
  library by name; instantiates `FB_SchmittTrigger` and exercises
  five hysteresis-band behaviours.

A failing test is reporting:

> **Test suite:**   `FB_SchmittTriggerTests`
> **Test:**         `LatchesHighAboveHighThreshold`
> **Assertion:**    `AssertEquals_BOOL` failed
> **Expected:**     `TRUE`
> **Actual:**       `FALSE`
> **Message:**      `trigger.Step() with fInput := 0.9 should latch HIGH`

Four further assertions cover:

- input below `fLowThreshold` -> output `FALSE`,
- input between thresholds (hysteresis band) -> output holds previous value,
- sequenced inputs across the band asserting correct transitions,
- boundary values exactly at the thresholds.

No hardcoded return value satisfies all five.

## Your task

Implement `FB_SchmittTrigger.Step` under `T1SchmittTrigger_Plc/` so
the test suite passes. The Schmitt-trigger behaviour is:

- output latches `TRUE` when `fInput > fHighThreshold`,
- output latches `FALSE` when `fInput < fLowThreshold`,
- output holds its previous value when
  `fLowThreshold <= fInput <= fHighThreshold`.

**Constraints:**

- Do not change anything under `SchmittTriggerTests/`. Test files are
  read-only for grading.
- Do not edit `.plcproj` or `.TcPOU` XML directly. Use the TwinCAT
  automation interface (e.g. TcKit's `update_pou_item` /
  `update_pou_item_patch`) for any change.
- After editing the library, the consumer build resolves against the
  *installed* library, not the source — so the bench harness re-runs
  `save_plc_as_library` on `T1SchmittTrigger_Plc` before each build.
  You don't need to invoke it yourself; the harness handles it
  between iterations of `run_tests`.

## Hint shape

(Vanilla and TcKit arms get this prompt verbatim — no diagnosis hints.)
