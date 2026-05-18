---
date: 2026-05-18
status: Current
related_adrs: [0007, 0008, 0012]
---

# 2026-05-18 — T2 PID anti-windup TDD pair (seed)

Third bug-hunting fixture authored. Same harness shape as the T1
round; tightened on three things T1 exposed:

- **TEST_ORDERED + `__POUNAME()`** throughout. Tests are
  state-accumulating (anti-windup, integral build-up), so the T1
  same-scan all-tests pattern would have collapsed the cycle
  semantics. The new TwinCAT CLAUDE.md template
  (ADR-0008 status note for 2026-05-18) calls this out.
- **`CyclicReachableThroughInterface`** as the 11th assertion. A
  PID written in the FB body rather than in `Update` passes the
  first ten tests (the tests call `pid.Update(...)` on the
  concrete FB) but fails the 11th, which calls
  `iPid.Update(...)` through an `I_Pid` reference. This explicitly
  grades the cyclic-in-method rule.
- **Setter validation** (`SetterRejectsNegativeKp`). Forces the
  model to author a non-trivial setter, exercising `add_property`
  with a non-empty setter body.

Both arms have not yet been run; this doc is the seed for the
results write-up.

## Fixture authoring

Authored via `bench/fixtures/bug-hunting/_author/author_T2.py`,
which uses the writer MCP tools end-to-end:

Library PLC (`T2Pid_Plc`):

- `I_Pid` interface, methods only (`Update`, `Reset`).
  Properties live on the concrete FB, not the interface, to dodge
  the abstract-property body issue.
- `FB_Pid` with the function-block header and an empty `VAR`
  block. No `IMPLEMENTS`, no methods, no properties.

Tests PLC (`PidTests`):

- `FB_PidTests EXTENDS TcUnit.FB_TestSuite` with eleven
  ordered test methods covering:
  1. `PProportionalOnly`
  2. `OutputClampsToMax`
  3. `OutputClampsToMin`
  4. `IntegralAccumulates`
  5. `AntiWindupHoldsIntegral`
  6. `DerivativeOnMeasurementNoSetpointSpike`
  7. `ReverseModeFlipsSign`
  8. `ResetClearsIntegral`
  9. `SetterRejectsNegativeKp`
  10. `IsSaturatedReflectsClampState`
  11. `CyclicReachableThroughInterface`
- MAIN drives the suite via `suite(); TcUnit.RUN();`.

Each test wraps its body in `IF TEST_ORDERED(__POUNAME()) THEN
... TEST_FINISHED(); END_IF`. Tests that accumulate state across
ticks use a `FOR` loop calling `pid.Update(...)` multiple times in
a single PLC scan, rather than spreading the accumulation across
PLC scans.

The fixture is gitignored for build artefacts (`_Boot/`,
`_Libraries/`, `*.tpzip`, `*.tszip`, etc.) per
`bench/fixtures/bug-hunting/.gitignore`. The committed footprint
on first author run is the `.sln`, both `.plcproj`s, the
`.TcPOU`/`.TcTTO` source files, and the CLAUDE.md template
dropped in by `scaffold_fixture`.

## Results — pending

Pending bench run. Will fill in:

| Task | Config | Calls | Tokens | Wall (s) | Test | Build |
| --- | --- | --- | --- | --- | --- | --- |
| T2-pid-anti-windup | empty (`--isolate-cwd`) | TBD | TBD | TBD | TBD | TBD |
| T2-pid-anti-windup | tckit (`--isolate-cwd --inject-skills`) | TBD | TBD | TBD | TBD | TBD |

Hypothesis going in: T1's vanilla-wins-9x finding was specific to
a five-line method body where the runtime cycle loop was waste.
T2 is materially larger (an interface to wire up, eight properties
to author, anti-windup logic to get right), so the tckit arm
should narrow the gap if the writer surface pays off. The eleventh
test should also pick up implementations that put cyclic logic
in the FB body, which we expect to be a vanilla failure mode given
the lack of TwinCAT-specific guidance in that arm.

## Bench invocation

```
python bench/run.py \
  --task bench/fixtures/bug-hunting/T2-pid-anti-windup/TASK.md \
  --config bench/configs/tckit.json \
  --sln-path bench/fixtures/bug-hunting/T2-pid-anti-windup/T2Pid.sln \
  --pre-save-as-library T2Pid_Plc \
  --post-run-tests PidTests \
  --tests-guard-path bench/fixtures/bug-hunting/T2-pid-anti-windup/PidTests_Tc/PidTests \
  --isolate-cwd \
  --inject-skills plugin/skills \
  --close-during-run \
  --runs 1
```

Vanilla arm: replace `--config bench/configs/tckit.json` with
`bench/configs/empty.json` and drop `--inject-skills`.
