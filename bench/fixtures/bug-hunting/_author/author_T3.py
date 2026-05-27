"""Author the T3 TcKit-utilities TDD fixture against a live bridge.

T3 is the third TDD task (after T1 schmitt-trigger and T2 PID
anti-windup). Where T1 and T2 each exercise a single FB, T3 stands
up a small generic-utility library with three independent surfaces:

- a PID controller (the API carried over verbatim from T2),
- a typeless ring buffer (``ANY``-on-every-surface so the call site
  is pointer-free),
- a string builder (4095-char backing) plus four standalone string
  utility functions.

The library is organised into per-utility folders on both PLCs,
exercising the ``add_folder`` + ``parent_folder`` writer calls
shipped in ADR-0013. The bench publishes the contract under
``TASK.md``; the model authors the bodies. The 28 ordered tests in
three TcUnit suites grade the implementation per behaviour.

See ADR-0007 for the bench framing, ADR-0008 for the per-fixture
CLAUDE.md template, ADR-0012 for the property/DUT writer additions,
and ADR-0013 for the folder + parent_folder + add_dut surfaces this
script exercises.
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
from tckit.ports.types import DUTKind, POUType  # noqa: E402

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "T3-tckit-utils"
SLN_NAME = "T3TckitUtils"
TESTS_PLC = "TckitUtilsTests"
# scaffold_fixture names the library PLC ``{SLN_NAME}_Plc``.
LIBRARY_PLC = f"{SLN_NAME}_Plc"
LIB_NS = LIBRARY_PLC  # used as the namespace prefix in test code

# Folder names (relative to POUs/ or DUTs/ on each PLC).
F_PID = "PID"
F_RB = "RingBuffer"
F_STR = "Strings"

# ---------------------------------------------------------------------------
# PID side - identical to T2 modulo the library namespace.
# ---------------------------------------------------------------------------

INTERFACE_DECL = "INTERFACE I_Pid\n"

INTERFACE_UPDATE = """\
METHOD Update : LREAL
VAR_INPUT
    setpoint    : LREAL;
    measurement : LREAL;
    deltaT      : LREAL;
END_VAR
"""

INTERFACE_RESET = "METHOD Reset\n"

FB_PID_DECL = """\
FUNCTION_BLOCK FB_Pid
VAR
END_VAR
"""

FB_PID_TESTS_DECL = f"""\
FUNCTION_BLOCK FB_PidTests EXTENDS TcUnit.FB_TestSuite
VAR
    pid       : {LIB_NS}.FB_Pid;
    iPid      : {LIB_NS}.I_Pid;
    output    : LREAL;
    output2   : LREAL;
    integral  : LREAL;
    saturated : BOOL;
    i         : DINT;
END_VAR
"""

FB_PID_TESTS_BODY = """\
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

PID_TEST_P_ONLY = """\
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

PID_TEST_CLAMP_MAX = """\
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

PID_TEST_CLAMP_MIN = """\
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

PID_TEST_INTEGRAL_ACCUM = """\
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

PID_TEST_ANTIWINDUP = """\
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
    FOR i := 1 TO 20 DO
        pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    END_FOR
    integral := pid.IntegralTerm;
    AssertTrue(Condition := integral <= 0.2,
        Message := 'Anti-windup should hold integral at/near saturation point, not let it grow');
    TEST_FINISHED();
END_IF
"""

PID_TEST_D_ON_MEASUREMENT = """\
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
    pid.Update(setpoint := 0.0, measurement := 0.0, deltaT := 0.1);
    output := pid.Update(setpoint := 10.0, measurement := 0.0, deltaT := 0.1);
    AssertTrue(Condition := ABS(output) < 1.0,
        Message := 'Derivative-on-measurement: setpoint step must NOT cause D-kick');
    TEST_FINISHED();
END_IF
"""

PID_TEST_REVERSE_MODE = """\
METHOD ReverseModeFlipsSign : BOOL
VAR
    output : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Reset();
    pid.Kp := 2.0;
    pid.Ki := 0.0;
    pid.Kd := 0.0;
    pid.Mode := 1;
    pid.OutputMin := -100.0;
    pid.OutputMax := 100.0;
    output := pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    AssertEquals_LREAL(Expected := -2.0, Actual := output, Delta := 1E-6,
        Message := 'Reverse mode should flip the sign of the controller output');
    TEST_FINISHED();
END_IF
"""

PID_TEST_RESET = """\
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

PID_TEST_SETTER_REJECTS = """\
METHOD SetterRejectsNegativeKp : BOOL
IF TEST_ORDERED(__POUNAME()) THEN
    pid.Kp := 0.5;
    pid.Kp := -1.0;
    AssertEquals_LREAL(Expected := 0.5, Actual := pid.Kp, Delta := 1E-9,
        Message := 'Setter must reject negative Kp; existing value preserved');
    TEST_FINISHED();
