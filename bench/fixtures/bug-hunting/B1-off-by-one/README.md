# B1 off-by-one fixture

ADR-0007 §"B1 off-by-one". Pilot fixture for the bug-hunting bench.
Authored entirely through TcKit's MCP tools — no hand-edited XML —
to exercise the ADR-0009 multi-PLC + library-tools chain end-to-end.

## Layout

```
B1-off-by-one/
├── CLAUDE.md                        ← copy of templates/twincat-claude.md
├── TASK.md                          ← bench prompt for both arms
├── README.md                        ← this file
├── B1RollingAverage.sln
├── B1RollingAverage.tspproj
├── B1RollingAverage/                ← Library PLC project (first plc; named after sln)
│   ├── B1RollingAverage.plcproj
│   └── POUs/
│       └── FB_RollingAverage.TcPOU
└── RollingAverageTests/             ← Tests PLC project (sibling, added via add_plc_project)
    ├── RollingAverageTests.plcproj
    └── POUs/
        └── FB_RollingAverageConsumer.TcPOU
```

The `.library` artefact (`B1RollingAverage.library`) is gitignored
and regenerated per run by `save_plc_as_library`.

## What the seeded bug is

`FB_RollingAverage.Step` sums the ring buffer with
`FOR i := 1 TO sampleCount DO`. The buffer is indexed `0..sampleCount-1`,
so the loop misses index 0 and reads past the end into the
zero-initialised tail. The correct loop is `FOR i := 0 TO sampleCount - 1 DO`.

For a stream of eight 10s, the buggy `Step` returns 8 instead of 10.

## Regenerating the fixture

```
python bench/fixtures/bug-hunting/_author/author_B1.py [--force]
```

`--force` wipes the generated tree (keeping CLAUDE.md, TASK.md, and
this README) before re-authoring. Requires the bridge service at
`$BRIDGE_URL` and a TwinCAT 4026 install.

## What's validated

Phase C0 authoring exercised the full ADR-0009 chain end-to-end on
2026-05-14:

- `create_project` → sln + first PLC.
- `add_plc_project` → second PLC sibling.
- `add_pou` + `add_method` in both PLC projects.
- `save_plc_as_library` → `.library` produced and installed in the
  System repo with Title=PlcName, Company="Tc3 Project", Version=1.0.0.0.
- `add_library_reference` with the default distributor `Tc3 Project`
  and `References` tree-item path — the reference lands in the
  consumer's `.plcproj` on disk (`<LibraryReference Include="B1RollingAverage,newest,Tc3 Project">`).
- `build` on the Tests PLC resolves the library reference against
  the installed library and completes clean.

That validates the two spike-by-implementation defaults documented
in ADR-0009. Six bridge bugs were caught and fixed along the way
(see PRs #73 and #74).

## End-to-end runtime smoke

`author_B1.py` now also generates `FB_RollingAverageTests` (the
`FB_TestSuite` descendant TASK.md promises) with the failing
`AverageOfConstantStream` test, and a Tests-PLC `MAIN` body that
instantiates the suite and calls `TcUnit.RUN()` cyclically. A live
runtime smoke driver lives at
[`_author/smoke_B1.py`](../_author/smoke_B1.py); it chains
`save_plc_as_library` → `build` → `deploy` → `start_runtime` →
`run_tests` → patch via `update_pou_item_patch` → re-run and asserts
red → green. With the bridge running and `TARGET_AMS_ID` set:

```powershell
$env:TARGET_AMS_ID = "127.0.0.1.1.1"
python bench/fixtures/bug-hunting/_author/smoke_B1.py
```

Pass/fail is read directly from PLC symbols
(`MAIN.suite.Tests[1].TestIsFailed`) so the smoke doesn't depend on
TcUnit's xUnit XML publisher, which is disabled by default
(`GVL_Param_TcUnit.xUnitEnablePublish := FALSE`).

The bench machine also needs the [TcUnit library](https://github.com/tcunit/TcUnit)
installed in the system library repository (distributor
`www.tcunit.org`); see `bench/README.md` Prerequisites.

## Reset between bench runs

```
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/B1-off-by-one
```

Path-scoped: reverts only the fixture's tracked files, leaves the
generated `.library` artefact and any other gitignored output in
place (they get rebuilt per run anyway).
