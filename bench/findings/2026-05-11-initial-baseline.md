---
date: 2026-05-11
status: Superseded
related_adrs: [0001, 0002]
superseded_by: bench/findings/2026-05-11-adr-0002-post-impl.md
---

# 2026-05-11 — Initial baseline: vanilla vs TcKit on read-only TcUnit tasks

First run of the benchmark harness. Single run per cell, exploratory.
Establishes a baseline against which ADR-0002 and ADR-0003 work can be
measured once implemented.

## Setup

- **Target project:** TcUnit at `C:/TcUnit`.
- **Configs:**
    - `empty` — vanilla Claude Code, built-in tools only.
    - `tckit` — TcKit via Docker SSE on `http://localhost:8000/sse`.
- **Tasks:**
    - `01-orient` — give a structural overview of TcUnit.
    - `02-find-callers` — find every call site of
      `FB_TestSuite.AssertEquals_INT`.
- **Model:** Opus 4.7.
- **Runs:** 1 per (task, config) pair. **N=1 is too small to draw
  strong conclusions** — these results are directional only.

## Results

| Task | Config | Tool calls | Total tokens | Wall (s) |
|---|---|---|---|---|
| 01-orient | empty | 24 | 8,549 | 140.4 |
| 01-orient | tckit | 30 | 9,865 | 150.5 |
| 02-find-callers | empty | 13 | 8,547 | 114.1 |
| 02-find-callers | tckit | 13 | 8,001 | 102.1 |

**Ratios** (vanilla ÷ tckit; higher means TcKit more efficient):

- Orient: tokens 0.87×, tool calls 0.80×, wall-clock 0.93× — **TcKit
  was 13-15% worse**.
- Find-callers: tokens 1.07×, wall-clock 1.12× — marginal TcKit win,
  within plausible noise at N=1.

## Tool breakdown

### `01-orient`

- **empty:** Bash×11, Read×6, Skill×1, ToolSearch×6 (24 calls)
- **tckit:** Bash×9, Read×9, Grep×3, Glob×1, PowerShell×2, Skill×1,
  ToolSearch×1, **`mcp__tckit__get_structure`×4** (30 calls)

The TcKit run called `get_structure` four times *and then* did nine
Reads, three Greps, nine Bash calls. The TcKit calls were added to the
exploration, not substituted for it.

### `02-find-callers`

- **empty:** Grep×11, Read×2 (13 calls)
- **tckit:** Grep×12, Read×1 (13 calls) — **no TcKit tools used**

Neither config reached for TcKit tools. Both ran on stock Grep+Read.
TcKit has no tool that directly addresses cross-file find-callers, so
this is expected behaviour.

## Findings

### 1. Tool availability is not tool adoption

The biggest result. When TcKit is registered alongside built-in tools,
Claude *samples* TcKit's structural tool (`get_structure`) but does not
commit to the layered-read chain (`get_structure` →
`get_pou_interface` → `get_pou_item`). Stock-tool exploration
continues in parallel, often dominating the call count.

The "5-15× context efficient" claim made earlier in this session was
an *architectural estimate* assuming Claude would use TcKit tools as a
**replacement pattern**. Measured behaviour shows it uses them as a
**supplement**. Hard correction: the architectural ceiling and the
behavioural floor are far apart, and the floor is what the user
experiences today.

### 2. MCP registration has a measurable fixed cost

Twenty TcKit tools register roughly 1,000+ tokens of system-prompt
overhead per turn. On short tasks where TcKit tools don't carry their
weight, this overhead alone offsets any savings. Visible in
`02-find-callers`: tokens are within 7% but TcKit's were *higher* than
vanilla on Task A despite the model using `get_structure` four times,
because the savings from those calls didn't compensate for the
registration tax plus the continued stock-tool exploration.

### 3. The right tool isn't always present