END_IF
"""

PID_TEST_IS_SATURATED = """\
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
    pid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);
    saturated := pid.IsSaturated;
    AssertFalse(Condition := saturated,
        Message := 'IsSaturated should be FALSE when output is within OutputMin..OutputMax');
    pid.Update(setpoint := 100.0, measurement := 0.0, deltaT := 0.1);
    saturated := pid.IsSaturated;
    AssertTrue(Condition := saturated,
        Message := 'IsSaturated should be TRUE once the output is clamped to OutputMax');
    TEST_FINISHED();
END_IF
"""

PID_TEST_INTERFACE = """\
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
    iPid := pid;
    viaIface := iPid.Update(setpoint := 1.0, measurement := 0.0, deltaT := 0.1);

    AssertEquals_LREAL(Expected := direct, Actual := viaIface, Delta := 1E-9,
        Message := 'Same Update inputs through pid and iPid must give the same output');
    TEST_FINISHED();
END_IF
"""

PID_TESTS = [
    ("PProportionalOnly", PID_TEST_P_ONLY),
    ("OutputClampsToMax", PID_TEST_CLAMP_MAX),
    ("OutputClampsToMin", PID_TEST_CLAMP_MIN),
    ("IntegralAccumulates", PID_TEST_INTEGRAL_ACCUM),
    ("AntiWindupHoldsIntegral", PID_TEST_ANTIWINDUP),
    ("DerivativeOnMeasurementNoSetpointSpike", PID_TEST_D_ON_MEASUREMENT),
    ("ReverseModeFlipsSign", PID_TEST_REVERSE_MODE),
    ("ResetClearsIntegral", PID_TEST_RESET),
    ("SetterRejectsNegativeKp", PID_TEST_SETTER_REJECTS),
    ("IsSaturatedReflectsClampState", PID_TEST_IS_SATURATED),
    ("CyclicReachableThroughInterface", PID_TEST_INTERFACE),
]

# ---------------------------------------------------------------------------
# Ring buffer side - empty FB stub + ST_Sample DUT + 9 tests.
# ---------------------------------------------------------------------------

FB_RB_DECL = """\
FUNCTION_BLOCK FB_RingBuffer
VAR
END_VAR
"""

ST_SAMPLE_DECL = """\
TYPE ST_Sample :
STRUCT
    t : LREAL;
    v : LREAL;
END_STRUCT
END_TYPE
"""

FB_RB_TESTS_DECL = f"""\
FUNCTION_BLOCK FB_RingBufferTests EXTENDS TcUnit.FB_TestSuite
VAR
END_VAR
"""

FB_RB_TESTS_BODY = """\
ConfigureRejectsZeroCapacity();
EmptyAfterConfigure();
PushPopFifoLreal();
PushPopFifoInt();
PushPopUserDefinedStruct();
PeekDoesNotConsume();
WrapAroundPreservesOrder();
PushReturnsFalseWhenFull();
PopReturnsFalseWhenEmpty();
"""

# Tests use locally-declared FBs + storage + values so each test is
# self-contained and starts from a fresh FB_init state. The element
# carriers (v1, v2, popped, etc.) must be variables, not literals,
# because TwinCAT's ANY requires an addressable argument.

RB_TEST_CONFIGURE_ZERO = f"""\
METHOD ConfigureRejectsZeroCapacity : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    AssertFalse(Condition := rb.Configure(storage, 0),
        Message := 'Configure with capacity=0 must return FALSE');
    AssertTrue(Condition := rb.Configure(storage, 4),
        Message := 'Configure with matched diSize/capacity must return TRUE');
    TEST_FINISHED();
END_IF
"""

RB_TEST_EMPTY_AFTER_CONFIGURE = f"""\
METHOD EmptyAfterConfigure : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    AssertTrue(Condition := rb.Configure(storage, 4),
        Message := 'Configure must succeed for ARRAY[1..4] OF LREAL with capacity=4');
    AssertEquals_UDINT(Expected := 0, Actual := rb.Count,
        Message := 'Fresh buffer Count must be 0');
    AssertTrue(Condition := rb.IsEmpty,
        Message := 'Fresh buffer IsEmpty must be TRUE');
    AssertFalse(Condition := rb.IsFull,
        Message := 'Fresh buffer IsFull must be FALSE');
    AssertEquals_UDINT(Expected := 4, Actual := rb.Capacity,
        Message := 'Capacity must reflect the configured value');
    TEST_FINISHED();
