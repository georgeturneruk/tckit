# B4 bError-propagation fixture

ADR-0007 §"B4 missing bError propagation". Same authoring chain as
B1-B3, different bug category.

## What the seeded bug is

`FB_PipelineStage` wraps an inner `FB_PipelineInner` whose `bError`
output is raised when its input is negative. `FB_PipelineStage.Step`
calls the inner FB but never reads or propagates `inner.bError` to
the outer `bError` output. Consumers see the outer stage always
reporting no error, regardless of inner state.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_B4.py [--force]
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
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/B4-bError
```
