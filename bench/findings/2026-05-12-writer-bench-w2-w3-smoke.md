# 2026-05-12 — Writer-bench W2 + W3 smoke

Follow-up to the W1 smoke that landed alongside the writer-bench harness (#53). W2 adds a `VAR_INPUT` to `FB_TestSuite`; W3 adds a new method to the same FB. Same harness, same target, same model (Opus 4.7), N=1 per (task, config). The findings tested two specific predictions from the W1 doc and surfaced one structural finding about `add_variable`'s placement behaviour.

## What changed since W1

1. **Two new task files** (`bench/tasks/W2-add-variable.md`, `bench/tasks/W3-add-method.md`). Both follow the W1 prompt-trim discipline: no explicit "verify your change" ask.
2. **`open_project` description tightened** in `tckit/server.py`. New wording tells the model the bench harness pre-opens and the call is rarely needed mid-session, while preserving the idempotency note. Test of W1 finding #5.
3. **No harness changes.** W2 and W3 plug straight into the existing `bench/tasks/*.md` discovery; `bench/run.py` and `bench/aggregate.py` are unchanged.

## Setup

- **Target:** `C:/TcKit-bench/TcUnit-writer/` (same fresh clone of TcUnit used for W1; `origin` removed for push safety).
- **Configs:** `empty` (vanilla Claude Code) and `tckit` (TcKit SSE on `http://localhost:8000/sse`).
- **Bridge:** native PowerShell, `XAE_MODE=headless`.
- **TcKit MCP server:** native, `python -m tckit.server --transport sse`, `PLC_PROJECT_PATH` set, restarted post-docstring-tighten so the model sees the new `open_project` description.
- **Model:** Opus 4.7.
- **Runs:** 1 per (task, config). **N=1, directional only.**
- **Reset between runs:** `git -C C:/TcKit-bench/TcUnit-writer reset --hard HEAD && git ... clean -fd`.
- **Build verification:** `POST /build` after each run.
- **cwd isolation:** every `claude -p` runs from the target project.

## Results

| Task            | Config | Tool calls | Total tokens | Wall (s) | Build |
| --------------- | ------ | ---------- | ------------ | -------- | ----- |
| W2-add-variable | empty  | 5          | 1,653        | 27.5     | OK    |
| W2-add-variable | tckit  | 7          | 1,328        | 40.2     | OK    |
| W3-add-method   | empty  | 5          | 1,236        | 26.2     | OK    |
| W3-add-method   | tckit  | 5          | 852          | 29.7     | OK    |

Pairwise ratios (vanilla / tckit; >1 means TcKit more efficient):

| Task            | Tokens | Wall  | Tool calls |
| --------------- | ------ | ----- | ---------- |
| W2-add-variable | 1.24x  | 0.68x | 0.71x      |
| W3-add-method   | 1.45x  | 0.88x | 1.00x      |

All four builds green. **The W1 prediction that vanilla's invented GUID would cause a W3 build failure did not hold** (see finding #1).

## Tool breakdown

**W2 / tckit (7 calls):** `ToolSearch ×2, mcp__tckit__get_pou_declaration ×3, mcp__tckit__get_structure ×1, mcp__tckit__add_variable ×1`. Sequence: load schemas, peek declaration, load `get_structure` schema, orient, re-peek declaration, write, **post-write peek**. Two of the three `get_pou_declaration` calls are bench-noise (initial peek + self-verification after the write); see finding #3.

**W2 / empty (5 calls):** `Glob ×1, Read ×2, Grep ×1, Edit ×1`. Glob for the `.TcPOU`, two reads to navigate, grep for the existing `VAR` block to anchor, then a direct `Edit` against the XML.

**W3 / tckit (5 calls):** `ToolSearch ×2, mcp__tckit__add_method ×1, Glob ×1, mcp__tckit__build ×1`. Sequence: load schemas, write, load `build` schema, find sln path, build. Two of the five calls are post-write build verification triggered by the prompt's "so the project still builds" wording; see finding #3.

**W3 / empty (5 calls):** `Glob ×1, Read ×2, Bash ×1, Edit ×1`. The Bash call was `wc -l "...FB_TestSuite.TcPOU"`, used to plan the Edit's anchor. The Edit hardcoded a placeholder GUID; see finding #1.

## Findings

### 1. Vanilla's GUID strategy is "make one up", and TwinCAT accepts it silently

The W1 doc predicted vanilla would either invent a GUID that XAE accepts or get a build failure. The reality is more interesting: vanilla didn't try to *generate* a GUID at all. It wrote the literal placeholder `{a1b2c3d4-e5f6-4789-abcd-ef0123456789}` directly into the `<Method Id="...">` attribute. That string is a mnemonic sentinel, not a UUID issued by any generator. TwinCAT compiled it without complaint and the project built clean.

This is a worse failure mode than a loud build failure would have been. Build is green, so a casual reader would consider the operation successful. But the placeholder GUID is:

- Not unique. The next vanilla `add_method` call from a fresh context is overwhelmingly likely to produce the same string, creating two methods with identical `Id` attributes in the same `.TcPOU`. We didn't trigger a collision in this single-task smoke, but the failure mode is latent.
- Not registered with TwinCAT's project tracking. XAE issues GUIDs from a known generator path. A placeholder may survive build but cause phantom diffs, merge friction, or downstream import problems on operations we haven't exercised yet.

TcKit's `add_method` got its GUID from the COM API (`{5f64218a-33b6-0fa6-34f0-b39daf82749c}` in the smoke), which is guaranteed unique and registered with TwinCAT in the same code path XAE itself uses. The thesis stands, just with the failure mode reclassified from "loud" to "silent". The right follow-up is to design a W3-variant that forces a collision (two methods added in sequence) and check whether the second placeholder collides with the first, and what TwinCAT does when it sees duplicate `Id` attributes.

### 2. The `open_project` tighten worked

W1 found TcKit spent 1 of 5 calls on `mcp__tckit__open_project` despite the harness pre-opening. The description on the writer tool now reads "Most workflows pre-open the project before any tool call, so this is rarely needed mid-session." Neither W2 tckit nor W3 tckit called `open_project` in the smoke. Clean one-call saving on both writer tasks. Carry the same discipline to other tools where the harness or skill already implies the precondition.

### 3. Self-verification keeps creeping back in, and it's asymmetric

W1 lesson was "don't ask the model to verify the change; the harness produces diff and build artefacts." W2/W3 prompts followed that discipline (W2 says nothing about verification; W3 says "so the project still builds" as a goal, not a check). Two of the four runs self-verified anyway:

- **W2 tckit** called `get_pou_declaration` once before the write *and once after*. The after-write call is pure verification, despite no prompt ask. The model defaulted to "read back what I wrote" because that is the natural shape of an edit-then-verify workflow.
- **W3 tckit** read "so the project still builds" as an instruction to *check* the build, not as a constraint to satisfy. It loaded the `build` tool schema (1 ToolSearch), located the sln (1 Glob), and called `build` (1 call). Three of the five calls are verification.

Vanilla self-verified in neither task. It just wrote the diff and stopped. So the verification cost falls asymmetrically on TcKit, making the ratios look smaller than they should. Possible mitigations:

- Tighten skill text to discourage post-write reads ("the harness verifies; you do not need to").
- Drop "so the project still builds" from the W3 prompt for the N=3 sweep, since it's the model's interpretation of the wording rather than a property of the task.
- Accept the cost in the ratio if the model's natural behaviour is a fair representation of real usage. A real operator probably also wants Claude to spot-check; the harness's `.build.json` doesn't reach the chat.

Strict prompt-trim for the N=3 sweep is the cheap option. The skill change is the principled option. Both are out of scope for #54.

### 4. `add_variable` places the new block at the end when none exists

W2's TcKit diff added the `bSilent : BOOL` declaration in a new `VAR_INPUT` block appended *after* the existing `VAR` block:

```
    NumberOfOrderedTests : UINT(...);
+END_VAR
+VAR_INPUT
+    bSilent : BOOL; // :Suppresses console output when TRUE:
 END_VAR]]></Declaration>
```

Vanilla's diff added the same declaration in a `VAR_INPUT` block placed conventionally *before* the existing `VAR`:

```
 FUNCTION_BLOCK FB_TestSuite
+VAR_INPUT
+    bSilent : BOOL; // :Suppresses console output when TRUE:
+END_VAR
 VAR
```

Both compile. Both are functionally equivalent. But the bridge harness (`Add-TcVariable.ps1`) creates the new scope block at the end of the declaration when the requested scope doesn't already exist. ST convention puts `VAR_INPUT` / `VAR_OUTPUT` / `VAR_IN_OUT` *before* `VAR` blocks, and TcUnit's `FB_TestSuite` happens to have no `VAR_INPUT` block. The harness picks a placement that the build accepts but a human reviewer would correct.

Two options on the table:

- **Status quo.** "Append when absent" is a defensible default; the build doesn't care; the author can move the block by hand. Risk: silently produces non-idiomatic declarations.
- **Convention-aware insertion.** Patch `Add-TcVariable.ps1` to insert a fresh `VAR_INPUT` block immediately after the `FUNCTION_BLOCK` line (or before the first `VAR` block). Modest harness change, no port surface impact.

Not in scope for this round; flagged for a follow-up.

### 5. TcKit is slower wall-clock but cheaper on tokens, on both writer tasks

W2 wall ratio 0.68x, W3 wall ratio 0.88x. Both favour vanilla on latency. The COM round trips through the bridge (XAE COM, SetItemSource, file save) cost real seconds compared to vanilla's local file Edit. Wall-clock is not the right metric to optimise on a writer task; token cost and correctness are. The wall numbers are a fair record of the cost, not a regression.

The token ratios (1.24x on W2, 1.45x on W3) move in the right direction even on N=1. W3's 1.45x with all five TcKit calls including the verification overhead is the load-bearing data point: if a prompt-trim removes the build call, the ratio shifts further. Speculative until the trim is run.

### 6. Vanilla's `Edit ×2` retry pattern did not recur on W2 or W3

W1 finding #3 noted vanilla called `Edit` twice on identical inputs (first succeeded, second got "old_string not found"). On W2 vanilla and W3 vanilla the Edit call landed on the first try, no retry. Either the model has learned to read the success signal, or the anchors on these two tasks happen to be unambiguous. Worth keeping an eye on across the N=3 sweep where the larger sample will tell us whether the retry pattern is anchor-specific or model-rate.

## What this validates and invalidates

**Validates:**

- **`add_variable` and `add_method` compose end-to-end** through MCP -> bridge -> COM -> XAE -> disk. Both produced buildable diffs.
- **`open_project` tighten** removed the defensive call on both W2 and W3 tckit runs. One-call savings cashed.
- **Token ratios move in TcKit's favour as the write gets structurally harder** (W1 1.05x → W2 1.24x → W3 1.45x). The trend matches the writer thesis even though all builds passed.
- **TcKit's GUID generation is the right answer.** It produced a real, unique, TwinCAT-issued GUID via the COM API while vanilla produced a placeholder string.

**Refines:**

- **The W1 GUID-collision prediction was too narrow.** The expected failure mode was a *build* failure; the actual failure mode is *silent acceptance of a placeholder*. Build verification alone won't catch this class of issue. A collision-forcing W3-variant would.
- **Self-verification cost is real and asymmetric.** Strict prompt-trim discipline doesn't always work because the model self-verifies on writes by default. The skill-text route (discourage post-write reads) is the principled fix.
- **`add_variable` placement is non-idiomatic when the target scope is absent.** Compiles, but a human reviewer would move it.

**Open:**

- Whether vanilla's placeholder GUID causes problems on a second add or on downstream operations we haven't run (library import, source-control merge, XAE refresh). The collision-forcing W3-variant ("add two methods in succession") would tell us.
- Whether the `Edit` retry pattern recurs at higher N or on tasks where the anchor is more ambiguous.
- Whether tightening the writer skill to discourage post-write self-verification removes the asymmetric cost, and by how much.
- The wall-clock cost of the COM round-trips. Not the metric to optimise on, but worth measuring once if we ever add a "fast path" for trivial structural writes.

## Caveats

- N=1 across two tasks at one prompt apiece. Token deltas below ~15% should not be treated as a signal, and the W2 ratio (1.24x) is near that floor. W3's 1.45x is more comfortably above it.
- One model (Opus 4.7), one project (TcUnit, ~50 POUs), one target FB (`FB_TestSuite`).
- W3's "build clean" result with vanilla's placeholder GUID is conditional on the single-add case. The latent failure mode is collision and downstream operations, neither exercised here.
- TcKit MCP server was restarted between #53 and this round to pick up the `open_project` docstring change. Any caching of tool descriptions on the model side could in principle mute the effect; the observed call-breakdown confirms the change landed.

## Suggested next experiments

1. **N=3 sweep over W1/W2/W3.** Same prompts (or post-trim variants of W3; see below), same target, three runs per (task, config). Tests whether the W3 1.45x ratio survives the noise floor, whether vanilla's GUID strategy is stable across runs, and whether the `Edit` retry pattern recurs at N>1.
2. **W3 prompt trim before N=3.** Drop "so the project still builds" wording; rely on the harness's `.build.json` to capture the outcome. Should remove the verification overhead from TcKit's tool count.
3. **Collision-forcing W3-variant** ("add two methods in succession"). Tests whether vanilla's placeholder GUID strategy collides on the second add and what TwinCAT does. Strongest test of the latent failure mode flagged in finding #1.
4. **`add_variable` placement fix** in `Add-TcVariable.ps1`. Insert new scope blocks at conventional positions (`VAR_INPUT`/`VAR_OUTPUT`/`VAR_IN_OUT` before `VAR`) when the scope is absent.
5. **Writer-skill text update to discourage post-write self-verification.** Add a one-liner to `tc-write-st` skill: "the bench/harness/operator verifies the diff; do not read back what you just wrote unless asked." Tests the principled fix to finding #3.
6. **W4 (add_pou) bench, parked again.** The collision-forcing W3-variant is a better next test of the silent-failure thesis than W4 right now.

## Interpretation, in one line

**The token ratio moves in TcKit's favour as structural complexity rises (W1 1.05x, W2 1.24x, W3 1.45x), the `open_project` tighten paid for itself, but the headline W3 finding is that vanilla writes a literal placeholder GUID and TwinCAT accepts it silently. That is a quieter failure mode than predicted, and a stronger argument for `add_method` than the build-failure case would have been.**
