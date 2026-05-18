---
date: 2026-05-18
status: Current
related_adrs: [0011, 0007]
---

# 2026-05-18 — T1 friction fixes plus skill nudges (n=1 per shape)

Followup to [2026-05-17](2026-05-17-adr-0011-impl-and-t1-rebench.md).
The previous re-bench measured the self-validating run at 23 calls
fighting bridge / path / NetId friction. This round removes that
friction and clarifies the skill text around the cleared-to-proceed
path so the model decides to self-validate in the first place.

## What landed

1. **`build` adapter validates `project_path`** before the bridge
   round-trip. Empty / missing / directory / wrong-extension all return
   a structured `BuildError` naming the path and (for directories) the
   `.sln` / `.tsproj` files actually present. Replaces the bridge's
   raw `STG_E_FILENOTFOUND` with an empty file field.
2. **`target_ams_id` falls back to env / config.** `deploy`,
   `start_runtime`, and `run_tests` now accept an empty arg and resolve
   via `TARGET_AMS_ID` env or `~/.tckit/config.toml`. Unresolved cases
   return a clear "set it via X / Y / Z" error. Safety gate ordering
   unchanged.
3. **`ProjectStructure.solution_path`** carries the resolved absolute
   `.sln` path the reader walked to during `get_structure`. Lets a
   follow-up `build()` pull `project_path` from a known-good field
   instead of guessing from cwd. Additive field; doc generators don't
   consume `ProjectStructure`.
4. **`tc-build-test-loop` skill** mentions `plc_name=<consumer>` on the
   first multi-PLC build, names where `target_ams_id` is sourced, and
   adds a "When the gate doesn't fire" paragraph for the bypass case
   so a plain success after `deploy` reads as cleared-to-proceed rather
   than an ambiguous outcome.
5. **`tc-write-st` skill** drops the "verification builds are
   bench-noise / the harness verifies" framing. That language conflated
   re-reading a write (genuine noise) with running the test cycle the
   user asked for (the actual job). Hand-off is now driven by what the
   user asked for, not by a baked-in bench assumption.

Plus the T1 bench `TASK.md` dropped its "harness handles validation
between iterations" sentence, since that explicitly told the model
not to self-validate.

## Numbers

| Run | Calls | Tokens | Wall (s) | Shape |
|---|---:|---:|---:|---|
| 2026-05-17 baseline (self-validate, fighting friction) | 23 | 5,652 | 134.0 | self-validate |
| Post-friction-fix only | 12 | 2,369 | 90.2 | hand-off |
| Post-skill-nudge | 19 | 4,042 | 107.8 | self-validate, 3 builds |
| Post-solution_path | 9 | 1,983 | 68.7 | hand-off |
| Vanilla reference (this round) | 6-11 | 1,500-3,500 | 40-70 | edit only |

Friction-conditional-on-self-validate dropped from 23 calls / 5,652
tokens / 134s to 19 / 4,042 / 107.8. The 3 builds inside that 19-call
run were `dir` → `.sln` without `plc_name` → `.sln` + `plc_name`; the
two miss-shoots are recovered by actionable errors at no path-search
or env-discovery cost. `solution_path` should cut the first
miss-shoot, but the only post-`solution_path` trial picked hand-off
so the build count isn't measurable yet.

## Open

The model picks self-validate vs hand-off non-deterministically across
otherwise-identical runs. Two consecutive n=1 trials after the skill
update split: 19-call self-validate then 9-call hand-off. The variance
is on the *decide-to-validate* axis, not the *cost-once-validating*
axis. An n=3 sweep would characterise the distribution; the friction
fixes pay off conditional on the model deciding to validate, and
nothing in the current change set degrades the hand-off case.

## Caveats

- n=1 in each shape, one model (Opus 4.7), one machine.
- Vanilla call count drifted 6-11 across the four runs of this round.
  Single-trial vanilla numbers carry the usual model-variance noise.
- The build-call-count win from `solution_path` is a hypothesis until
  the next self-validating trial.
