# B5 default-init fixture

ADR-0007 §"B5 wrong default initialisation". Same authoring chain as
B1-B4, different bug category.

## What the seeded bug is

`FB_PIDController.fGain` defaults to `0.0`. The Step method
multiplies the error input by `fGain`, so the first call returns
`0.0` regardless of the error magnitude. The multiplicative identity
(`1.0`) is the sensible default — otherwise the controller is a
no-op until the consumer explicitly sets `fGain`.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_B5.py [--force]
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
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/B5-default-init
```
