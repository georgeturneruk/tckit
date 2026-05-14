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
