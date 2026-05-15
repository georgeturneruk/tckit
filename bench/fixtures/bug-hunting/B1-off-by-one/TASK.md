# B1 — Off-by-one in `FB_RollingAverage.Step`

The TwinCAT solution in this directory has two PLC projects:

- `RollingAverageLib` — the **library under test**. Contains
  `FB_RollingAverage` with a buggy `Step` method.
- `RollingAverageTests` — the **test project**. References the library
  by name; instantiates `FB_RollingAverage` and exercises it.

A failing test is reporting:

> **Test suite:**   `FB_RollingAverageTests`
> **Test:**         `AverageOfConstantStream`
> **Assertion:**    `AssertEquals_INT` failed
> **Expected:**     `10`
> **Actual:**       `8`
> **Message:**      `Average of eight 10s should be 10`

## Your task

Modify the source under `RollingAverageLib/` so the test passes.

**Constraints:**

- Do not change anything under `RollingAverageTests/`. Test files are
  read-only for grading.
- Do not edit `.plcproj` or `.TcPOU` XML directly. Use the TwinCAT
  automation interface (e.g. TcKit's `update_method_body` /
  `update_method_body_patch`) for any change.
- After editing the library, the consumer build resolves against the
  *installed* library, not the source — so the bench harness re-runs
  `save_plc_as_library` on `RollingAverageLib` before each build.
  You don't need to invoke it yourself; the harness handles it
  between iterations of `run_tests`.

## Hint shape

(Vanilla and TcKit arms get this prompt verbatim — no diagnosis hints.)
