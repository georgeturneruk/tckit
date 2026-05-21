"""Author the T2 anti-windup PID TDD fixture against a live bridge.

T2 is the second TDD task (after T1 schmitt-trigger). The model is
given an empty ``FB_Pid``, a fully-declared ``I_Pid`` interface, and
a TcUnit suite asserting eleven behaviours of a PID controller with
anti-windup, output clamping, derivative-on-measurement, and
direction modes.

The fixture deliberately commits no GVL or DUT: the model authors
``GVL_PidDefaults`` (default tunings), and may author
``ST_PidState`` / ``E_PidMode`` for internal state organisation. The
new writer tools (``add_property``, ``add_dut``) exist precisely so
the model can stand up these objects through TcKit rather than raw
XML edits.

See ADR-0007 for the bench framing, ADR-0008 for the per-fixture
CLAUDE.md template (now installed by the shared scaffolder), and
ADR-0012 for the property/DUT writer additions.
"""

from __future__ import annotations

import sys

from _common import (
    REPO_ROOT,
    check,
    finalise_fixture,
    parse_args,
    scaffold_fixture,
)
from tckit.ports.types import POUType  # noqa: E402

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "T2-pid-anti-windup"
SLN_NAME = "T2Pid"
TESTS_PLC = "PidTests"
LIBRARY_FB = "FB_Pid"
INTERFACE = "I_Pid"
TESTS_FB = "FB_PidTests"

# ---------------------------------------------------------------------------
# Library side: empty FB and the I_Pid interface.
# ---------------------------------------------------------------------------

# FB_Pid ships with nothing more than the function-block header and an
# empty VAR block. The model is expected to add `IMPLEMENTS I_Pid`,
# author the cyclic logic in a `Update` method (NOT in the FB body —
# see twincat/cyclic-in-method.md), the `Reset` method, and the
# tuning + state properties the test suite exercises.
LIBRARY_FB_DECL = f"""\
FUNCTION_BLOCK {LIBRARY_FB}
VAR
END_VAR
"""

# I_Pid is interface-only: methods, no properties. Keeping properties
# off the interface dodges the abstract-accessor body issue (TwinCAT
# property children inside an INTERFACE have empty bodies, which our
# add_property adapter doesn't naturally model). The polymorphism
# test (`CyclicReachableThroughInterface`) only needs Update and
# Reset to be reachable through `I_Pid`.
INTERFACE_DECL = f"""\
INTERFACE {INTERFACE}
"""

INTERFACE_UPDATE = """\
METHOD Update : LREAL
VAR_INPUT
    setpoint    : LREAL;
    measurement : LREAL;
    deltaT      : LREAL;
END_VAR
"""

INTERFACE_RESET = """\
METHOD Reset
"""

# ---------------------------------------------------------------------------
# Tests side: TcUnit suite with 11 ordered tests.
# ---------------------------------------------------------------------------

TESTS_FB_DECL = f"""\
FUNCTION_BLOCK {TESTS_FB} EXTENDS TcUnit.FB_TestSuite
VAR
    pid       : {SLN_NAME}_Plc.{LIBRARY_FB};
    iPid      : {SLN_NAME}_Plc.{INTERFACE};
    output    : LREAL;
    output2   : LREAL;
    integral  : LREAL;
    saturated : BOOL;
    i         : DINT;
END_VAR
"""

TESTS_FB_BODY = """\
PProportionalOnly();
OutputClampsToMax();
OutputClampsToMin();
IntegralAccumulates();
AntiWindupHoldsIntegral();
DerivativeOnMeasurementNoSetpointSpike();
ReverseModeFlipsSign();
ResetClearsIntegral();
SetterRejectsNegativeKp();
IsSaturatedReflectsClampState();
CyclicReachableThroughInterface();
"""

# Default-mode bookkeeping: tests set Mode := 0 for DIRECT, 1 for
# REVERSE. The model is free to author E_PidMode and use the enum
# constants internally; the test surface here uses INT to keep the
# fixture independent of any model-authored DUT.
TEST_P_ONLY = """\
METHOD PProportionalOnly : BOOL
VAR
    output : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 2.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    output := pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    AssertEquals_LREAL(Expected := 2.0, Actual := output, Delta := 1E-6,
        Message := 'P-only Kp=2 error=1 should yield output=2');
    TEST_FINISHED();
END_IF
"""

TEST_CLAMP_MAX = """\
METHOD OutputClampsToMax : BOOL
VAR
    output : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 2.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -5.0;
    pid.OutputMax := 5.0;
    output := pid.Update(setpoint := 100.0, measurement := 0.0, deltaT := 0.1);
    AssertEquals_LREAL(Expected := 5.0, Actual := output, Delta := 1E-6,
        Message := 'Output should clamp to OutputMax on large positive error');
    TEST_FINISHED();
END_IF
"""

