# Bench findings

Post-hoc records from bench rounds and post-implementation reviews. Each
finding is dated and tied (where applicable) to an ADR it validated, refined,
or contradicted.

**Status** is one of:
- `Current`: still reflects reality.
- `Superseded`: a later finding or ADR walked the result back. The
  `superseded_by:` frontmatter field names the replacement.
- `Stale`: predates a major change with no explicit successor yet. Treat the
  measurements as historical only.

| Date       | Title                                                                 | Status     | Related ADRs |
|------------|-----------------------------------------------------------------------|------------|--------------|
| [2026-06-28](2026-06-28-csharp-rewrite-feasibility.md) | C#/.NET rewrite feasibility spike (COM from net8, dependency stack) | Current | 0015 |
| [2026-05-21](2026-05-21-t2-attempt-add-property-fix-and-popup-detection.md) | T2-pid bench attempt: `add_property` end-to-end fix and XAE popup detection | Current | 0007, 0012 |
| [2026-05-18](2026-05-18-t2-pid-anti-windup-seed.md)                | T2 PID anti-windup TDD pair (seed)                                    | Current    | 0007, 0008, 0012 |
| [2026-05-18](2026-05-18-t1-friction-fixes-and-skill-nudges.md)     | T1 friction fixes plus skill nudges                                   | Current    | 0011, 0007       |
| [2026-05-17](2026-05-17-adr-0011-impl-and-t1-rebench.md)           | ADR-0011 fixes landed, T1 re-benched (n=1)                            | Superseded | 0011, 0007       |
| [2026-05-16](2026-05-16-t1-schmitt-trigger-pair.md)                | T1 Schmitt-trigger TDD pair (n=1, isolated)                           | Superseded | 0007, 0011       |
| [2026-05-16](2026-05-16-b1-bench-harness-tckit-smoke.md)           | B1 bench harness end-to-end + n=1 pair                                | Current    | 0007, 0010, 0011 |
| [2026-05-12](2026-05-12-writer-bench-wrap-up.md)                   | Writer-bench wrap-up (post-fix W2/W3 re-smoke)                        | Current    | 0003             |
| [2026-05-12](2026-05-12-writer-bench-w2-w3-smoke.md)               | Writer-bench W2 + W3 smoke                                            | Superseded | 0003             |
| [2026-05-12](2026-05-12-writer-bench-harness-w1-smoke.md)          | Writer-bench harness + W1 smoke                                       | Current    | 0003             |
| [2026-05-11](2026-05-11-subjective-quality-review.md)              | Subjective quality review of the post-#42 / post-#46 bench outputs    | Current    | 0002             |
| [2026-05-11](2026-05-11-post-issue-42.md)                          | Post-#42: reader cache, mtime guard, refocused task set               | Current    | 0002, 0004       |
| [2026-05-11](2026-05-11-adr-0002-post-impl.md)                     | Post-ADR-0002: orient skill + extended `get_structure`                | Superseded | 0002             |
| [2026-05-11](2026-05-11-initial-baseline.md)                       | Initial baseline: vanilla vs TcKit on read-only TcUnit tasks          | Superseded | 0001, 0002       |

## Why Superseded

- `2026-05-17-adr-0011-impl-and-t1-rebench.md`: Numbers superseded by
  `2026-05-18-t1-friction-fixes-and-skill-nudges.md`; T1 was re-benched again
  after the friction fixes landed.
- `2026-05-16-t1-schmitt-trigger-pair.md`: The 9x vanilla-wins gap was driven
  by a UmRT path-resolution bug; ADR-0011 closed it, see the 2026-05-17 and
  2026-05-18 follow-ups. The fixture-authoring notes and hysteresis findings
  remain useful.
- `2026-05-12-writer-bench-w2-w3-smoke.md`: Three fixes (convention-aware
  placement, skill verification rule, prompt trim) landed the same day and the
  numbers were re-measured in `2026-05-12-writer-bench-wrap-up.md`.
- `2026-05-11-adr-0002-post-impl.md`: Measured against a TcKit with the #42
  reader cache still broken; `2026-05-11-post-issue-42.md` is the post-fix
  read.
- `2026-05-11-initial-baseline.md`: Captured a TcKit-in-Docker that silently
  failed every MCP call (issue #43); post-impl runs measured native Windows.

## Reading order

1. This index, top-down (newest first). Note dates and status.
2. Anything marked `Current` for the ADR you care about.
3. Anything marked `Superseded`: skim only if you need the journey or the
   measurement methodology, not the numbers themselves.

## Maintenance

When a finding is added, the row goes at the top of the table (newest first).
When a later finding or ADR walks a result back, mark this finding
`Superseded` in its frontmatter, set `superseded_by:`, and update the
"Why Superseded" section above with a one-liner explaining what changed.
