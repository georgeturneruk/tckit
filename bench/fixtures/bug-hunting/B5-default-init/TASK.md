# B5 — Wrong default initialisation in `FB_PIDController`

The TwinCAT solution in this directory has two PLC projects:

- `B5PIDController_Plc` — the **library under test**. Contains
  `FB_PIDController` whose `fGain` is initialised to the wrong value.
- `PIDControllerTests` — the **test project**. References the library
  by name; instantiates `FB_PIDController` and exercises it.

A failing test is reporting:

> **Test suite:**   `FB_PIDControllerTests`
> **Test:**         `FirstStepReturnsNonZeroForNonZeroError`
> **Assertion:**    `AssertEquals_REAL` failed
> **Expected:**     `2.5`
> **Actual:**       `0.0`
> **Message:**      `Step(2.5) on default-initialised controller should be 2.5`

## Your task

Modify the source under `B5PIDController_Plc/` so the test passes.

**Constraints:**

- Do not change anything under `PIDControllerTests/`. Test files are
  read-only for grading.
- After editing the library, the consumer build resolves against the
  *installed* library, not the source — so the bench harness re-runs
  `save_plc_as_library` on `B5PIDController_Plc` before each build.
  You don't need to invoke it yourself; the harness handles it
  between iterations of `run_tests`.

## Hint shape

(Vanilla and TcKit arms get this prompt verbatim — no diagnosis hints.)
