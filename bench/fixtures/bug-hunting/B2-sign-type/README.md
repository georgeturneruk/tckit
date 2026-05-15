# B2 sign / type fixture

ADR-0007 §"B2 sign / type". Sibling of B1 — same authoring chain,
different bug category.

## Layout

```
B2-sign-type/
├── CLAUDE.md                        ← copy of templates/twincat-claude.md
├── TASK.md                          ← bench prompt for both arms
├── README.md                        ← this file
├── B2SignedDelta.sln
├── B2SignedDelta.tspproj
├── B2SignedDelta_Plc/               ← Library PLC project (first plc; suffix added to avoid sln/project name collision)
│   ├── B2SignedDelta_Plc.plcproj
│   └── POUs/
│       └── FB_Counter.TcPOU
└── CounterTests/                    ← Tests PLC project (sibling, added via add_plc_project)
    ├── CounterTests.plcproj
    └── POUs/
        └── FB_CounterConsumer.TcPOU
```

The `.library` artefact (`B2SignedDelta_Plc.library`) is gitignored
and regenerated per run by `save_plc_as_library`.

## What the seeded bug is

`FB_Counter.GetSignedDelta` declares its return type as `UDINT`
(unsigned). For inputs where `b > a`, the subtraction underflows to
~4 billion instead of producing a negative result. The correct
return type is `DINT`.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_B2.py [--force]
```

`--force` wipes the generated tree (keeping CLAUDE.md, TASK.md, and
this README) before re-authoring. Requires the bridge service at
`$BRIDGE_URL` and a TwinCAT 4026 install with the TcUnit library
present in the system repository.

## Deferred

Same shape as B1's deferred list:

- The `FB_CounterTests` `FB_TestSuite` descendant referenced by
  TASK.md. The currently authored `FB_CounterConsumer` is a
  build-smoke consumer, not a TcUnit suite.
- Runtime smoke (`deploy` → `start_runtime` → `run_tests` → `get_test_results`)
  — requires a TwinCAT runtime + `TARGET_AMS_ID` env var.

The bench machine needs the [TcUnit library](https://github.com/tcunit/TcUnit)
installed; see `bench/README.md` Prerequisites.

## Reset between bench runs

```
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/B2-sign-type
```

Path-scoped: reverts only the fixture's tracked files; the
generated `.library` and other gitignored output are left in place.
