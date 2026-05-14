---
name: tc-build-test-loop
description: Use when building a TwinCAT project, deploying to a target, running TcUnit tests, or iterating on build/test failures through TcKit (build, deploy, start_runtime, run_tests, get_test_results). Triggers on requests like "build it", "run the tests", "fix the build errors", "deploy to <NetId>", "make the tests pass". Enforces the build-before-deploy ordering, the 2-attempt-per-error build fix limit, the 5-iteration test loop limit, the awaiting_confirmation handshake for deploy and start_runtime, and the save+install rule for multi-PLC solutions with library references. Do NOT use for the initial code write itself (that is tc-write-st).
allowed-tools: mcp__tckit__build, mcp__tckit__deploy, mcp__tckit__start_runtime, mcp__tckit__run_tests, mcp__tckit__get_test_results, mcp__tckit__update_pou_item, mcp__tckit__get_pou_item, mcp__tckit__get_pou_interface, mcp__tckit__save_plc_as_library
---

# Build / deploy / test loop

## Build-fix loop

1. Call `build(project_path)`.
2. If `success: true`:
   - If a `docs_warning` field is present, this is non-fatal — note it for the user but do NOT loop on it.
   - Proceed to deploy/test as the user requested.
3. If `success: false`, take the **first** error in the JSON list. Read the offending file via `get_pou_item` if needed. Fix that one file only.
4. Rebuild. Do not batch fixes across files.
5. **Two-strikes rule.** If the same error message on the same file/line persists after a second fix attempt, STOP. Present the error, your two attempts, your hypothesis, and ask the user.

## Deploy and start_runtime — the safety-gate handshake

`deploy(target_ams_id, confirmed=False)` and `start_runtime(target_ams_id, confirmed=False)` are gated server-side. The first call without `confirmed=True` returns an `awaiting_confirmation` JSON payload describing the action and target.

When you receive `awaiting_confirmation`:

1. Treat it as a normal control-flow signal, not an error. Do not retry blindly.
2. Surface the `warning` text and the `target_ams_id` to the user verbatim, including the override hint about ALLOWED_NETIDS / SAFETY_CONFIRMATIONS so the user knows their options.
3. Wait for explicit approval in chat ("yes", "go ahead", "confirmed").
4. Only then call again with `confirmed=True`.
5. Never auto-confirm. ALLOWED_NETIDS and SAFETY_CONFIRMATIONS are the user's env config — do not assume them, do not edit `.env` to bypass the gate.

If the response is `error` mentioning BLOCKED_NETIDS, the target is permanently blacklisted. Do not retry — surface the error to the user.

## Build-before-deploy

Never call `deploy` unless the most recent `build` returned `success: true`. If the user asks you to deploy and the last build failed or wasn't run this session, build first.

## Multi-PLC builds with library references

If the solution holds two or more PLC projects where one (the consumer) holds a compiled library reference to another (the library), the consumer build resolves against the *installed* library, not the source. Editing the library project's source has no effect on the consumer until the library is saved and reinstalled.

When you've edited the library project (or aren't sure whether you have), call `save_plc_as_library(plc_name=<library>, output_path=<path>, install=True)` **before** rebuilding the consumer. Output path can be anywhere writable; the harness writes the `.library` and installs it to the system repo in one COM call. `get_structure` shows the per-PLC `libraries` list — if a consumer references a sibling PLC project by name, that's the trigger.

If the solution has only one PLC project, or the consumer uses Source-Only references (resolved automatically by TwinCAT's build), this step doesn't apply. When in doubt, run the save-as-library — it's idempotent and adds no real cost.

## Test loop

1. Build → must succeed.
2. Deploy (with the safety-gate handshake above).
3. `run_tests()`, then `get_test_results()`.
4. For each failure, read the failing test's body via `get_pou_item`, understand the assertion, fix the code under test (not the test, unless the test is wrong and the user agrees).
5. Go back to step 1.
6. **Five-iteration cap.** If tests are still failing on the 6th attempt, STOP and present:
   - What you tried (per-iteration summary).
   - Current failures with messages.
   - Your hypothesis.
   - A specific question for the user.

## Never

- Deploy without a green build.
- Auto-confirm a deploy or start_runtime.
- Continue past test iteration 5.
- Modify safety-critical code in a fix loop without escalating to `tc-write-st` for the human-review check.
