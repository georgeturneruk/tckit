# B3 state-machine fixture

ADR-0007 §"B3 state-machine wrong transition". Same authoring chain
as B1/B2, different bug category.

## What the seeded bug is

`FB_TrafficLight.Step` has a `CASE` block driving a Red -> RedAmber
-> Green -> Amber -> Red cycle. The transition out of the Green
state (state=2) wrongly sets the next state to `Red` (0) instead of
`Amber` (3), so the light skips the Amber phase on its way down.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_B3.py [--force]
```

`--force` wipes the generated tree (keeping CLAUDE.md, TASK.md, and
this README) before re-authoring. Requires the bridge service at
`$BRIDGE_URL` and a TwinCAT 4026 install with the TcUnit library
present in the system repository.

## Deferred + reset

Same shape as B1's README. The bench machine needs the
[TcUnit library](https://github.com/tcunit/TcUnit) installed; see
`bench/README.md` Prerequisites. Reset between bench runs:

```
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/B3-state-machine
```
