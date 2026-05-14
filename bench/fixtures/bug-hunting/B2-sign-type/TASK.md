# B2 — Sign / type bug in `FB_Counter.GetSignedDelta`

The TwinCAT solution in this directory has two PLC projects:

- `B2SignedDelta_Plc` — the **library under test**. Contains
  `FB_Counter` with a buggy `GetSignedDelta` method.
- `CounterTests` — the **test project**. References the library
  by name; instantiates `FB_Counter` and exercises it.

A failing test is reporting:

> **Test suite:**   `FB_CounterTests`
> **Test:**         `DeltaIsNegativeWhenBExceedsA`
> **Assertion:**    `AssertEquals_DINT` failed
> **Expected:**     `-2`
> **Actual:**       `4294967294`
> **Message:**      `GetSignedDelta(a := 5, b := 7) should return -2`

## Your task

Modify the source under `B2SignedDelta_Plc/` so the test passes.

**Constraints:**

- Do not change anything under `CounterTests/`. Test files are
  read-only for grading.
- Do not edit `.plcproj` or `.TcPOU` XML directly. Use the TwinCAT
  automation interface (e.g. TcKit's `update_pou_item` /
  `update_pou_item_patch`) for any change.
- After editing the library, the consumer build resolves against the
  *installed* library, not the source — so the bench harness re-runs
  `save_plc_as_library` on `B2SignedDelta_Plc` before each build.
  You don't need to invoke it yourself; the harness handles it
  between iterations of `run_tests`.

## Hint shape

(Vanilla and TcKit arms get this prompt verbatim — no diagnosis hints.)
