---
adr: 0004
title: Reader cache invalidation strategy
status: Exploring
created: 2026-05-11
last_reviewed: 2026-05-18
issue: 42
pr:
related: [0005]
---

## Current state

**Decision (live):** Use `.plcproj` mtime as the staleness signal. One
`stat()` per read on the warm path; full `get_structure` rebuild on the cold
path. With multi-PLC support (ADR-0005), the reader tracks one mtime per
`.plcproj` (`dict[plc_name, float]`) and rebuilds the whole index if any of
them moves. No explicit invalidation hook, no filesystem watcher.

**Where it lives:** `tckit/adapters/readers/xml_reader.py:_refresh_index_if_stale`.
Per-`.plcproj` mtime tracking introduced under ADR-0005.

**Open questions:**
- Does mtime hold up if `remove_pou`/`rename_pou` or any future writer bypasses
  the `.plcproj` rewrite? None do today.
- Per-call `stat()` latency on Windows shares; not seen yet.

## Context

`XmlReader` keeps an in-memory `_file_index` mapping POU/GVL/DUT names to their files on disk. The index is populated by `get_structure` and consulted by every subsequent `get_pou_interface`, `get_pou_item`, `get_gvl`, `get_dut` call. Without an index those follow-up calls would have to re-scan the project tree on every MCP request.

Before issue #42, the reader was instantiated freshly on every MCP request via `TcKitConfig.reader()`. The index was therefore "fresh by accident": each request built a new one. That made the layered read pattern broken (every chained read paid a full re-scan), but it sidestepped any invalidation question. Fixing #42 caches the reader for the server's lifetime, so the index now persists across requests; that creates an invalidation surface that did not previously exist.

The reader's index holds *file paths*, not file *contents*. Body edits to a POU do not invalidate the index because we re-read the `.TcPOU` on every `get_pou_item` / `get_pou_interface`. The cases that actually invalidate the index are *structural* changes to the project: a POU added, removed, or renamed. Today there are three sources for those:

1. **TwinCAT's own writer** when XAE Shell is open against the project (out of band relative to the MCP server).
2. **External tooling** the user runs: `git checkout`, editor file-tree operations, manual filesystem moves.
3. **TcKit's `ProjectWriter`** via the `automation_writer` adapter. Today that means `add_pou` is the only structural operation; `add_method` / `update_pou_item` only touch existing POU files.

For invalidation to be useful the reader needs to react to all three, not just to (3).

## Decision

For the current writer surface and access pattern, shipped in this PR:

- Use the `.plcproj` file's mtime as a staleness signal. TwinCAT rewrites the `.plcproj` (which carries the `<Compile Include="…" />` manifest of POU files) on every structural change, regardless of who triggered it. Body-only edits do not touch `.plcproj`.
- Record `_index_plcproj` and `_index_mtime` when `get_structure` populates the index.
- On every read, `XmlReader._refresh_index_if_stale()` does one `stat()` on the recorded `.plcproj`. If the mtime has changed (or the file has disappeared), call `get_structure` again to rebuild the index, then continue with the requested read.

Cost on the warm path: one `stat()` per read. Negligible.
Cost on the cold path (post-structural-change): one full tree walk, identical to the original `get_structure` call. We pay this at most once per structural change.

## Alternatives considered

**A. Explicit invalidation hook from `ProjectWriter`.** The writer would call into a shared "invalidate" function after `add_pou` / `remove_pou`. Cleaner architecturally for in-process writes. Rejected for now: (i) adapters cannot import each other per the One Rule, so this would require routing through a port or through `TcKitConfig`, which is more wiring than the mtime guard; (ii) it would not catch external writes (XAE Shell, git checkout, manual edits) — so we would still need mtime or a watcher in addition. Revisit when in-process writes are frequent enough that waiting until the next read to re-index becomes a real latency problem.

**B. Filesystem watcher (`watchdog`).** Maximum correctness: a background thread monitors the project root, invalidates immediately on relevant events. Adds a runtime dependency, adds threading, adds platform-specific event semantics. Overkill for a single-developer-at-a-time MCP server whose reads happen every few seconds at most. Park for now.

**C. TTL-based invalidation.** Cheap to implement, but the choice of TTL is wrong both ways: short TTLs defeat the cache; long TTLs leave a stale index for too long. Rejected.

**D. Full re-scan on every read.** Always correct. This is the pre-#42 behaviour. Rejected on perf grounds; #42's whole reason for caching is to avoid this.

## Consequences

What this enables:

- The layered read pattern that `tc-orient-project` and `tc-read-project` are built around now actually composes across MCP requests. `get_structure → get_pou_interface → get_pou_item` works end-to-end.
- The cache is self-correcting on any structural change visible in `.plcproj`, whether the change came through TcKit or any external tool.

What it does not cover:

- **Zero-latency reaction to in-process structural writes.** If TcKit's writer adds a POU at request *N*, the index is rebuilt on the *next* read request rather than immediately. For today's workflow (writes are followed by builds, not by reads) this is fine.
- **Changes that do not touch `.plcproj`.** Today the reader only indexes POUs / GVLs / DUTs (`<Compile Include>`), all listed in `.plcproj`. If we later index something else (e.g. `.xti` task references, library binaries) we will need to extend the staleness signal accordingly.

What it locks us out of: nothing concrete. The mtime guard does not preclude later adding an explicit invalidation hook or a watcher; both would simply replace or augment the `stat()` call inside `_refresh_index_if_stale`.

## Open questions

1. **Does the mtime guard hold up when the writer surface grows?** Once `remove_pou`, `rename_pou`, or batch add/remove operations exist, do they all route through XAE-driven `.plcproj` rewrites, or do any of them bypass it? If any bypass `.plcproj`, the mtime alone is no longer enough.
2. **Is a per-call `stat()` actually free?** On Windows shares or unusual filesystems, `stat()` latency can spike. If we see this in the bench, switch to a coarser check (mtime sampled once per N seconds) or move to an explicit hook.
3. **Cross-project lifetimes.** Today the cached reader keys staleness against a single `.plcproj`. Calling `get_structure` against a different project simply rebuilds the index. Fine for single-project sessions; for hypothetical multi-project sessions we would need per-project sub-indices. No current pressure to build this.

## Status notes

- 2026-05-11: Drafted as `Exploring`. Ships alongside issue #42's reader-caching fix. Promote to `Proposed` (or `Implemented`, if scope holds) once the writer surface grows enough that the in-process-write latency caveat starts mattering, or once a watcher / explicit hook becomes the obviously correct next step.
- 2026-05-12: ADR-0003 implemented (patch-style writes added). Confirmed by inspection of `XmlReader` that only `_file_index` and its staleness metadata are cached; no per-POU content lives in the reader between calls. Body-only edits via `update_pou_item_patch` therefore do not require explicit invalidation, and `add_variable`'s read-modify-write goes through the bridge's own COM-level read each call. Structural writes (`add_pou`, `add_method`) continue to be caught by the existing `.plcproj` mtime guard because XAE rewrites the manifest. No code change to this ADR's mechanism; staying `Exploring` until a writer flow strains the mtime check.