TEST_CLAMP_MIN = """\
METHOD OutputClampsToMin : BOOL
VAR
    output : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 2.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -5.0;
    pid.OutputMax := 5.0;
    output := pid.Update(setpoint := -100.0, measurement := 0.0, deltaT := 0.1);
    AssertEquals_LREAL(Expected := -5.0, Actual := output, Delta := 1E-6,
        Message := 'Output should clamp to OutputMin on large negative error');
    TEST_FINISHED();
END_IF
"""

TEST_INTEGRAL_ACCUM = """\
METHOD IntegralAccumulates : BOOL
VAR
    integral : LREAL;
    i : DINT;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 0.0;
    pid.Ki := 1.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    FOR i := 1 TO 10 DO
        pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    END_FOR
    integral := pid.IntegralTerm;
    AssertEquals_LREAL(Expected := 1.0, Actual := integral, Delta := 0.05,
        Message := 'I-only Ki=1 error=1 for 10x0.1s should integrate to ~1.0');
    TEST_FINISHED();
END_IF
"""

TEST_ANTIWINDUP = """\
METHOD AntiWindupHoldsIntegral : BOOL
VAR
    integral : LREAL;
    i : DINT;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 0.0;
    pid.Ki := 10.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -1.0;
    pid.OutputMax := 1.0;
    // Drive into saturation: with Ki=10 and error=1, a single 0.1s tick
    // would produce output 1.0 (at clamp). Twenty more ticks should
    // not grow the integral once saturated.
    FOR i := 1 TO 20 DO
        pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    END_FOR
    integral := pid.IntegralTerm;
    AssertTrue(Condition := integral <= 0.2,
        Message := 'Anti-windup should hold integral at/near saturation point, not let it grow');
    TEST_FINISHED();
END_IF
"""

TEST_D_ON_MEASUREMENT = """\
METHOD DerivativeOnMeasurementNoSetpointSpike : BOOL
VAR
    output : LREAL;
    output2 : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 0.0;
    pid.Ki := 0.0;
    pid.Kd := 1.0;
    pid.Mode := 0;
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    // Steady state: setpoint=0, measurement=0.
    pid.Update(setpoint := 0.0, measurement := 0.0, deltaT := 0.1);
    // Step the SETPOINT. With derivative-on-error this would produce
    // a large spike (d_error/dt ~ 100). With derivative-on-measurement
    // the derivative is computed from -dMeasurement/dt, which is 0.
    output := pid.Update(setpoint := 10.0, measurement := 0.0, deltaT := 0.1);
    AssertTrue(Condition := ABS(output) < 1.0,
        Message := 'Derivative-on-measurement: setpoint step must NOT cause D-kick');
    TEST_FINISHED();
END_IF
"""

TEST_REVERSE_MODE = """\
METHOD ReverseModeFlipsSign : BOOL
VAR
    output : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 2.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 1;  // reverse
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    output := pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    AssertEquals_LREAL(Expected := -2.0, Actual := output, Delta := 1E-6,
        Message := 'Reverse mode should flip the sign of the controller output');
    TEST_FINISHED();
END_IF
"""

TEST_RESET_CLEARS_INTEGRAL = """\
METHOD ResetClearsIntegral : BOOL
VAR
    integral : LREAL;
    i : DINT;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Kp := 0.0;
    pid.Ki := 1.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    FOR i := 1 TO 5 DO
        pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    END_FOR
    AssertTrue(Condition := pid.IntegralTerm > 0.0,
        Message := 'Integral must be non-zero before Reset for this test to be meaningful');
    pid.Reset();
    integral := pid.IntegralTerm;
    AssertEquals_LREAL(Expected := 0.0, Actual := integral, Delta := 1E-9,
        Message := 'Reset must zero the integral term');
    TEST_FINISHED();
END_IF
"""

TEST_SETTER_REJECTS_NEGATIVE_KP = """\
METHOD SetterRejectsNegativeKp : BOOL
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Kp := 0.5;
    pid.Kp := -1.0;  // setter must reject this
    AssertEquals_LREAL(Expected := 0.5, Actual := pid.Kp, Delta := 1E-9,
        Message := 'Setter must reject negative Kp; existing value preserved');
    TEST_FINISHED();
END_IF
"""

TEST_IS_SATURATED = """\
METHOD IsSaturatedReflectsClampState : BOOL
VAR
    saturated : BOOL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 1.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -10.0;
    pid.OutputMax := 10.0;
    // Small error, output well within limits.
    pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    saturated := pid.IsSaturated;
    AssertFalse(Condition := saturated,
        Message := 'IsSaturated should be FALSE when output is within OutputMin..OutputMax');
    // Now saturate the output.
    pid.Update(setpoint := 100.0, measurement := 0.0, deltaT := 0.1);
    saturated := pid.IsSaturated;
    AssertTrue(Condition := saturated,
        Message := 'IsSaturated should be TRUE once the output is clamped to OutputMax');
    TEST_FINISHED();
END_IF
"""

