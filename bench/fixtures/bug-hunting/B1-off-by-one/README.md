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
└── B1RollingAverage/
    ├── B1RollingAverage.tsproj
    ├── B1RollingAverage/            ← Library PLC project (first plc; named after sln)
    │   ├── B1RollingAverage.plcproj
    │   └── POUs/
    │       └── FB_RollingAverage.TcPOU
    └── RollingAverageTests/         ← Tests PLC project (sibling, added via add_plc_project)
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

## What's validated vs. deferred

**Surfaced by Phase C0 authoring (2026-05-14):**

- Two bridge bugs in the ADR-0009 surface — `New-TcProject.ps1` and
  `Add-TcPlcProject.ps1` were not suppressing the return values from
  `Solution.Create`, `Solution.AddFromTemplate`, and `Solution.SaveAs`,
  so the bridge response shape was a JSON array instead of an object
  and `to_result` blew up. Fixed in the same PR as this fixture lands.
- ADR-0009 `create_project` produces an sln + first PLC against a
  live 4026 install (after the bridge fix).

**Gated on operator-driven live smoke** (bridge must be restarted to
pick up the new ADR-0009 routes and the bug fixes above):

- ADR-0009 `add_plc_project` adds a second PLC sibling to the created sln.
- ADR-0009 `save_plc_as_library` produces a `.library` and installs it.
- ADR-0009 `add_library_reference` resolves with the default
  `distributor="Tc3 Project"` and the `TIPC^<plc>^<plc> Project^References`
  tree path.
- Consumer build against the installed library succeeds.

Run the authoring script after restarting the bridge to complete the
above and commit the produced `.sln`/`.plcproj`/`.TcPOU` files.

**Deferred** (separate follow-up before the bench is runnable):

- TcUnit library reference + `GVL_TcUnit.TcGVL` with
  `TcUnit_ResultExportXmlPath` constant. Placeholder-reference
  authoring (`AddPlaceholder`) is not yet in the writer port; for the
  bench, this can be filled in by hand in XAE once and committed, or
  a `bench/post_session.py` step can wire it.
- The `FB_RollingAverageTests` `FB_TestSuite` descendant referenced
  by TASK.md — depends on the TcUnit reference above. The currently
  authored `FB_RollingAverageConsumer` is a build-smoke consumer, not
  a TcUnit suite.
- Runtime smoke (`deploy` → `start_runtime` → `run_tests` → `get_test_results`)
  — requires a TwinCAT runtime + `TARGET_AMS_ID` env var.

## Reset between bench runs

```
git -C <repo-root> checkout HEAD -- bench/fixtures/bug-hunting/B1-off-by-one
```

Path-scoped: reverts only the fixture's tracked files, leaves the
generated `.library` artefact and any other gitignored output in
place (they get rebuilt per run anyway).
