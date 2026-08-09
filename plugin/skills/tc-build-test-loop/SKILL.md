---
name: tc-build-test-loop
description: Use when building a TwinCAT project, deploying to a target, running TcUnit tests, or iterating on build/test failures through TcKit (Build, Deploy, StartRuntime, RunTests, GetTestResults). Triggers on requests like "build it", "run the tests", "fix the build errors", "deploy to <NetId>", "make the tests pass". Enforces the build-before-deploy ordering, the 2-attempt-per-error build fix limit, the 5-iteration test loop limit, the permission-gate rules for Deploy and StartRuntime (surface denials, never self-elevate), and the save+install rule for multi-PLC solutions with library references. Do NOT use for the initial code write itself (that is tc-write-st).
allowed-tools: mcp__tckit__Build, mcp__tckit__Deploy, mcp__tckit__StartRuntime, mcp__tckit__RunTests, mcp__tckit__GetTestResults, mcp__tckit__UpdateMethodBody, mcp__tckit__UpdateMethodBodyPatch, mcp__tckit__UpdatePouImplementation, mcp__tckit__UpdatePouImplementationPatch, mcp__tckit__GetPouItem, mcp__tckit__GetPouInterface, mcp__tckit__SavePlcAsLibrary, mcp__tckit__AnalyseProject
---

# Build / deploy / test loop

## Analyse first

Before the first `Build` of a cycle, call `AnalyseProject(projectPath, severity: "warning")`. It parses the project files offline, so it needs no XAE and returns in under a second against a build's tens of seconds, and it catches a class of defect a green build cannot rule out: a function block instance on a call stack whose state resets every call, floating-point equality, retention that cannot retain, a global with two writers.

- `severity: "warning"` keeps naming suggestions out of the way. Raise it to `suggestion` only when the user asked about conventions.
- Fix what it reports before building. These are not build errors, so building first tells you nothing about them.
- Check `skipped` and `config_warnings`. A short finding list next to a long `skipped` list means coverage was partial, not that the project is clean.
- One pass per cycle, not per fix. Re-run it after the build-fix loop settles if you changed much.

This does not replace the build. It is cheaper, so it goes first.

## Build-fix loop

1. Call `Build(project_path)` with an absolute path to the `.sln` (or `.tsproj`). For multi-PLC solutions, pass `plc_name=<consumer>` on the same call; see "Multi-PLC builds with library references" below.
2. If `success: true`:
   - If a `docs_warning` field is present, this is non-fatal — note it for the user but do NOT loop on it.
   - Proceed to deploy/test as the user requested.
3. If `success: false`, take the **first** error in the JSON list. Read the offending file via `GetPouItem` if needed. Fix that one file only.
4. Rebuild. Do not batch fixes across files.
5. **Two-strikes rule.** If the same error message on the same file/line persists after a second fix attempt, STOP. Present the error, your two attempts, your hypothesis, and ask the user.

## Where `targetAmsId` comes from

`Deploy`, `StartRuntime`, and `RunTests` take a required `targetAmsId` parameter; there is no env-var or config-file default. If you don't know the target, ask the user rather than guessing or hunting through the filesystem.

## Deploy and StartRuntime — the permission gate

`Deploy`, `StartRuntime`, and `RunTests` are execute-class tools, gated server-side by the permission file at `~/.tckit/permissions.json` (or `$TCKIT_HOME/permissions.json`), hot-reloaded on every call. The gate has two axes:

- **mode** — `read` < `write` < `execute`. `Build` needs `write`; the three execute-class tools need `execute`. A call above the current mode returns `{"error": "Permission denied: ..."}`.
- **Target NetIds** (execute-class only) — `blocked_net_ids` is a hard "never touch this target" guard (block always wins); a non-empty `allowed_net_ids` is an allowlist.

When a call returns a `Permission denied` error:

1. Treat it as a normal control-flow signal, not a fault. Do not retry the same call.
2. Surface the error text to the user verbatim — it names the missing mode or the offending NetId and how to change it.
3. **Never self-elevate.** Do not call `SetPermissions` or edit `permissions.json` to raise the mode or extend the allowlist unless the user explicitly asks for that change in chat. The stance is the user's standing configuration, not an obstacle to route around.
4. If the denial says the target is in `blocked_net_ids`, that is the hard guard: `SetPermissions` cannot lift it and you must not edit the file to lift it. Do not retry — surface the error and stop.

**When the gate clears.** A plain success means the current mode allows execute and the target passed the NetId rules. That is the user's standing pre-authorisation for this target — continue the cycle (StartRuntime, RunTests) rather than pausing for a fresh confirmation; there is no per-call confirmed handshake on these tools. (`WriteSymbols`, `InvokeRpc`, and `DeleteIoDevice` do carry a `confirmed` handshake on top of the gate, but those belong to the tc-hardware skill, not this loop.)

## Build-before-deploy

Never call `Deploy` unless the most recent `build` returned `success: true`. If the user asks you to deploy and the last build failed or wasn't run this session, build first.

## Multi-PLC builds with library references

If the solution holds two or more PLC projects where one (the consumer) holds a compiled library reference to another (the library), the consumer build resolves against the *installed* library, not the source. Editing the library project's source has no effect on the consumer until the library is saved and reinstalled.

When you've edited the library project (or aren't sure whether you have), call `SavePlcAsLibrary(plc_name=<library>, output_path=<path>, install=True)` **before** rebuilding the consumer. Output path can be anywhere writable; the harness writes the `.library` and installs it to the system repo in one COM call. `GetStructure` shows the per-PLC `libraries` list — if a consumer references a sibling PLC project by name, that's the trigger.

If the solution has only one PLC project, or the consumer uses Source-Only references (resolved automatically by TwinCAT's build), this step doesn't apply. When in doubt, run the save-as-library — it's idempotent and adds no real cost.

## Test loop

1. Build → must succeed.
2. Deploy (subject to the permission gate above; on a denial, stop and surface it).
3. `RunTests()`. The response carries `summary` (totals for the whole run) and `failures` (one entry per failed test with `suite_name`, `test_name`, `message`) inline by default. **Do not** call `GetTestResults()` on the happy path — it is for the full per-test list including passes, which you only need when the inline failure detail is insufficient.
4. If `summary.failures == 0` you're done.
5. For each failed test, read its body via `GetPouItem`, understand the assertion, fix the code under test (not the test, unless the test is wrong and the user agrees).
6. Go back to step 1.
7. **Five-iteration cap.** If tests are still failing on the 6th attempt, STOP and present:
   - What you tried (per-iteration summary).
   - Current failures with messages.
   - Your hypothesis.
   - A specific question for the user.

**Report the actual outcome.** Once the loop terminates (all green, error escalation, or iteration cap), report the test results from the final `RunTests` call. Don't stop after editing and assume the harness will validate — if the gate cleared you, you own the cycle through to results.

## Never

- Deploy without a green build.
- Treat a green build as evidence that the analysis findings were wrong. Everything `AnalyseProject` reports compiles; that is the entire reason it exists.
- Raise the permission mode or extend the NetId allowlist yourself to get a denied Deploy or StartRuntime through. Stance changes happen only when the user explicitly asks. (A plain success means the gate cleared under the user's standing configuration — proceeding on that is fine.)
- Continue past test iteration 5.
- Modify safety-critical code in a fix loop without escalating to `tc-write-st` for the human-review check.