# Test 11 is the explicit polymorphism check. If the model wrote the
# cyclic logic in FB_Pid's body (rather than in a `Update` method on
# the FB), `iPid.Update(...)` reaches a method that does nothing
# (the interface's METHOD Update is satisfied by an empty FB-side
# method) and the assertion fails.
TEST_CYCLIC_THROUGH_INTERFACE = """\
METHOD CyclicReachableThroughInterface : BOOL
VAR
    direct    : LREAL;
    viaIface  : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 3.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 0;
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    direct := pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);

    pid.Reset();
    iPid := pid;  // FB_Pid IMPLEMENTS I_Pid required
    viaIface := iPid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);

    AssertEquals_LREAL(Expected := direct, Actual := viaIface, Delta := 1E-9,
        Message := 'Same Update inputs through pid and iPid must give the same output');
    TEST_FINISHED();
END_IF
"""

MAIN_DECL = """\
PROGRAM MAIN
VAR
    suite : FB_PidTests;
END_VAR
"""

MAIN_BODY = """\
suite();
TcUnit.RUN();
"""


def main() -> int:
    args = parse_args(__doc__ or "")
    scaffold = scaffold_fixture(
        fixture_dir=FIXTURE_DIR,
        sln_name=SLN_NAME,
        tests_plc=TESTS_PLC,
        force=args.force,
    )
    w = scaffold.writer

    # ---- Library side: I_Pid interface + empty FB_Pid ----
    check(
        f"add_pou({INTERFACE})",
        w.add_pou(INTERFACE, POUType.INTERFACE, INTERFACE_DECL, plc_name=scaffold.library_plc),
    )
    check(
        f"add_method({INTERFACE}.Update)",
        w.add_method(INTERFACE, "Update", INTERFACE_UPDATE, plc_name=scaffold.library_plc),
    )
    check(
        f"add_method({INTERFACE}.Reset)",
        w.add_method(INTERFACE, "Reset", INTERFACE_RESET, plc_name=scaffold.library_plc),
    )
    check(
        f"add_pou({LIBRARY_FB})",
        w.add_pou(LIBRARY_FB, POUType.FUNCTION_BLOCK, LIBRARY_FB_DECL, plc_name=scaffold.library_plc),
    )

    # ---- Tests side: FB_PidTests + 11 ordered tests + MAIN driver ----
    check(
        f"add_pou({TESTS_FB})",
        w.add_pou(TESTS_FB, POUType.FUNCTION_BLOCK, TESTS_FB_DECL, plc_name=scaffold.tests_plc),
    )
    check(
        f"update_pou_implementation({TESTS_FB})",
        w.update_pou_implementation(TESTS_FB, TESTS_FB_BODY, plc_name=scaffold.tests_plc),
    )
    tests = [
        ("PProportionalOnly", TEST_P_ONLY),
        ("OutputClampsToMax", TEST_CLAMP_MAX),
        ("OutputClampsToMin", TEST_CLAMP_MIN),
        ("IntegralAccumulates", TEST_INTEGRAL_ACCUM),
        ("AntiWindupHoldsIntegral", TEST_ANTIWINDUP),
        ("DerivativeOnMeasurementNoSetpointSpike", TEST_D_ON_MEASUREMENT),
        ("ReverseModeFlipsSign", TEST_REVERSE_MODE),
        ("ResetClearsIntegral", TEST_RESET_CLEARS_INTEGRAL),
        ("SetterRejectsNegativeKp", TEST_SETTER_REJECTS_NEGATIVE_KP),
        ("IsSaturatedReflectsClampState", TEST_IS_SATURATED),
        ("CyclicReachableThroughInterface", TEST_CYCLIC_THROUGH_INTERFACE),
    ]
    for name, code in tests:
        check(
            f"add_method({TESTS_FB}.{name})",
            w.add_method(TESTS_FB, name, code, plc_name=scaffold.tests_plc),
        )

    # MAIN already exists on the tests PLC from create_project. Replace
    # its declaration + body so it drives the suite under TcUnit.
    check(
        "update_pou_declaration(MAIN)",
        w.update_pou_declaration("MAIN", MAIN_DECL, plc_name=scaffold.tests_plc),
    )
    check(
        "update_pou_implementation(MAIN)",
        w.update_pou_implementation("MAIN", MAIN_BODY, plc_name=scaffold.tests_plc),
    )

    finalise_fixture(scaffold)
    return 0


if __name__ == "__main__":
    sys.exit(main())