END_IF
"""

RB_TEST_FIFO_LREAL = f"""\
METHOD PushPopFifoLreal : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF LREAL;
    v1, v2, v3 : LREAL;
    popped   : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 4);
    v1 := 1.0;
    v2 := 2.0;
    v3 := 3.0;
    AssertTrue(Condition := rb.Push(v1), Message := 'Push(1.0) must succeed');
    AssertTrue(Condition := rb.Push(v2), Message := 'Push(2.0) must succeed');
    AssertTrue(Condition := rb.Push(v3), Message := 'Push(3.0) must succeed');
    AssertEquals_UDINT(Expected := 3, Actual := rb.Count,
        Message := 'Count after three pushes must be 3');
    AssertTrue(Condition := rb.Pop(popped), Message := 'Pop must succeed when buffer is non-empty');
    AssertEquals_LREAL(Expected := 1.0, Actual := popped, Delta := 1E-9,
        Message := 'First Pop must return the first Push');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 2.0, Actual := popped, Delta := 1E-9,
        Message := 'Second Pop must return the second Push');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 3.0, Actual := popped, Delta := 1E-9,
        Message := 'Third Pop must return the third Push');
    AssertTrue(Condition := rb.IsEmpty,
        Message := 'Buffer must be empty after popping all elements');
    TEST_FINISHED();
END_IF
"""

RB_TEST_FIFO_INT = f"""\
METHOD PushPopFifoInt : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF INT;
    v1, v2   : INT;
    popped   : INT;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 4);
    v1 := 11;
    v2 := 22;
    rb.Push(v1);
    rb.Push(v2);
    AssertEquals_UDINT(Expected := 2, Actual := rb.Count,
        Message := 'Count after two INT pushes must be 2');
    rb.Pop(popped);
    AssertEquals_INT(Expected := 11, Actual := popped,
        Message := 'INT FIFO order must match LREAL FIFO order');
    rb.Pop(popped);
    AssertEquals_INT(Expected := 22, Actual := popped,
        Message := 'Second INT Pop must return the second Push');
    TEST_FINISHED();
END_IF
"""

RB_TEST_FIFO_STRUCT = f"""\
METHOD PushPopUserDefinedStruct : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF ST_Sample;
    inA, inB : ST_Sample;
    popped   : ST_Sample;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 4);
    inA.t := 0.1; inA.v := 11.0;
    inB.t := 0.2; inB.v := 22.0;
    rb.Push(inA);
    rb.Push(inB);
    AssertEquals_UDINT(Expected := 2, Actual := rb.Count,
        Message := 'Count after two ST_Sample pushes must be 2');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 0.1, Actual := popped.t, Delta := 1E-9,
        Message := 'ST_Sample.t must round-trip exactly');
    AssertEquals_LREAL(Expected := 11.0, Actual := popped.v, Delta := 1E-9,
        Message := 'ST_Sample.v must round-trip exactly');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 0.2, Actual := popped.t, Delta := 1E-9,
        Message := 'Second ST_Sample.t must round-trip exactly');
    AssertEquals_LREAL(Expected := 22.0, Actual := popped.v, Delta := 1E-9,
        Message := 'Second ST_Sample.v must round-trip exactly');
    TEST_FINISHED();
END_IF
"""

RB_TEST_PEEK = f"""\
METHOD PeekDoesNotConsume : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF LREAL;
    v1       : LREAL;
    seen     : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 4);
    v1 := 7.5;
    rb.Push(v1);
    AssertTrue(Condition := rb.Peek(seen),
        Message := 'Peek must succeed when buffer is non-empty');
    AssertEquals_LREAL(Expected := 7.5, Actual := seen, Delta := 1E-9,
        Message := 'Peek must return the front element');
    AssertEquals_UDINT(Expected := 1, Actual := rb.Count,
        Message := 'Peek must not change Count');
    seen := 0.0;
    rb.Peek(seen);
    AssertEquals_LREAL(Expected := 7.5, Actual := seen, Delta := 1E-9,
        Message := 'A second Peek must return the same element');
    TEST_FINISHED();
END_IF
"""

RB_TEST_WRAP = f"""\
METHOD WrapAroundPreservesOrder : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..3] OF LREAL;
    a, b, c, d, e : LREAL;
    popped   : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 3);
    a := 1.0; b := 2.0; c := 3.0;
    rb.Push(a); rb.Push(b); rb.Push(c);
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 1.0, Actual := popped, Delta := 1E-9,
        Message := 'First Pop must return 1.0 before wrap');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 2.0, Actual := popped, Delta := 1E-9,
        Message := 'Second Pop must return 2.0 before wrap');
    d := 4.0; e := 5.0;
    AssertTrue(Condition := rb.Push(d),
        Message := 'Push after Pops must succeed (forces write index to wrap)');
    AssertTrue(Condition := rb.Push(e),
        Message := 'Second Push after Pops must also succeed');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 3.0, Actual := popped, Delta := 1E-9,
        Message := 'After wrap, Pop must return 3.0 (the surviving pre-wrap element)');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 4.0, Actual := popped, Delta := 1E-9,
        Message := 'After wrap, second Pop must return 4.0');
    rb.Pop(popped);
    AssertEquals_LREAL(Expected := 5.0, Actual := popped, Delta := 1E-9,
        Message := 'After wrap, third Pop must return 5.0');
    TEST_FINISHED();
