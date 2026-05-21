# bug-hunting fixture authoring scripts

One Python script per fixture, each one drives the bridge through
the full TcKit MCP-tool chain to produce the committed
`.sln`/`.plcproj`/`.TcPOU` shape from scratch.

These are *authoring* scripts, not part of the bench-run loop. The
bench reset replays the committed fixture state via
`git -C <repo-root> checkout HEAD -- <fixture-path>`; the scripts
exist to *regenerate* that committed state when the seeded bug
needs to change or when a new fixture is added.

## Prerequisites

- Bridge service reachable at `$BRIDGE_URL` (default `localhost:8765`).
- TwinCAT 4026 install + TcXaeShell.
- An XAE session open (or `XAE_MODE=headless`) — the bridge will swap
  the active solution while authoring, so save any in-progress work
  first.

## Running

```
python bench/fixtures/bug-hunting/_author/author_<id>.py [--force]
```

`--force` clears any previously-generated tree in the fixture
directory before re-authoring; the static support files
(`CLAUDE.md`, `TASK.md`, `README.md`) are kept.

After a clean authoring run, inspect the produced tree and commit
the generated `.sln`/`.plcproj`/`.TcPOU` files. The `.library`
artefact is gitignored — it gets rebuilt per bench run.

## End-to-end smokes

Standalone scripts that exercise specific bridge -> COM paths
against a throwaway project. Run after touching the bridge or any
of the wire-protocol code; they catch regressions the unit tests
can't see.

| Script                            | Covers                                                       | Needs bridge + XAE |
|-----------------------------------|--------------------------------------------------------------|--------------------|
| `smoke_property.py`               | `add_property`, `add_dut` (ADR-0012)                         | yes                |
| `smoke_deletes.py`                | `delete_pou` / `delete_method` / `delete_property` / `delete_gvl` / `delete_dut` / `delete_variable` / `delete_folder` (ADR-0013 waves 1-3) | yes                |
| `smoke_library_deletes.py`        | `delete_library_reference`, `delete_placeholder` (ADR-0013 wave 4); also probes the orphan `<Parameters>` open question | yes                |
| `smoke_folders.py`                | `add_folder` + `parent_folder` on every `add_*` (ADR-0013 wave 5) | yes                |
| `smoke_reader_symmetry.py`        | ALIAS DUT classification + `GVLRef`/`DUTRef` folder paths (ADR-0013 wave 6) | no (offline)       |

Each smoke uses its own `_smoke-*` fixture directory under
`bench/fixtures/` and prints a cleanup command at the end. The
library smoke also leaves an installed library in the system repo
that needs manual removal from XAE's Library Repository view.
