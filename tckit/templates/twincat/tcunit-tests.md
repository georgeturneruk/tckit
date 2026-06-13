# TcUnit test conventions

Four conventions for TcUnit tests:

1. Prefer `TEST_ORDERED` over `TEST`.
2. Declare suites in MAIN, but never call them yourself; the runner
   drives every registered suite.
3. Use `RUN_IN_SEQUENCE()` (not `RUN()`) when multiple suites exist.
4. Name tests with `__POUNAME()`, not a hard-coded string.

## TEST_ORDERED over TEST

TcUnit's `TEST('Name')` runs every declared test in the same scan
cycle. That is fine for stateless assertions, but breaks tests that
need state to accumulate across scans — e.g., a PID controller's
integrator, a state-machine's transitions, an anti-windup clamp.

`TEST_ORDERED('Name')` runs tests strictly in declaration order:
test 2 is skipped until test 1 calls `TEST_FINISHED()`. Pattern:

```
IF TEST_ORDERED('LatchesHigh') THEN
    trigger(fInput := 0.9);
    bResult := trigger.Step();
    AssertEquals_BOOL(Expected := TRUE, Actual := bResult, Message := '...');
    TEST_FINISHED();
END_IF
```

Default to `TEST_ORDERED` for any test whose meaning depends on
what happened in earlier scans.

## Let the runner drive the suites; never call them

Declare each suite as a `VAR` in MAIN and let the runner execute it.
`RUN()` and `RUN_IN_SEQUENCE()` already invoke every registered suite
once per run. Calling a suite instance yourself (e.g. `suite1();`)
runs it a *second* time, which corrupts the results: the reported
test count exceeds the real one, and the last suite ends up carrying
a cumulative copy of every test that ran before it. The symptom is a
run that "passes" but reports more tests than exist.

When MAIN drives more than one suite, use `RUN_IN_SEQUENCE()` rather
than `RUN()`. It serialises execution, so suite 2 only starts after
suite 1 has finished. With a single suite, plain `RUN()` is fine.

```
PROGRAM MAIN
VAR
    suite1 : FB_FooTests;
    suite2 : FB_BarTests;
END_VAR

// Do NOT call suite1(); / suite2(); here. The runner drives them;
// calling them as well double-runs each suite.
TcUnit.RUN_IN_SEQUENCE();
```

## __POUNAME() for test names

Hard-coding the method name as the test name is a rename hazard:
renaming the method without renaming the string silently breaks
test identification (the suite still runs, the report just looks
wrong).

`__POUNAME()` is a compile-time constant that returns the current
POU's name as a string. Used inside a test method, it yields the
method name. Rename the method and the test name follows
automatically.

```
METHOD LatchesHighAboveHighThreshold : BOOL
VAR
END_VAR

IF TEST_ORDERED(__POUNAME()) THEN
    trigger(fInput := 0.9);
    bResult := trigger.Step();
    AssertEquals_BOOL(Expected := TRUE, Actual := bResult, Message := '...');
    TEST_FINISHED();
END_IF
```

**Version note:** `__POUNAME()` is a TwinCAT 4026 compile-time
constant. On 4024 it does not exist; fall back to a literal string
that matches the method name. TcKit targets 4026, so fixtures and
examples use `__POUNAME()` unconditionally.