END_IF
"""

RB_TEST_FULL = f"""\
METHOD PushReturnsFalseWhenFull : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..2] OF LREAL;
    v1, v2, v3 : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 2);
    v1 := 1.0; v2 := 2.0; v3 := 3.0;
    AssertTrue(Condition := rb.Push(v1), Message := 'First Push must succeed');
    AssertTrue(Condition := rb.Push(v2), Message := 'Second Push must succeed');
    AssertTrue(Condition := rb.IsFull,
        Message := 'IsFull must be TRUE when Count = Capacity');
    AssertFalse(Condition := rb.Push(v3),
        Message := 'Push must return FALSE when the buffer is full');
    AssertEquals_UDINT(Expected := 2, Actual := rb.Count,
        Message := 'A refused Push must not change Count');
    TEST_FINISHED();
END_IF
"""

RB_TEST_EMPTY_POP = f"""\
METHOD PopReturnsFalseWhenEmpty : BOOL
VAR
    rb       : {LIB_NS}.FB_RingBuffer;
    storage  : ARRAY[1..4] OF LREAL;
    popped   : LREAL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rb.Configure(storage, 4);
    AssertFalse(Condition := rb.Pop(popped),
        Message := 'Pop must return FALSE on an empty buffer');
    AssertFalse(Condition := rb.Peek(popped),
        Message := 'Peek must return FALSE on an empty buffer');
    TEST_FINISHED();
END_IF
"""

RB_TESTS = [
    ("ConfigureRejectsZeroCapacity", RB_TEST_CONFIGURE_ZERO),
    ("EmptyAfterConfigure", RB_TEST_EMPTY_AFTER_CONFIGURE),
    ("PushPopFifoLreal", RB_TEST_FIFO_LREAL),
    ("PushPopFifoInt", RB_TEST_FIFO_INT),
    ("PushPopUserDefinedStruct", RB_TEST_FIFO_STRUCT),
    ("PeekDoesNotConsume", RB_TEST_PEEK),
    ("WrapAroundPreservesOrder", RB_TEST_WRAP),
    ("PushReturnsFalseWhenFull", RB_TEST_FULL),
    ("PopReturnsFalseWhenEmpty", RB_TEST_EMPTY_POP),
]

# ---------------------------------------------------------------------------
# Strings side - FB_StringBuilder + 4 functions + 8 tests.
# ---------------------------------------------------------------------------

FB_SB_DECL = """\
FUNCTION_BLOCK FB_StringBuilder
VAR
END_VAR
"""

# Standalone functions: ship headers only. A bodyless FUNCTION
# returns the default-initialised value of its return type, which
# satisfies TwinCAT's "function must assign its return" check at
# warning level only (build still passes).

F_TRIM_DECL = """\
FUNCTION F_Trim : STRING
VAR_INPUT
    s : STRING;
END_VAR
"""

F_STARTSWITH_DECL = """\
FUNCTION F_StartsWith : BOOL
VAR_INPUT
    s      : STRING;
    prefix : STRING;
END_VAR
"""

F_ENDSWITH_DECL = """\
FUNCTION F_EndsWith : BOOL
VAR_INPUT
    s      : STRING;
    suffix : STRING;
END_VAR
"""

F_CONTAINS_DECL = """\
FUNCTION F_Contains : BOOL
VAR_INPUT
    s      : STRING;
    needle : STRING;
END_VAR
"""

FB_STR_TESTS_DECL = """\
FUNCTION_BLOCK FB_StringTests EXTENDS TcUnit.FB_TestSuite
VAR
END_VAR
"""

FB_STR_TESTS_BODY = """\
BuilderEmptyOnInit();
BuilderAppendOnce();
BuilderAppendMany();
BuilderAppendOverflowReturnsFalse();
BuilderClearResetsLength();
TrimStripsSurroundingWhitespace();
StartsWithEndsWithBoundaries();
ContainsFindsSubstring();
"""

STR_TEST_BUILDER_EMPTY = f"""\
METHOD BuilderEmptyOnInit : BOOL
VAR
    sb : {LIB_NS}.FB_StringBuilder;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    AssertEquals_UDINT(Expected := 0, Actual := sb.Length,
        Message := 'Freshly constructed builder Length must be 0');
    AssertFalse(Condition := sb.IsFull,
        Message := 'Freshly constructed builder IsFull must be FALSE');
    AssertEquals_UDINT(Expected := 4095, Actual := sb.Capacity,
        Message := 'Builder Capacity must be the documented 4095 bytes');
    TEST_FINISHED();
