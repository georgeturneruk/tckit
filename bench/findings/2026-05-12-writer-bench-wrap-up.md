# 2026-05-12 — Writer-bench wrap-up (post-fix W2/W3 re-smoke)

Wrap-up round to address the three actionable findings from the W2/W3 smoke earlier today:

- `add_variable` placed a new scope block at the end of the declaration when the target scope was absent, producing non-idiomatic ST.
- TcKit's writer flow self-verified post-write (W2 tckit re-read `get_pou_declaration`, W3 tckit ran `build`), neither asked for in the prompt.
- The W3 prompt's "so the project still builds" wording was being parsed as a verification instruction by the model.

Three fixes, plus a re-smoke of the tckit arm on both tasks. Vanilla is unaffected by any of these changes (no MCP server in its config, no skill change reaches it, the W3 prompt change is a single phrase vanilla didn't act on in the prior smoke), so the previous vanilla numbers stand. **N=1 per (task, config) for the tckit re-runs; directional only.**

## What changed

1. **`bridge/harness/Add-TcVariable.ps1`.** When the requested scope block doesn't exist on the target item, the harness now inserts the new block at the conventional ST position (order: `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR`, `VAR CONSTANT`, `VAR_PERSISTENT`, `VAR_TEMP`). Implementation is inline in `Add-VariableToDeclaration`; an earlier attempt with a separate `Find-ExistingScopeBlocks` helper broke parameter binding under PowerShell 5.1 (typed `List[string]` argument coerced to an empty string during binding), which the first re-smoke caught immediately (see finding #2).
2. **`tc-write-st` skill.** Two new rules:
   - For a clear add on a named FB, call the writer directly. Defensive `get_pou_interface` / `get_pou_declaration` to "confirm the FB exists" is wasted; the writer fails cleanly if the target is missing.
   - After a successful writer call, do not read back / re-build to verify. The writer's success response IS the confirmation; the operator and harness verify the artefact. New anti-pattern bullet added for the same point.
3. **W3 prompt trim.** Dropped "so the project still builds" since the harness's `.build.json` already records the build outcome and the wording was steering the model to call `build` itself.

Bridge re-reads harness scripts per request and Claude Code re-reads skills per `claude -p` invocation, so neither service needed restarting.

## Results

Pre-fix numbers are from the smoke earlier today (`2026-05-12-writer-bench-w2-w3-smoke.md`). Post-fix tckit numbers are from re-runs; vanilla is unchanged.

| Task | Config | Calls (pre / post) | Tokens (pre / post) | Wall s (pre / post) | Build |
| ---- | ------ | ------------------ | ------------------- | ------------------- | ----- |
| W2-add-variable | empty | 5 / 5 | 1,653 / 1,653 | 27.5 / 27.5 | OK |
| W2-add-variable | tckit | 7 / **3** | 1,328 / **691** | 40.2 / **21.7** | OK |
| W3-add-method | empty | 5 / 5 | 1,236 / 1,236 | 26.2 / 26.2 | OK |
| W3-add-method | tckit | 5 / **2** | 852 / **508** | 29.7 / **15.5** | OK |

Pairwise ratios vanilla / tckit (post-fix; >1 means TcKit more efficient):

| Task | Tokens | Wall | Tool calls |
| ---- | ------ | ---- | ---------- |
| W2-add-variable | 2.39x | 1.27x | 1.67x |
| W3-add-method | 2.43x | 1.69x | 2.50x |

For comparison, the pre-fix ratios were W2 tokens 1.24x / wall 0.68x / calls 0.71x, and W3 tokens 1.45x / wall 0.88x / calls 1.00x. The wrap-up changes flipped both wall and call ratios into TcKit's favour and nearly doubled the token ratio on each task.

## Tool breakdown (post-fix tckit arms)

**W2 / tckit (3 calls):** `ToolSearch ×1, mcp__tckit__add_variable ×1, mcp__tckit__get_pou_declaration ×1`. Sequence: load schemas, write, read back. The pre-fix pre-write peek and orient (which had cost two calls) are both gone. The post-write read remains (see finding #3).

**W3 / tckit (2 calls):** `ToolSearch ×1, mcp__tckit__add_method ×1`. Sequence: load schemas, write, stop. The pre-fix verification chain (`ToolSearch` for build, `Glob` for sln, `build`) is gone. Zero defensive calls.

Diffs are byte-identical to the pre-fix runs in shape, only the GUIDs differ:

- W2 tckit diff: file hash `78c9c87`, identical to vanilla's W2 diff (correct conventional placement of the new `VAR_INPUT` block between `FUNCTION_BLOCK FB_TestSuite` and the existing `VAR` block).
- W3 tckit diff: a new method with GUID `{aef0f756-a5d9-0b98-12f4-e928c3647f66}` issued by the COM API, inserted at the canonical alphabetic position (before `SetStartedAtIfNotSet`).

## Findings

### 1. The placement fix produces vanilla-equivalent diffs

W2 tckit's diff is now byte-identical to vanilla's: a new `VAR_INPUT` block placed between the `FUNCTION_BLOCK` header and the existing `VAR` block, exactly where a human reviewer would put it. The previous "append at end of declaration" placement is gone.

The fix only affects the absent-scope path. If the target scope already exists, the existing pre-fix behaviour stands: insert before the matching `END_VAR`, no reordering. This preserves placement intent on items the user has already laid out.

### 2. The first re-smoke caught a real harness bug

The original placement fix had a helper function with a typed parameter (`[Parameter(Mandatory)][System.Collections.Generic.List[string]]$Lines`) that PowerShell 5.1 rejected with "Cannot bind argument to parameter 'Lines' because it is an empty string." The model recovered by reaching for `update_pou_item_patch` and patching the declaration directly, which produced a correct end-state but used 6 calls instead of 3. This was visible in the run JSON (the `add_variable` tool_result had `success: false`) but invisible from the build outcome alone.

Two lessons:

- **Build verification is not enough.** Both the pre-fix and the failed-binding versions produced green builds and correct diffs, but the path was wrong. The per-run tool sequence (and per-call success state) is the artefact to look at.
- **The model adapts around tool failures.** When `add_variable` returned `success: false`, the model didn't surface it to the user; it silently switched to `update_pou_item_patch` and got the job done. That's resilient, but it masks the bug. Worth noting for future harness changes: a passing build does not prove the tool worked as advertised, only that the model found *some* path to the right end state.

The inlined version (no helper function) avoids the binding issue and is verified by the second W2 re-smoke (3 calls, success on `add_variable`, no fallback).

### 3. Self-verification is partly fixed; one read still survives

The W3 skill rule worked completely: zero defensive calls, two-tool sequence (`ToolSearch`, `add_method`). The W2 outcome is mixed: the pre-write peek and orient are gone, but the model still called `get_pou_declaration` once after the successful `add_variable`. That is the rule the skill specifically called out as bench-noise, and the model did it anyway.

Two readings:

- The model is more cautious on `add_variable` than `add_method`. A method add is structurally well-defined (signature + body); a variable add depends on the surrounding scope block, so the model wants to confirm the placement. Skill text might need to be more emphatic on this specific case.
- The post-write read on W2 may be the model trying to honour the prompt's "Briefly state which tool you used" by checking what landed. The wording isn't asking for verification but it could be read that way.

Either way, the change is significant: W2 tckit went from 7 calls to 3 calls (one residual self-verify), not zero. Worth a second pass at the skill or the W2 prompt during a future bench round; not blocking.

### 4. The W3 ratio is no longer floor-near, it's clearly above the noise threshold

W3 token ratio jumped from 1.45x to **2.43x**. Tool-call ratio jumped from 1.00x to **2.50x**. Both are comfortably above the ~15% deltas-below-which-don't-trust-N=1 floor. Even at N=1, this is a fair representation of `add_method`'s value over vanilla's XML-Edit approach.

W2's ratio is even higher proportionally (1.24x → 2.39x tokens, 0.71x → 1.67x calls) because the wrap-up removed two of the four "bench noise" calls TcKit was paying. The token ratio in particular is no longer near the noise floor.

### 5. TcKit's wall-clock cost is now better than vanilla on both tasks

W2 wall ratio: 0.68x → **1.27x**. W3 wall ratio: 0.88x → **1.69x**. Both crossed into TcKit territory because the residual COM-round-trip overhead is dominated by the savings from skipping orientation, pre-write peek, and post-write verify.

This was not the headline expectation from the smoke; we expected TcKit to be slower on wall-clock by the bridge round-trip cost and faster on tokens by tool-output compression. Both turned out true, but the call-count reduction from the skill update produced enough wall-clock savings to flip the ratio. Worth holding lightly given N=1.

### 6. Net wrap-up effect, in raw call/token deltas

- **W2 tckit: 7 → 3 calls (-4, -57%)**. Sub-breakdown: -2 from pre-write peek/orient gone (skill), -1 from post-write peek gone... actually no, the post-write peek remained. So -2 from pre-write peek/orient gone, then the original 7th call was the second read which also went away. Net -4 from skill changes.
- **W3 tckit: 5 → 2 calls (-3, -60%)**. Sub-breakdown: -3 from build verification chain gone (skill + prompt trim).

The wrap-up did not change the writer thesis qualitatively. It made it visible at the metric level.

## What this validates and invalidates

**Validates:**

- **Convention-aware placement is the right default.** Producing vanilla-equivalent diffs eliminates a real friction point for human reviewers.
- **The skill is the right surface for verification discipline.** Prompt-level discipline alone (the lesson from W1) wasn't enough; the model self-verifies by default on writes. The skill rule cut three of four residual self-verifies across W2 and W3 in this smoke.
- **The W3 prompt trim worked.** The model no longer interprets "so the project still builds" as a verification ask.

**Refines:**

- **`add_variable` is still defensive.** The post-write read on W2 didn't go away despite the skill rule. Skill wording or a more direct anti-pattern about VAR adds specifically is the cheapest next try.
- **Build outcome alone is not a sufficient check.** The PowerShell 5.1 binding bug had a green build with a wrong tool path. Per-call success states are part of the gate.

**Open:**

- N=3 sweep over W1/W2/W3 with the post-fix harness/skill/prompt. Deferred per user direction; pick up when the dev pace slows down.
- Vanilla's GUID placeholder behaviour (silent placeholder, no collision in single-add). Deferred per user direction.
- Whether the residual W2 post-write read can be eliminated with sharper skill wording, or whether it's intrinsic to the writer-on-VAR pattern. Try once during the N=3 sweep.

## Caveats

- N=1 across two re-runs. The pre-fix vs post-fix delta is large enough that it's unlikely to be noise, but the absolute post-fix numbers should be held lightly.
- Vanilla numbers are reused from the earlier smoke today. The W3 prompt trim shouldn't affect vanilla (the dropped phrase didn't change behaviour in the pre-fix smoke), but vanilla was not re-run.
- The first re-smoke's behaviour (model falling back to `update_pou_item_patch` when `add_variable` failed) was caused by a transient harness bug introduced by this PR's draft. The numbers in the table are from the post-fix re-smoke, after the bug was inlined out.
- One model (Opus 4.7), one project (TcUnit), one target FB (`FB_TestSuite`).

## Suggested next experiments

1. **N=3 sweep** when dev pace allows. The post-fix ratios are likely to stabilise rather than swing; the W3 result is the most stable claim to nail down.
2. **Sharper skill wording on `add_variable` self-verification**, then re-smoke W2. The residual post-write read is the only remaining bench-noise call on the writer surface.
3. **Bigger benchmark: bug-hunting in our own solution with predefined TcUnit tests.** Out of scope for the current writer-bench round; will get its own ADR.

## Interpretation, in one line

**Three small changes (convention-aware placement, skill verification rule, prompt trim) cut TcKit's tool-call count by 4 on W2 and 3 on W3, lifted both token ratios above the noise floor (W2 1.24x → 2.39x, W3 1.45x → 2.43x), and flipped wall-clock from "vanilla wins" to "TcKit wins" on both tasks; the W2 post-write peek is the only residual bench-noise call still on the writer surface.**
