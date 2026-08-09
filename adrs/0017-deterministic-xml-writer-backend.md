---
adr: 0017
title: Deterministic XML writer backend for IProjectWriter
status: Accepted
created: 2026-08-09
last_reviewed: 2026-08-09
issue:
pr:
supersedes:
superseded_by:
related: [0004, 0005, 0013, 0015]
---

## Current state

**Decision (live):** Ship a second `IProjectWriter` implementation that
deterministically edits the TwinCAT project XML on disk (.TcPOU / .TcGVL /
.TcDUT / .plcproj), selected per session via `TCKIT_WRITER=automation|xml`
(default: automation on Windows, xml elsewhere; never a per-call fallback).
The Automation Interface stays the preferred backend and becomes the oracle:
a live parity harness runs each verb through both backends and diffs
canonicalised trees. Shared primitives (StCode, VarBlock, PatchText,
TaskBinding, TcKind, PlcProjXml) live in `TcKit.Core/Authoring`; the writer
joins the reader in `TcKit.Adapters.Xml` (renamed from
`TcKit.Adapters.Reader`) so both share one file-format layer.

**Where it lives:** All implemented on the feature branch: `TcKit.Core/Authoring/`
(shared primitives), `TcKit.Adapters.Xml` (XmlProjectWriter + TcPlcObjectFile /
PlcProjFile / TcXmlFormat / GuidSource / SolutionContext), backend selection in
`TcKit.Server/Program.cs` + `TcKit.Cli` (`--writer` / `--sln`), the net8.0
retarget with the multi-targeted COM lane, linux-x64 release artefacts, a Linux
CI integration step, and `dotnet/oracle/parity-writer.ps1`. Interfaces are
emitted as `.TcIO` and the reader now indexes them. **The promotion gate has
passed on a live 4026: the full parity sweep is green (28 verbs, 0 diverged),
and XAE opens and compiles (CheckAllObjects, zero errors/warnings) a project
authored entirely by the xml backend, LineIds absent.**

**Open questions:**

- Whether `CreateProject`/`AddPlcProject` can scaffold from embedded
  templates in a later phase (v1 fails explicitly).
- ParameterGuard state is per process, so CLI-per-verb automation usage
  loses spliced parameter blocks on the next verb's save (found by the
  parity harness; the long-lived MCP server is unaffected). Worth a
  follow-up: persist guard registrations, or re-verify from disk.

## Context

Every write verb routes through the Automation Interface: it needs a running
TcXaeShell, so writes are impossible on Linux, in CI, and in headless
ADR-0007 bench runs. The reader has been file-based from the start; the file
formats are stable, fixture-covered XML, and the once-forbidden direct-XML
lane already has a sanctioned precedent (`PlcProjXml`, the placeholder
parameter splice). What blocked a file-based writer historically was
verification: no way to prove the emitted XML matches what XAE writes. With
the automation backend complete and live-validated (`smoke-writer.ps1`),
that gap closes — XAE itself can arbitrate, verb by verb.

Constraints that shaped the design: adapters may only reference `TcKit.Core`
(the one rule); the reader invalidates its cache on `.plcproj` mtime only
(ADR-0004) and XAE loads from `<Compile Include>` items, so structural
writes must rewrite the owning `.plcproj`; XAE regenerates files from its
stale in-memory tree on the next save, so backends must never interleave
within a session; CI already builds and tests on ubuntu, so the writer is
Linux-testable before any TFM retarget.

## Decision

Add `XmlProjectWriter : IProjectWriter` to `TcKit.Adapters.Xml`, mirroring
the automation lane's split: a thin shell (solution state, verb
serialisation, exception-to-Result mapping) over a static
`XmlProjectAuthor`, on top of an internal file-format layer
(`TcPlcObjectFile`, `PlcProjFile`, `TcXmlFormat`, `GuidSource`).

Key points, in decreasing order of importance:

1. **Parity oracle over trust.** `oracle/parity-writer.ps1` scaffolds a
   scratch solution, clones it, drives every verb through both backends via
   the CLI, canonicalises (strip Id GUIDs and LineIds, drop
   ProjectExtensions, sort ItemGroup children, normalise BOM/EOL) and diffs
   after every verb. A verb is promoted only when parity holds.
2. **Session-scoped backend selection.** `TCKIT_WRITER` resolved once at
   startup in the Server's DI factory and via `--writer` in the CLI.
   Falling back per call would strand half a write sequence in each
   backend's view of the project.