END_IF
"""

STR_TEST_BUILDER_ONCE = f"""\
METHOD BuilderAppendOnce : BOOL
VAR
    sb       : {LIB_NS}.FB_StringBuilder;
    buf      : ARRAY[1..32] OF BYTE;
    written  : UDINT;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    AssertTrue(Condition := sb.Append('hello'),
        Message := 'Append("hello") must succeed on an empty builder');
    AssertEquals_UDINT(Expected := 5, Actual := sb.Length,
        Message := 'Length after Append("hello") must be 5');
    written := sb.CopyTo(ADR(buf), SIZEOF(buf));
    AssertEquals_UDINT(Expected := 5, Actual := written,
        Message := 'CopyTo must report 5 payload bytes');
    AssertEquals_BYTE(Expected := 16#68, Actual := buf[1],
        Message := 'First copied byte must be ASCII "h" (0x68)');
    AssertEquals_BYTE(Expected := 16#6F, Actual := buf[5],
        Message := 'Fifth copied byte must be ASCII "o" (0x6F)');
    TEST_FINISHED();
END_IF
"""

STR_TEST_BUILDER_MANY = f"""\
METHOD BuilderAppendMany : BOOL
VAR
    sb : {LIB_NS}.FB_StringBuilder;
    i  : DINT;
    ok : BOOL;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    ok := TRUE;
    FOR i := 1 TO 500 DO
        ok := ok AND sb.Append('ab');
    END_FOR
    AssertTrue(Condition := ok,
        Message := 'Each of the 500 two-byte appends must succeed (1000 < 4095)');
    AssertEquals_UDINT(Expected := 1000, Actual := sb.Length,
        Message := 'Length after 500 x "ab" must be 1000');
    AssertFalse(Condition := sb.IsFull,
        Message := 'IsFull must remain FALSE while Length < Capacity');
    TEST_FINISHED();
END_IF
"""

STR_TEST_BUILDER_OVERFLOW = f"""\
METHOD BuilderAppendOverflowReturnsFalse : BOOL
VAR
    sb        : {LIB_NS}.FB_StringBuilder;
    i         : DINT;
    rejected  : BOOL;
    lengthAt  : UDINT;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    rejected := FALSE;
    FOR i := 1 TO 4100 DO
        IF NOT sb.Append('X') THEN
            rejected := TRUE;
            lengthAt := sb.Length;
            EXIT;
        END_IF
    END_FOR
    AssertTrue(Condition := rejected,
        Message := 'A single-byte Append must return FALSE before 4100 calls have completed');
    AssertEquals_UDINT(Expected := 4095, Actual := lengthAt,
        Message := 'Length at refusal point must equal Capacity (4095)');
    AssertTrue(Condition := sb.IsFull,
        Message := 'IsFull must be TRUE at the refusal point');
    TEST_FINISHED();
END_IF
"""

STR_TEST_BUILDER_CLEAR = f"""\
METHOD BuilderClearResetsLength : BOOL
VAR
    sb : {LIB_NS}.FB_StringBuilder;
END_VAR
IF TEST_ORDERED(__POUNAME()) THEN
    sb.Append('payload');
    AssertTrue(Condition := sb.Length > 0,
        Message := 'Builder must have non-zero Length before Clear for the test to be meaningful');
    sb.Clear();
    AssertEquals_UDINT(Expected := 0, Actual := sb.Length,
        Message := 'Clear must zero Length');
    AssertTrue(Condition := sb.Append('again'),
        Message := 'Append after Clear must succeed');
    AssertEquals_UDINT(Expected := 5, Actual := sb.Length,
        Message := 'Length must reflect the post-Clear Append');
    TEST_FINISHED();
END_IF
"""

STR_TEST_TRIM = f"""\
METHOD TrimStripsSurroundingWhitespace : BOOL
IF TEST_ORDERED(__POUNAME()) THEN
    AssertEquals_STRING(Expected := 'hi', Actual := {LIB_NS}.F_Trim('  hi  '),
        Message := 'F_Trim must strip surrounding spaces');
    AssertEquals_STRING(Expected := 'hi', Actual := {LIB_NS}.F_Trim('$Thi$N'),
        Message := 'F_Trim must strip tab/CR/LF as well as space');
    AssertEquals_STRING(Expected := '', Actual := {LIB_NS}.F_Trim(''),
        Message := 'F_Trim of the empty string must return the empty string');
    AssertEquals_STRING(Expected := 'inner space', Actual := {LIB_NS}.F_Trim('inner space'),
        Message := 'F_Trim must preserve internal whitespace');
    TEST_FINISHED();
