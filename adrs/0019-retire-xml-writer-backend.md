---
adr: 0019
title: Retire the deterministic XML writer backend
status: Accepted
created: 2026-08-24
last_reviewed: 2026-08-24
issue:
pr:
supersedes: [0017]
superseded_by:
related: [0015]
---

## Current state

**Decision (live):** `IProjectWriter` has one backend again: the Automation
Interface. The deterministic XML writer of ADR-0017 (`XmlProjectWriter` and
the file-format layer beneath it), the `TCKIT_WRITER` / `--writer` /
`TCKIT_SOLUTION` selection machinery, the Linux CI write-integration step,
and `oracle/parity-writer.ps1` are removed. Structural writes are
Windows-with-XAE only, matching the other COM lanes. The reader and
`TcKit.Core/Authoring` primitives (used by the automation lane) are
untouched, as are the Linux release artefacts (reader, analysis, ADS).

**Where it lives:** This PR. The removed backend's design and parity
evidence stay recorded in ADR-0017 and `dotnet/PORTING.md`.

## Context

ADR-0017 added a second `IProjectWriter` so writes could run off Windows.
It reached full parity (28 verbs, 0 diverged on a live 4026), and was then
ported wholesale to a standalone Rust tool that lives next to the PLC
sources it edits, adds pre-write ST validation TcKit never had, and pins
XAE's byte shapes with golden tests. That left two maintained
implementations of byte-identical write semantics behind one port — drift
between them being most dangerous exactly where parity matters most, which
is the same argument ADR-0017 used against duplicating the shared
primitives.

## Decision

Keep exactly one writer per environment: the external XML tool off Windows,
TcKit's Automation Interface backend on Windows against a live XAE. TcKit
drops its XML writer rather than freezing it, so no future verb has to be
implemented twice or can silently diverge.

Removal is total rather than a deprecation: the backend selection surface
(`TCKIT_WRITER`, `--writer`, `TCKIT_SOLUTION`, `--sln` seeding), the
xml-lane tests and CI integration step, and the parity oracle all go with
it. The writer port keeps its platform guard shape from the other COM
lanes: first use off Windows raises `PlatformNotSupportedException` with a
clear message instead of a DI activation error.

## Alternatives considered

- **Freeze the xml backend in place:** still two copies of the write
  semantics; "frozen" code behind a live port rots silently until someone
  flips `TCKIT_WRITER=xml`.
- **Keep only the parity oracle:** it drives both backends through the CLI,
  so it cannot outlive the xml lane it exists to promote.
- **Extract the xml backend as its own package:** that is exactly what the
  external port is; doing it twice recreates the drift problem.

## Consequences

- TcKit writes need Windows + XAE again; Linux/CI hosts get read, analysis,
  docs, and ADS lanes only. Off-Windows authoring is explicitly someone
  else's job.
- The Linux CI write-integration coverage is gone; the automation lane's
  live smoke (`oracle/smoke-writer.ps1`) remains the write gate.
- ADR-0017's open question (template scaffolding on the xml backend) closes
  as moot.

## Status notes

- 2026-08-24: Drafted and accepted; removal PR opened alongside.
