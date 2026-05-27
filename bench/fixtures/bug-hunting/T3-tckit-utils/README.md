# T3 TcKit utilities TDD fixture

ADR-0007 task T3. Third TDD task after T1 schmitt-trigger and T2 PID
anti-windup. Where T1 and T2 each exercise a single FB, T3 stands up
a small **generic-utility library** with three independent surfaces:
a PID controller (the API carried over from T2), a typeless ring
buffer over user-supplied storage, and a string accumulator with
helper functions. The library is organised into per-utility folders
on both PLCs, exercising the `add_folder` + `parent_folder` writer
calls shipped in ADR-0013.

## What the fixture provides

Library (`T3TckitUtils_Plc`):

- `POUs/PID/` - `I_Pid` interface (Update, Reset) and an empty
  `FB_Pid` function block.
- `POUs/RingBuffer/` - an empty `FB_RingBuffer` function block.
- `POUs/Strings/` - an empty `FB_StringBuilder` plus four empty
  string-utility FUNCTION stubs (`F_Trim`, `F_StartsWith`,
  `F_EndsWith`, `F_Contains`).

Tests project (`TckitUtilsTests`):

- `DUTs/RingBuffer/ST_Sample` - a tests-internal fixture struct
  (`t : LREAL; v : LREAL;`) used only by the user-defined-type
  ring-buffer test. Kept out of the library so the public surface
  stays generic.
- Three TcUnit suites, one per utility folder, totalling 28 ordered
  tests:
  - `FB_PidTests` (11 tests) - identical to the T2 suite.
  - `FB_RingBufferTests` (9 tests) - empty/full/wrap-around plus
    round-trips for LREAL, INT, and `ST_Sample`.
  - `FB_StringTests` (8 tests) - builder length / overflow / clear
    behaviour plus trim, starts-with, ends-with, contains.
- The standard TwinCAT CLAUDE.md template installed by the shared
  scaffolder, plus one bench-specific topic file:
  `twincat/any-type-pattern.md`.

## What the model must author

- `FB_Pid`: `IMPLEMENTS I_Pid`, the `Update` and `Reset` methods, and
  the eight properties (`Kp`, `Ki`, `Kd`, `OutputMin`, `OutputMax`,
  `Mode`, `IntegralTerm` GET-only, `IsSaturated` GET-only). Setters
  for `Kp`/`Ki`/`Kd` must reject negatives. Whatever internal state
  the implementation needs (a VAR block on the FB is fine; an
  `ST_PidState` struct DUT is the cleaner option).
- `FB_RingBuffer`: `Configure`, `Push`, `Pop`, `Peek`, `Clear`
  methods, and `Count` / `Capacity` / `IsEmpty` / `IsFull`
  properties. The method signatures take `ANY` so the call site stays
  pointer-free; `MEMCPY` does the byte copies behind the scenes.
- `FB_StringBuilder`: `Append`, `AppendLine`, `Clear`, `CopyTo`, and
  the three GET-only properties (`Length`, `Capacity`, `IsFull`).
- `F_Trim`, `F_StartsWith`, `F_EndsWith`, `F_Contains` bodies.

Everything else (state structs, helper enums) is at the model's
discretion. The bench publishes only the contract under `TASK.md`.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_T3.py [--force]
```

Requires the bridge running and the TcUnit library installed in the
system repository. The author script creates the sln, both PLC
projects, the folders, the interface, the four FB stubs, the four
FUNCTION stubs, `ST_Sample`, all 28 tests, MAIN, and finally drops
the TwinCAT CLAUDE.md template.

The .library artefact and IDE build outputs are gitignored
(`bench/fixtures/bug-hunting/.gitignore`); only the source files
(`.sln`, `.plcproj`, `.TcPOU`, `.TcDUT`, `.TcIO`, `.tsproj`, etc.)
should be committed.

## References

- [ADR-0007](../../../adrs/0007-bug-hunting-bench.md) - bench fixture
  layout, tamper guard, harness orchestration.
- [ADR-0008](../../../adrs/0008-portable-twincat-claude-md.md) - the
  CLAUDE.md template that ships in this fixture.
- [ADR-0012](../../../adrs/0012-property-and-dut-writer.md) - the
  `add_property` and `add_dut` writer tools the model uses to fill
  in the stubs.
- [ADR-0013](../../../adrs/0013-folder-organisation-deletes-and-reader-symmetry.md) -
  the `add_folder` + `parent_folder` capability this bench
  demonstrates.
