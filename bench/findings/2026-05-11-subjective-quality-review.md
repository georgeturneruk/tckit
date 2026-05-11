# 2026-05-11 — Subjective quality review of the post-#42 / post-#46 bench outputs

Follow-up to `2026-05-11-post-issue-42.md`. That file scored efficiency (tool calls, tokens, wall). This one reads the *actual answers* Claude produced and asks: are TcKit and vanilla giving equivalent-quality output, or is one cutting corners?

Six runs in scope: the post-#42 Task 01 / Task 02 pair, the post-#46 Task 03 pair, plus the pre-#46 TcKit derail run for Task 03 as a control. All N=1, Opus 4.7, native TcKit, TcUnit target. `.md` siblings live in `bench/results/` (now de-mojibaked, see #50).

## Task 01 — orient

Token cost was a tie at the numeric level (TcKit 4,627, vanilla 4,586). The *answers* are not a tie.

- **TcKit covered both PLC projects** in the solution (`TcUnit/` library + `TcUnit-Verifier_TwinCAT/` harness) and correctly named the `PlcTask` (10 ms cycle, priority 20, runs `PRG_TEST`).
- **Vanilla reported "No tasks".** It looked at the library's `.tsproj`, which has no `<Tasks>` block, and concluded the project had no PLC task. The verifier's `.tsproj` does have one. Vanilla's exploration was effectively one-Glob-deep and under-scoped itself.
- Library refs: TcKit listed 8 (including `Tc3_Module` and a self-reference for the verifier-as-consumer). Vanilla listed 6.
- Vanilla added more *config* detail TcKit missed: `LinkAlways=true` on both GVLs, `SubObjectsSortedByName=True`, `PerformStaticAnalyse=False`. Useful for someone editing the project options.

**Quality verdict: TcKit is more *correct* at parity cost.** The bench tabulated this as a tie; reading the outputs shows a small but real win for TcKit on cross-project structural completeness. The same shape of error (vanilla under-scoping to one sub-project) should scale on bigger multi-project solutions.

## Task 02 — pinpoint method

Bodies were **byte-for-byte identical**. Both configs quoted the same `AssertEquals_INT` ST verbatim. Summary sentences are functionally equivalent. Vanilla added a line-number citation ("lines 2851–2875") — a minor bonus from having had to Grep for the offset.

**Quality verdict: tie. Pure efficiency win to TcKit** (1.23× tokens, 1.33× wall).

## Task 03 — explain FB API

Three answers compared: vanilla (post-#46), TcKit (post-#46), TcKit (pre-#46 derail).

- **Coverage was effectively the same across all three.** 22 scalar `AssertEquals_*` variants, 15 1-D array variants, 2 2-D variants, 2 3-D variants, AssertTrue/False, generic `AssertEquals`, `AreAllTestsFinished`, ~17–18 INTERNAL methods, 2 PRIVATE, plus `FB_init` and `GetTestByName`. Nothing material was missing from any of them.
- **Vanilla's organisation was the most polished.** Subsections within INTERNAL ("Test registration & lookup", "Counts & aggregation", "Timing", "Instance path") read more cleanly than TcKit's flat table.
- **Post-#46 TcKit was the most compact** at 6,824 tokens vs vanilla's 11,731 (42% cheaper) while covering the same ground.
- **The pre-#46 derail did not produce a worse answer.** Even when the model fell back to stock-tool reads after `open_project` failed, the resulting summary was comparable in coverage and clarity to the other two.

**Quality verdict: tie. Vanilla edges TcKit on organisation; TcKit is dramatically cheaper.** The #46 fix saved tokens, not answer quality.

## Cross-task takeaways

1. **Quality is largely equivalent across configs at this scale.** The efficiency claims aren't paid for in quality on any task in the set. That's the load-bearing observation.
2. **One genuine quality win for TcKit hides in the orient numbers.** TcKit's `get_structure` aggregated tasks across both PLC projects; vanilla's exploration didn't. The token ratio of 0.99× undersold this. For *cross-project structural questions* TcKit is more thorough than ad-hoc Glob+Read.
3. **The #46 fix preserved answer quality.** Steering the model away from `open_project` made the response cheaper without making it worse. No accidental trade-off.
4. **The Task 03 derail was a cost-only failure.** Pre-#46 TcKit produced a correct answer via stock tools at higher cost; post-#46 TcKit produced an equally correct answer via the right tool at lower cost. Reframes the original observation: "TcKit fell back and burned ~30% more tokens to give the same answer", not "TcKit fell back and gave a worse answer".
5. **Vanilla is competent on small projects.** On every task except the orient miss, vanilla either tied with TcKit or was arguably nicer (Task 03 organisation). The "bigger TcKit wins live on larger projects" hypothesis from the post-#42 findings still stands and is still untested.
6. **TcKit's structural-call advantage is "the whole project is in scope".** The Task 01 verifier-task miss is the same kind of error that scales: ad-hoc exploration is one-Glob-deep and easy to under-scope; `get_structure` walks once and aggregates everything.

## What this validates and invalidates

**Validates:**

- Reading the `.md` siblings is genuinely cheap and useful. The verifier-task miss on Task 01 was not visible from the metrics alone; only the body of the answer revealed it. Future bench passes should treat output review as a standard step, not a one-off.
- ADR-0002's bet that orientation is the higher-leverage navigation investment. TcKit's `get_structure` doing one walk and aggregating across sub-projects is paying off in correctness on top of the efficiency we already measured.

**Refines:**

- The post-#42 findings called Task 01 a numeric tie. With qualitative review folded in, it's a *correctness* win for TcKit at parity cost. Future write-ups should track output-completeness deltas separately from token deltas.

**Open:**

- All "vanilla is competent on small projects" caveats. The next experiment that would shift these conclusions is a larger project (TcOpen TcoCore or similar, ~200+ POUs). Predicted: orient quality gap widens substantially.

## Caveats

- N=1. Quality observations on a single sample shouldn't be over-weighted. The verifier-task miss could be a model-variance outcome; re-run at N=3 would confirm whether vanilla reliably under-scopes here.
- One model (Opus 4.7).
- One project (TcUnit, ~50 POUs).
- Quality is judged by me reading the outputs, not by an automated rubric. Reasonable people could weight "organisation" vs "completeness" differently.

## Suggested next experiments

1. **Re-run Task 01 at N=3** specifically to confirm whether vanilla reliably misses the verifier task or whether that was a one-off.
2. **TcoCore (or similar large project) bench** — same three tasks. Tests the "wins widen on bigger projects" hypothesis directly.
3. **Add a Task 04 pointed at the verifier project** (e.g. "what's in TcUnit-Verifier_TwinCAT and how does it relate to the library?"). Specifically exercises the cross-project structural shape where TcKit's edge appears.
4. **Track output-completeness alongside token cost** in future findings tables. A second-cheapest answer that's measurably more correct is still a win.

## Interpretation, in one line

**The numeric bench scored Task 01 as a TcKit-vs-vanilla tie; reading the actual answers showed TcKit caught the verifier-project task that vanilla missed, so the tie hides a small correctness win. On the other two tasks TcKit was cheaper at equivalent quality. Across the set, nothing in TcKit's efficiency story was paid for in answer accuracy.**