END_IF
"""

STR_TEST_STARTS_ENDS = f"""\
METHOD StartsWithEndsWithBoundaries : BOOL
IF TEST_ORDERED(__POUNAME()) THEN
    AssertTrue(Condition := {LIB_NS}.F_StartsWith('foobar', 'foo'),
        Message := 'F_StartsWith must return TRUE for a matching prefix');
    AssertFalse(Condition := {LIB_NS}.F_StartsWith('foo', 'foobar'),
        Message := 'F_StartsWith must return FALSE when the prefix is longer than the string');
    AssertTrue(Condition := {LIB_NS}.F_StartsWith('anything', ''),
        Message := 'F_StartsWith with an empty prefix must return TRUE');
    AssertTrue(Condition := {LIB_NS}.F_EndsWith('foobar', 'bar'),
        Message := 'F_EndsWith must return TRUE for a matching suffix');
    AssertFalse(Condition := {LIB_NS}.F_EndsWith('bar', 'foobar'),
        Message := 'F_EndsWith must return FALSE when the suffix is longer than the string');
    AssertTrue(Condition := {LIB_NS}.F_EndsWith('anything', ''),
        Message := 'F_EndsWith with an empty suffix must return TRUE');
    TEST_FINISHED();
END_IF
"""

STR_TEST_CONTAINS = f"""\
METHOD ContainsFindsSubstring : BOOL
IF TEST_ORDERED(__POUNAME()) THEN
    AssertTrue(Condition := {LIB_NS}.F_Contains('one two three', 'two'),
        Message := 'F_Contains must find an interior substring');
    AssertFalse(Condition := {LIB_NS}.F_Contains('abc', 'd'),
        Message := 'F_Contains must return FALSE when the needle is absent');
    AssertTrue(Condition := {LIB_NS}.F_Contains('any', ''),
        Message := 'F_Contains with an empty needle must return TRUE');
    AssertFalse(Condition := {LIB_NS}.F_Contains('', 'x'),
        Message := 'F_Contains over an empty string must return FALSE for a non-empty needle');
    TEST_FINISHED();
END_IF
"""

STR_TESTS = [
    ("BuilderEmptyOnInit", STR_TEST_BUILDER_EMPTY),
    ("BuilderAppendOnce", STR_TEST_BUILDER_ONCE),
    ("BuilderAppendMany", STR_TEST_BUILDER_MANY),
    ("BuilderAppendOverflowReturnsFalse", STR_TEST_BUILDER_OVERFLOW),
    ("BuilderClearResetsLength", STR_TEST_BUILDER_CLEAR),
    ("TrimStripsSurroundingWhitespace", STR_TEST_TRIM),
    ("StartsWithEndsWithBoundaries", STR_TEST_STARTS_ENDS),
    ("ContainsFindsSubstring", STR_TEST_CONTAINS),
]

# ---------------------------------------------------------------------------
# MAIN - instantiates all three suites and lets TcUnit drive them.
# ---------------------------------------------------------------------------

MAIN_DECL = """\
PROGRAM MAIN
VAR
    pidSuite : FB_PidTests;
    rbSuite  : FB_RingBufferTests;
    strSuite : FB_StringTests;
END_VAR
"""

MAIN_BODY = """\
pidSuite();
rbSuite();
strSuite();
TcUnit.RUN();
"""

# Bench-specific topic file: the ANY-descriptor idiom that the
# ring-buffer surface relies on. ``scaffold_fixture`` wipes the
# fixture directory before re-running, so this file is rewritten
# from here each --force pass.
ANY_TYPE_PATTERN_MD = """\
# The TwinCAT `ANY` descriptor

TwinCAT exposes a built-in generic input type, `ANY`, that lets a
method accept "a value of any type, including user-defined STRUCTs"
without per-type overloads. The compiler resolves `ANY` to a small
descriptor at the call site:

```
TYPE __SYSTEM.AnyType :
STRUCT
    nTypeClass : __SYSTEM.TYPE_CLASS;
    diSize     : DINT;                   // SIZEOF(actual argument)
    pValue     : POINTER TO BYTE;        // ADR(actual argument)
END_STRUCT
END_TYPE
```

A method declared as

```
METHOD Push : BOOL
VAR_INPUT
    item : ANY;
END_VAR
```

can be called as `rb.Push(myLreal)` or `rb.Push(myStruct)` and read
the argument's address with `item.pValue` and its byte width with
`item.diSize`. The caller writes no `ADR(...)` and no `SIZEOF(...)`.