On `02-find-callers`, the model correctly used stock Grep — TcKit has
no purpose-fit tool for cross-file find-callers. This is the gap
ADR-0001's deferred Tier-B methods (`find_callers`,
`find_instantiations`) were proposed to close. Without them, TcKit
can't help on this very common workflow; the tie is essentially
"vanilla works, TcKit adds nothing here."

### 4. Existing skills loaded but did not change behaviour

Both `01-orient` runs invoked the `Skill` tool once — probably the
existing `tc-read-project` skill being pulled in by the project's
CLAUDE.md guidance. It did not visibly change the call distribution
toward the TcKit chain. **Skills that describe a workflow are not the
same as skills that enforce one.** ADR-0002's proposed
`tc-orient-project` skill needs to be more directive than
`tc-read-project` currently is — explicitly "stop after the structural
overview, do not crawl further unless the task demands it."

## What this validates and invalidates

**Validates:**

- **ADR-0002** (orient skill + extended `get_structure`) is more
  important, not less. The architectural ceiling is real only if the
  model follows a disciplined path. A directive skill that says "here
  is your structural budget; spend it, then stop" is the mechanism for
  enforcement.
- **ADR-0001's deferral of search** was right. The
  generic-`find_symbol` surface drafted there would not have helped on
  either of these tasks; targeted methods (`find_callers`,
  `find_instantiations`) would, but only on Task C. The cost-benefit
  argument for deferral holds.

**Invalidates / weakens:**

- The earlier "5-15× context-efficient" composite estimates for
  TcKit-today. On these two tasks, measured ratio is 0.8-1.1×.
  Architectural reasoning ≠ measured behaviour; the gap is large and
  consistent in the wrong direction.
- The framing in the README that positions TcKit's read tools as the
  primary defence against context rot. Today, on unfamiliar projects,
  they help only if the model commits to using them, and there is no
  current mechanism that ensures that.

## Caveats

- **N=1 per cell.** Single runs. Variance is unbounded; some of these
  numbers could swing 20-30% on re-run. Run again at N=3 before
  treating any single number as load-bearing.
- **One model (Opus 4.7).** Other models reach for tools differently.
  Haiku in particular tends to follow declared patterns more
  literally; would be worth a comparison.
- **Read-only tasks only.** Build, deploy, write to `.TcPOU` are
  invisible here. TcKit's capability gap with vanilla is largest on
  *write* — `Edit` corrupting `.TcPOU` XML is a real outcome stock
  tools produce. Those capability differences don't appear in this
  benchmark by design (Task B was deferred).
- **No `tc-orient-project` skill yet.** Once ADR-0002 lands, re-run
  Task A and measure the delta.

## Suggested next experiments

1. **N=3 baseline.** Same matrix, three runs per cell. Establishes
   stable means and reveals which ratios are real vs noise. ~$10 of
   compute.
2. **Directive-skill ablation.** Add a project-local CLAUDE.md to the
   benchmark's working directory that explicitly instructs the model
   to use TcKit's layered pattern and avoid `Read`/`Grep` on `.TcPOU`
   files. Re-run Task A. Does behaviour change? If yes, the missing
   piece is direction, not tooling.
3. **Post-ADR-0002 re-run.** After the orient skill and extended
   `get_structure` ship, run Task A with TcKit. Target: the ratio
   moves from 0.87× to ≥1.5× on tokens.
4. **Post-ADR-0003 re-run.** Add a new task that requires a small
   edit (e.g. add a comment to a method body). Compare patch-style
   write vs full-item-replace. Target: 5-10× context savings on the
   edit path.
5. **Haiku run.** Same matrix, model = `claude-haiku-4-5`. Tests
   whether smaller models follow declared tool patterns more
   literally.

## Interpretation, in one line

**Today, on read-only navigation against an unfamiliar TwinCAT
project, current TcKit ≈ vanilla Claude Code.** The architectural
foundation is in place; the missing piece is the mechanism that makes
the model use it. Direction (skills, tool descriptions, CLAUDE.md
guidance) beats more tools.
