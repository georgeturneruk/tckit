# B4 — Missing `bError` propagation in `FB_PipelineStage`

The TwinCAT solution in this directory has two PLC projects:

- `B4Pipeline_Plc` — the **library under test**. Contains
  `FB_PipelineStage` (and the inner `FB_PipelineInner` it wraps).
- `PipelineTests` — the **test project**. References the library
  by name; instantiates `FB_PipelineStage` and exercises it.

A failing test is reporting:

> **Test suite:**   `FB_PipelineStageTests`
> **Test:**         `OuterReportsErrorWhenInnerErrors`
> **Assertion:**    `AssertEquals_BOOL` failed
> **Expected:**     `TRUE`
> **Actual:**       `FALSE`
> **Message:**      `stage.bError should be TRUE when inner FB raises an error`

## Your task

Modify the source under `B4Pipeline_Plc/` so the test passes.

**Constraints:**

- Do not change anything under `PipelineTests/`. Test files are
  read-only for grading.
- After editing the library, the consumer build resolves against the
  *installed* library, not the source — so the bench harness re-runs
  `save_plc_as_library` on `B4Pipeline_Plc` before each build.
  You don't need to invoke it yourself; the harness handles it
  between iterations of `run_tests`.

## Hint shape

(Vanilla and TcKit arms get this prompt verbatim — no diagnosis hints.)