## Reading and writing through `pValue`

`ANY` is technically a `VAR_INPUT` type - the FB only sees the
descriptor by value. But the descriptor *contains* the caller's
address. The pointer is live for the duration of the synchronous
call, so the method body may both read from and write to it:

```
// Push (write into our storage from the caller's variable)
MEMCPY(destAddr := ADR(_storage[_writeIdx * elementSize]),
       srcAddr  := item.pValue,
       n        := elementSize);

// Pop (write back into the caller's variable from our storage)
MEMCPY(destAddr := out.pValue,
       srcAddr  := ADR(_storage[_readIdx * elementSize]),
       n        := elementSize);
```

The "write back through a `VAR_INPUT ANY`" direction is unusual but
not a hack: `pValue` is a `POINTER TO BYTE`, and using a pointer for
its address-of meaning is exactly what pointers are for. The result
is that `rb.Pop(sample);` updates `sample` in the caller's frame as
if it had been declared `VAR_IN_OUT`, while keeping the API
pointer-free at the call site.

## The size-mismatch guard

Because the FB can no longer rely on the type system to keep
elements homogeneous, every `Push`/`Pop`/`Peek` should compare the
caller's `diSize` against the element size locked in by
`Configure`:

```
IF UDINT_TO_DINT(elementSize) <> item.diSize THEN
    Push := FALSE;
    RETURN;
END_IF
```

This catches `rb.Push(myInt)` against a buffer that was configured
for `ARRAY OF LREAL`: the operation refuses rather than copying the
wrong number of bytes.

## When to reach for `ANY`

Use `ANY` to hide pointer arithmetic at API boundaries where:

- the FB stores or shuffles raw bytes irrespective of type
  (queues, ring buffers, byte pools), and
- the call site would otherwise repeat the same
  `ADR(x), SIZEOF(x)` pair on every line.

