# T1 Schmitt-trigger TDD fixture

ADR-0007 §"T1 TDD". Unlike B1-B5 there is no seeded bug; the
implementation is intentionally empty and the model must write it.

## What the fixture provides

`FB_SchmittTrigger` is fully declared (signature, `VAR_INPUT`,
`VAR_OUTPUT`, hysteresis thresholds) but its `Step` method body is
`;`. The accompanying TcUnit test suite asserts five behaviours
covering the hysteresis band — see TASK.md.

The bench's pass criterion is "all five assertions pass on a fresh
build". No hardcoded return value satisfies all of them, so the
model must implement the Schmitt-trigger logic correctly.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_T1.py [--force]
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
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/T1-schmitt-trigger
```
