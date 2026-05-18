# T2 PID anti-windup TDD fixture

ADR-0007 task T2. Second TDD task after T1 schmitt-trigger, but
substantially larger: the model authors a non-trivial control law
plus several properties, where T1's `Step` was five lines of
hysteresis.

## What the fixture provides

- An `I_Pid` interface with `Update` and `Reset` methods.
- An empty `FB_Pid` function block (no `IMPLEMENTS`, no methods).
- A TcUnit suite (`FB_PidTests`, eleven ordered tests) covering
  proportional action, output clamping, integral accumulation,
  anti-windup, derivative-on-measurement, reverse mode, reset,
  setter validation, saturation status, and an interface-call
  polymorphism check.
- The standard TwinCAT CLAUDE.md template (dropped in by the shared
  scaffolder) covering the cyclic-in-method, polymorphism-arrays,
  and TcUnit conventions the suite relies on.

## What the model must author

- `IMPLEMENTS I_Pid` on `FB_Pid`.
- `Update` and `Reset` methods that satisfy the interface.
- Eight properties (`Kp`, `Ki`, `Kd`, `OutputMin`, `OutputMax`,
  `Mode`, `IntegralTerm` (GET only), `IsSaturated` (GET only)). At
  least the GET+SET setters for `Kp`/`Ki`/`Kd` validate against
  negatives; the corresponding tests assert that.
- Whatever internal state the implementation needs (a VAR block on
  the FB is fine; an `ST_PidState` struct DUT is the cleaner
  option). The new `add_dut` writer tool makes the latter
  possible.
- Optionally, a `GVL_PidDefaults` with initial tunings.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_T2.py [--force]
```

Requires the bridge running and the TcUnit library installed in the
system repository. The author script creates the sln, both PLC
projects, the interface, the empty FB, all eleven tests, MAIN, and
finally drops the TwinCAT CLAUDE.md template.

The .library artefact and IDE build outputs are gitignored
(`bench/fixtures/bug-hunting/.gitignore`); only the source files
(`.sln`, `.plcproj`, `.TcPOU`, etc.) should be committed.

## References

- [ADR-0007](../../../adrs/0007-bug-hunting-bench.md) - bench fixture
  layout, tamper guard, harness orchestration.
- [ADR-0008](../../../adrs/0008-portable-twincat-claude-md.md) - the
  CLAUDE.md template that ships in this fixture.
- [ADR-0012](../../../adrs/0012-property-and-dut-writer.md) - the
  `add_property` and `add_dut` writer tools that this bench
  exercises.
