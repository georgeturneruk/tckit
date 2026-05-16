---
adr: 0005
title: Multi-project sln support
status: Implemented
created: 2026-05-12
issue:
pr:
---

## Context

TcKit currently assumes one PLC project per session. Two visible
limits:

- `bridge/harness/_TcDte.psm1`'s `Resolve-TcPlcName` throws when the
  solution contains more than one PLC project unless the caller
  passes `-PlcName` to disambiguate (line 172 today).
- `tckit/adapters/readers/xml_reader.py:86` sorts `*.plcproj` by
  depth and uses only the shallowest. Other `.plcproj` files in the
  same tree are silently ignored.

The W-series benches happened to target TcUnit (a single-PLC
solution), so the limitation was invisible. The bug-hunting bench
(ADR-0007) needs the library + test split that is idiomatic in
TwinCAT projects: one `.plcproj` for the code under test, one
`.plcproj` for the TcUnit harness, both referenced by a single
`.sln`, with a linked-library reference from test to library.
Without multi-project support, that bench cannot be authored.

Two further use cases that are real but not driving this ADR:
operators with realistic codebases that already split library and
application into separate PLC projects, and TwinCAT's own
"include library source" pattern where a shared library project
lives next to its application consumers.

## Decision

Every project-scoped MCP tool gains an optional `plc_name` parameter
with default `None`. The default means "use the only PLC project in
the solution; raise a clear error if there is more than one and no
name was given". This mirrors the bridge harness's existing
`Resolve-TcPlcName` behaviour, which is correct as-is; the work is
in the Python adapter layer and the MCP signatures.

### Tools that gain `plc_name`

- **ProjectReader**: `get_structure`, `get_pou_interface`,
  `get_pou_declaration`, `get_pou_item`, `get_gvl`, `get_dut`.
- **ProjectWriter**: `add_pou`, `add_method`, `update_pou_item`,
  `update_pou_item_patch`, `add_variable`. `open_project` and
  `create_project` stay solution-scoped (no PLC name needed).
- **BuildRunner**: `build`, `deploy`. `start_runtime` stays
  solution-scoped.
- **TestRunner**: `run_tests`, `get_test_results` (defined here so
  ADR-0006 can call them through cleanly).

Signature shape:

```python
def get_pou_interface(
    self,
    pou_name: str,
    *,
    plc_name: str | None = None,
) -> POUInterface: ...
```

Keyword-only to keep positional call sites stable.

### Session-wide override

The `PLC_PROJECT_NAME` env var is already referenced in
`tckit/config.py` but currently unused. Promote it: when set, it
becomes the default for `plc_name` on every call that does not
pass one explicitly. Per-call `plc_name` always wins over the env
default. The bench's `tckit.json` config can set this to lock a
single-session run to one PLC project even in a multi-PLC sln.

### XmlReader internals

Replace the "shallowest `.plcproj`" heuristic with a
`{plc_name: plcproj_path}` map built at index time:

```python
self._plcproj_by_name = {
    parse_plcproj_name(p): p
    for p in sorted(root.rglob("*.plcproj"))
}
```

`_file_index` becomes nested: `dict[plc_name, dict[symbol_name, Path]]`.
`get_structure(project_path, plc_name=None)` scopes its walk to the
named PLC's folder when provided, or scans every PLC and returns a
structure that explicitly groups POUs/GVLs/DUTs by `plc_name`.

The mtime guard from ADR-0004 keeps watching the parent solution's
modification time (which moves when any `.plcproj` is edited).
Cache invalidation triggers per the existing rule; the new keyed
structure does not need finer-grained invalidation because typical
edits touch one PLC at a time and the cost of rebuilding the full
map is low.

### POU name resolution

Today, `get_pou_interface(pou_name)` looks up `pou_name` in a flat
`_file_index`. With two PLC projects, a POU name could exist in
both (the library defines `FB_Filter`, the test project's TcUnit
suite defines `FB_FilterTests`, but a project might genuinely have
`FB_State` in both). Resolution rule:

1. If `plc_name` is given, look up only in that PLC.
2. Otherwise, look up in the env-default PLC if set.
3. Otherwise, if the symbol is unique across all PLCs, return it.
4. Otherwise, raise with an ambiguous-symbol error naming the
   PLC projects that contain it.

Same logic for GVLs and DUTs.

### Bridge

No changes. The PowerShell harness already accepts `-PlcName` and
resolves correctly. The Python adapters just pass `plc_name`
through to the bridge body where they previously omitted it.

### `get_structure` response shape

