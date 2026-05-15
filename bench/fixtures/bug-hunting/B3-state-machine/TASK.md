# B3 — State-machine wrong transition in `FB_TrafficLight.Step`

The TwinCAT solution in this directory has two PLC projects:

- `B3TrafficLight_Plc` — the **library under test**. Contains
  `FB_TrafficLight` with a buggy `Step` method.
- `TrafficLightTests` — the **test project**. References the library
  by name; instantiates `FB_TrafficLight` and exercises it.

A failing test is reporting:

> **Test suite:**   `FB_TrafficLightTests`
> **Test:**         `GreenTransitionsToAmber`
> **Assertion:**    `AssertEquals_INT` failed
> **Expected:**     `3` (Amber)
> **Actual:**       `0` (Red)
> **Message:**      `Light in Green should transition to Amber`

## Your task

Modify the source under `B3TrafficLight_Plc/` so the test passes.

**Constraints:**

- Do not change anything under `TrafficLightTests/`. Test files are
  read-only for grading.
- Do not edit `.plcproj` or `.TcPOU` XML directly. Use the TwinCAT
  automation interface (e.g. TcKit's `update_method_body` /
  `update_method_body_patch`) for any change.
- After editing the library, the consumer build resolves against the
  *installed* library, not the source — so the bench harness re-runs
  `save_plc_as_library` on `B3TrafficLight_Plc` before each build.
  You don't need to invoke it yourself; the harness handles it
  between iterations of `run_tests`.

## Hint shape

(Vanilla and TcKit arms get this prompt verbatim — no diagnosis hints.)