Do **not** use `ANY` where the value actually matters - PID
tunings, control set-points, anything you'd want to compute with.
The descriptor erases the type, so arithmetic on the underlying
bytes is the caller's problem. `ANY` is a transport, not a value.
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
    lib = scaffold.library_plc
    tests = scaffold.tests_plc

    # Bench-specific topic file (installed alongside the standard
    # CLAUDE.md template tree).
    topic_path = FIXTURE_DIR / "twincat" / "any-type-pattern.md"
    topic_path.parent.mkdir(parents=True, exist_ok=True)
    topic_path.write_text(ANY_TYPE_PATTERN_MD, encoding="utf-8")
    print(f"OK   wrote {topic_path.relative_to(REPO_ROOT)}")

    # ---- Library-side folders ----
    check(f"add_folder(POUs/{F_PID}, {lib})",
          w.add_folder(F_PID, parent_path="POUs", plc_name=lib))
    check(f"add_folder(POUs/{F_RB}, {lib})",
          w.add_folder(F_RB, parent_path="POUs", plc_name=lib))
    check(f"add_folder(POUs/{F_STR}, {lib})",
          w.add_folder(F_STR, parent_path="POUs", plc_name=lib))

    # ---- Tests-side folders (mirror, plus DUTs/RingBuffer for ST_Sample) ----
    check(f"add_folder(POUs/{F_PID}, {tests})",
          w.add_folder(F_PID, parent_path="POUs", plc_name=tests))
    check(f"add_folder(POUs/{F_RB}, {tests})",
          w.add_folder(F_RB, parent_path="POUs", plc_name=tests))
    check(f"add_folder(POUs/{F_STR}, {tests})",
          w.add_folder(F_STR, parent_path="POUs", plc_name=tests))
    check(f"add_folder(DUTs/{F_RB}, {tests})",
          w.add_folder(F_RB, parent_path="DUTs", plc_name=tests))

    # ---- Library: PID (interface + empty FB) ----
    check("add_pou(I_Pid)",
          w.add_pou("I_Pid", POUType.INTERFACE, INTERFACE_DECL,
                    parent_folder=F_PID, plc_name=lib))
    check("add_method(I_Pid.Update)",
          w.add_method("I_Pid", "Update", INTERFACE_UPDATE,
                       parent_folder=F_PID, plc_name=lib))
    check("add_method(I_Pid.Reset)",
          w.add_method("I_Pid", "Reset", INTERFACE_RESET,
                       parent_folder=F_PID, plc_name=lib))
    check("add_pou(FB_Pid)",
          w.add_pou("FB_Pid", POUType.FUNCTION_BLOCK, FB_PID_DECL,
                    parent_folder=F_PID, plc_name=lib))

    # ---- Library: RingBuffer (empty FB only; ST_Sample is tests-internal) ----
    check("add_pou(FB_RingBuffer)",
          w.add_pou("FB_RingBuffer", POUType.FUNCTION_BLOCK, FB_RB_DECL,
                    parent_folder=F_RB, plc_name=lib))

    # ---- Library: Strings (empty builder + four function stubs) ----
    check("add_pou(FB_StringBuilder)",
          w.add_pou("FB_StringBuilder", POUType.FUNCTION_BLOCK, FB_SB_DECL,
                    parent_folder=F_STR, plc_name=lib))
    check("add_pou(F_Trim)",
          w.add_pou("F_Trim", POUType.FUNCTION, F_TRIM_DECL,
                    parent_folder=F_STR, plc_name=lib))
    check("add_pou(F_StartsWith)",
          w.add_pou("F_StartsWith", POUType.FUNCTION, F_STARTSWITH_DECL,
                    parent_folder=F_STR, plc_name=lib))
    check("add_pou(F_EndsWith)",
          w.add_pou("F_EndsWith", POUType.FUNCTION, F_ENDSWITH_DECL,
                    parent_folder=F_STR, plc_name=lib))
    check("add_pou(F_Contains)",
          w.add_pou("F_Contains", POUType.FUNCTION, F_CONTAINS_DECL,
                    parent_folder=F_STR, plc_name=lib))

    # ---- Tests: PID suite ----
    check("add_pou(FB_PidTests)",
          w.add_pou("FB_PidTests", POUType.FUNCTION_BLOCK, FB_PID_TESTS_DECL,
                    parent_folder=F_PID, plc_name=tests))
    check("update_pou_implementation(FB_PidTests)",
          w.update_pou_implementation("FB_PidTests", FB_PID_TESTS_BODY, plc_name=tests))
    for name, code in PID_TESTS:
        check(f"add_method(FB_PidTests.{name})",
              w.add_method("FB_PidTests", name, code,
                           parent_folder=F_PID, plc_name=tests))

    # ---- Tests: RingBuffer suite + the ST_Sample fixture DUT ----
    check("add_dut(ST_Sample, tests)",
          w.add_dut("ST_Sample", ST_SAMPLE_DECL, dut_kind=DUTKind.STRUCT,
                    parent_folder=F_RB, plc_name=tests))
    check("add_pou(FB_RingBufferTests)",
          w.add_pou("FB_RingBufferTests", POUType.FUNCTION_BLOCK, FB_RB_TESTS_DECL,
                    parent_folder=F_RB, plc_name=tests))
    check("update_pou_implementation(FB_RingBufferTests)",
          w.update_pou_implementation("FB_RingBufferTests", FB_RB_TESTS_BODY, plc_name=tests))
    for name, code in RB_TESTS:
        check(f"add_method(FB_RingBufferTests.{name})",
              w.add_method("FB_RingBufferTests", name, code,
                           parent_folder=F_RB, plc_name=tests))

    # ---- Tests: Strings suite ----
    check("add_pou(FB_StringTests)",
          w.add_pou("FB_StringTests", POUType.FUNCTION_BLOCK, FB_STR_TESTS_DECL,
                    parent_folder=F_STR, plc_name=tests))
    check("update_pou_implementation(FB_StringTests)",
          w.update_pou_implementation("FB_StringTests", FB_STR_TESTS_BODY, plc_name=tests))
    for name, code in STR_TESTS:
        check(f"add_method(FB_StringTests.{name})",
              w.add_method("FB_StringTests", name, code,
                           parent_folder=F_STR, plc_name=tests))

    # ---- Tests: MAIN drives all three suites under TcUnit ----
    check("update_pou_declaration(MAIN)",
          w.update_pou_declaration("MAIN", MAIN_DECL, plc_name=tests))
    check("update_pou_implementation(MAIN)",
          w.update_pou_implementation("MAIN", MAIN_BODY, plc_name=tests))

    # ---- Trim auto-scaffolded empty folders ----
    # ``create_project`` seeds each PLC with DUTs/GVLs/VISUs/POUs at
    # the root. We only need the ones we put content into; the rest
    # is noise in Solution Explorer.
    check(f"delete_folder(DUTs, {lib})",
          w.delete_folder("DUTs", plc_name=lib))
    check(f"delete_folder(GVLs, {lib})",
          w.delete_folder("GVLs", plc_name=lib))
    check(f"delete_folder(VISUs, {lib})",
          w.delete_folder("VISUs", plc_name=lib))
    check(f"delete_folder(GVLs, {tests})",
          w.delete_folder("GVLs", plc_name=tests))
    check(f"delete_folder(VISUs, {tests})",
          w.delete_folder("VISUs", plc_name=tests))

    finalise_fixture(scaffold)
    return 0


if __name__ == "__main__":
    sys.exit(main())