When `plc_name=None` on a multi-PLC sln, `get_structure` returns
the same `ProjectStructure` it does today but with POUs grouped
by `plc_name` in a new `plcs: dict[str, ...]` mapping rather than a
flat list. Tools that want the flat-list shape can iterate. This
is a breaking change to the response on multi-PLC projects only;
single-PLC projects keep their current shape (the dict has one
entry and `POURef` consumers don't need to change).

## Alternatives considered

- **Stateful "current PLC" set by an MCP call.** Rejected: hidden
  state across multi-step flows, fragile when a session bounces
  between PLCs.
- **Implicit "guess from POU name uniqueness".** Rejected: name
  collisions between library and test projects are realistic
  (`E_State`, `FB_Util`, etc.), and a guess that picks one silently
  is worse than an explicit error.
- **Two separate MCP servers, one per PLC.** Rejected: operator
  complexity that defeats the point. Bench harness would need to
  juggle two SSE endpoints; tools would still need to know which
  server to call.
- **Solution-wide reads, PLC-scoped writes.** Tempting because
  reads are the common case. Rejected because the response shape
  needs the PLC label to be useful (Claude needs to know "this POU
  lives in the library, that one lives in the tests"), at which
  point the parameter for scoping is essentially free.

## Consequences

**Enables:** library + test split, the bug-hunting bench (ADR-0007),
operator workflows with multi-PLC solutions.

**Costs:** every project-scoped MCP tool gains one optional
keyword argument. 99% of callers ignore it. The `ProjectStructure`
return shape changes on multi-PLC projects (additive on single-PLC).

**Locks out:** nothing. The single-PLC default keeps existing
behaviour intact. Future ADRs can add solution-wide operations
without revisiting this one.

**Risks:** ambiguous-symbol errors will surface in real downstream
use. The error message must name which PLCs contain the symbol so
the user can disambiguate without re-reading the project. The
fallback rule (1-2-3 above) is deliberately deterministic; no magic.

## Status notes

- 2026-05-12: Drafted as `Proposed`. Implementation lands as a
  dedicated PR before ADR-0006 (TestRunner) so the testrunner can
  rely on `plc_name` from day one.
- 2026-05-12: Implemented. Notable deviations from the original
  draft:
  - **ProjectStructure shape (breaking change).** The draft kept the
    flat `pous`/`gvls`/`duts` lists on single-project sln results and
    introduced `plcs` only for multi-project ones. Implementation
    replaces the flat lists with a single
    `plcs: dict[str, PLCSection]` mapping in every case; the
    single-project sln returns a one-entry dict. `libraries` moved
    onto `PLCSection` because library references live per `.plcproj`.
    `tasks` stays at the solution level because TwinCAT tasks are
    sln-wide. `POURef` gained a required `plc_name` field.
  - **Per-plcproj mtime guard.** ADR-0004 watched the parent sln's
    mtime as a single staleness signal. With multi-project support,
    the reader tracks one mtime per `.plcproj` (a
    `dict[plc_name, float]`) and rebuilds the whole index if any
    single `.plcproj` moves. Watching the sln file alone would miss
    edits to a single PLC project that don't bump the sln.
  - **Doc generator bundled in this PR.** The doc adapter walks the
    filesystem directly (independent of `XmlReader`) and would
    otherwise silently merge POUs from every `.plcproj` into one
    flat undifferentiated docs site, with cross-references that
    wrongly span PLC projects. `_doc_model.ProjectDoc` now carries
    `plcs: dict[str, PLCDoc]`; HTML and Markdown output is sectioned
    under `<output>/<plc_name>/<object>.{html,md}` with a top-level
    solution index. `_compute_used_by` is scoped within a PLC project.
  - **Shared resolver.** `tckit/utils/plc_resolver.py` (`resolve_plc_name`)
    centralises the 1-2-3-4 fallback for writer/builder/test-runner
    callers; the reader uses a symbol-aware variant inline that
    prefers the unique-symbol fallback over an "any PLC project"
    auto-resolve (matches ADR Decision section "Resolution rule").
  - **TestRunner gained `target_ams_id`.** The original ADR text
    listed only `plc_name` on the TestRunner methods. The IDE
    workflow requires picking both a PLC project and a target route
    when running tests, and implicit "last deployed target" state
    would be brittle across MCP calls. ``run_tests`` /
    ``wait_complete`` / ``get_results`` now take ``target_ams_id``
    as the first positional argument (matches ``BuildRunner.deploy``
    shape). Captured in ADR-0006 Status notes too.