3. **plcproj is part of every structural write.** Add/delete of a POU, GVL,
   DUT, or folder rewrites Compile/Folder items — required by XAE and it
   bumps the mtime the reader's cache watches. Body-only edits touch only
   the object file.
4. **Determinism spec.** New files: UTF-8 BOM, CRLF, two-space indent,
   `TcPlcObject Version="1.1.0.1"`, CDATA always present. Edits: minimal
   diff via `XmlDocument PreserveWhitespace=true`, reproducing the file's
   existing BOM/EOL. ST containing `]]>` is refused. LineIds omitted on new
   files, untouched on existing ones.
5. **Scope.** All object/library verbs supported. `SavePlcAsLibrary` fails
   explicitly (needs the compiler). `CreateProject`/`AddPlcProject` fail
   explicitly in v1; embedded-template scaffolding is a later phase gated on
   parity evidence. `AddLibraryReference` cannot validate the library is
   installed — the error surfaces at build time.
6. **Runs-on-Linux is designed, not accidental.** Retarget to plain net8.0
   with `net8.0;net8.0-windows` multi-targeting for
   Automation/Server/Cli (UseWPF pins the UIA lane to the windows TFM),
   `[SupportedOSPlatform("windows")]` on the COM/STA lane, fail-fast
   registrations for COM-backed ports off-Windows, and linux-x64 release
   artefacts.

The agent-facing rule "never edit .TcPOU/.plcproj XML directly" survives
unchanged: it forbids bypassing the writer tools, not a particular backend
behind them.

## Alternatives considered

- **Sibling `TcKit.Adapters.XmlWriter` project:** forbidden from reusing the
  reader's `TcFileParser` by the one rule; duplicating it undermines
  read/write consistency. Folding into one on-disk adapter matches
  "adapter = external system" (the Automation adapter already implements
  four ports).
- **Duplicate the shared primitives per adapter (DocGen precedent):** the
  patch and split semantics must be byte-identical across backends; two
  copies would drift exactly where parity matters most.
- **Automation-with-XML-fallback per call:** XAE's stale in-memory tree
  regenerates files on its next save, silently reverting interleaved file
  edits; this is the failure ParameterGuard exists to repair.
- **Keep net8.0-windows everywhere and publish win-only:** works in CI by
  accident (`EnableWindowsTargeting`), but never yields a Linux-runnable
  release artefact.

## Consequences

Enables: writes on Linux/CI with no TwinCAT install; unit-testable authoring
against temp dirs; headless bench authoring; fast writes (no COM round
trips); the parity harness doubles as a regression net for the automation
backend itself.

Costs: a second implementation to keep in lockstep as the port grows (the
parity harness is the mitigation); no XAE-side validation on the xml path —
name clashes, bad types, and uninstalled libraries surface at build time
instead of write time; scaffolding and save-as-library remain
Windows/XAE-only in v1.

Locks out: nothing — the automation backend remains the default and the
oracle.

## Status notes

- 2026-08-09: Drafted as Proposed. Groundwork landed alongside: shared
  primitives promoted to `TcKit.Core/Authoring`, `TcKit.Adapters.Reader`
  renamed to `TcKit.Adapters.Xml`, and `ResolvePlcName` now honours
  `PLC_PROJECT_NAME` (reader-consistent: only when it names a PLC in the
  open solution).
- 2026-08-09 (later): Full implementation on the feature branch: writer,
  backend selection, net8.0 retarget + linux-x64 artefacts, Linux CI
  integration step, parity harness. 522 tests green; CLI xml-backend
  sequence verified locally against a B1 fixture copy. Promotion to
  Accepted/Implemented gates on the live parity sweep on the bench box.
- 2026-08-09 (live sweep): Promoted to Accepted. Three sweep iterations on
  a live 4026: first run 14/28 diverged, all but two from one root cause
  (property shapes; XAE emits `PROPERTY PUBLIC`, `Name="Get"/"Set"` on
  accessors, and a `PUBLIC` + empty VAR accessor declaration). Second run
  isolated the library lane: XAE records a `*` version as `newest` in
  LibraryReference Includes, keeps LibraryReferences in their own
  ItemGroup, and drops an emptied group. Third run: 28 in parity, 0
  diverged. Live findings beyond the writer: XAE happily opens and builds
  files without LineIds, and the ParameterGuard per-process gap above.
  Implemented (+ `pr:`) once the PR merges.
