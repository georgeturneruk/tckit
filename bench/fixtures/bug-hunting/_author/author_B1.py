"""Author the B1 off-by-one bug-hunting fixture against a live bridge.

Drives the ADR-0009 multi-PLC + library chain end-to-end:

    create_project (Library PLC, named ${SlnName}_Plc)
        -> add_plc_project (Tests PLC, sibling)
        -> add_library_placeholder (Tests -> TcUnit, xUnitEnablePublish=TRUE)
        -> add_pou (FB_RollingAverage in Library, with the seeded bug)
        -> add_method (Step in FB_RollingAverage, with the off-by-one)
        -> add_pou (FB_RollingAverageConsumer in Tests, calls FB_RollingAverage)
        -> save_plc_as_library (Library, install=True)
        -> add_library_reference (Tests -> Library)
        -> build (Tests)

Bench runs reset the fixture from git; this script exists to
*regenerate* that committed shape when the seeded bug or layout
changes. Run with --force to overwrite. Requires the bridge service
at $BRIDGE_URL (default localhost:8765) and a TwinCAT 4026 install
with the TcUnit library installed.
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B1-off-by-one"
SLN_NAME = "B1RollingAverage"
TESTS_PLC = "RollingAverageTests"
LIBRARY_FB = "FB_RollingAverage"
CONSUMER_FB = "FB_RollingAverageConsumer"
SUITE_FB = "FB_RollingAverageTests"
TEST_METHOD = "AverageOfConstantStream"


LIBRARY_FB_DECL = """\
FUNCTION_BLOCK FB_RollingAverage
VAR
    samples : ARRAY[0..15] OF INT;
    sampleCount : INT := 8;
    nextIndex : INT;
END_VAR
"""

# Off-by-one bug seeded in Step: `FOR i := 1 TO sampleCount` reads
# samples[1..8] when it should read samples[0..7]. samples[8..15] are
# zero-initialised (never written), so a constant-input stream of 10s
# yields sum = 70 instead of 80 and average = 8 instead of 10.
STEP_METHOD = """\
METHOD Step : INT
VAR_INPUT
    sample : INT;
END_VAR
VAR
    sum : DINT;
    i : INT;
END_VAR
samples[nextIndex] := sample;
nextIndex := (nextIndex + 1) MOD sampleCount;
sum := 0;
FOR i := 1 TO sampleCount DO
    sum := sum + samples[i];
END_FOR
Step := DINT_TO_INT(sum / sampleCount);
"""


# TcUnit suite FB. The body call lands in the FB's own implementation block
# (split by Update-TcPouItem at END_VAR). EXTENDS TcUnit.FB_TestSuite needs
# the TcUnit placeholder to already be on the consumer PLC at add time —
# _common.py installs it inside scaffold_fixture for exactly this reason.
SUITE_FB_CODE = """\
FUNCTION_BLOCK FB_RollingAverageTests EXTENDS TcUnit.FB_TestSuite
VAR
    averager : B1RollingAverage_Plc.FB_RollingAverage;
    result : INT;
    i : INT;
END_VAR
AverageOfConstantStream();
"""


# Empty VAR/END_VAR included as the issue-#84 workaround so Add-TcMethod's
# splitter sees a clear declaration/implementation boundary.
TEST_METHOD_CODE = """\
METHOD PRIVATE AverageOfConstantStream
VAR
END_VAR
TEST('AverageOfConstantStream');
FOR i := 1 TO 8 DO
    result := averager.Step(sample := 10);
END_FOR
AssertEquals_INT(
    Expected := 10,
    Actual := result,
    Message := 'Average of eight 10s should be 10');
TEST_FINISHED();
"""


# Cyclic driver: the suite FB instance ticks each cycle (which runs the
# test methods), then TcUnit.RUN() advances the runner state machine. The
# runner stops when every suite reports finished.
MAIN_CODE = """\
PROGRAM MAIN
VAR
    suite : FB_RollingAverageTests;
END_VAR
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
    consumer_code = (
        f"FUNCTION_BLOCK {CONSUMER_FB}\n"
        "VAR\n"
        f"    adder : {scaffold.library_plc}.{LIBRARY_FB};\n"
        "    lastResult : INT;\n"
        "END_VAR\n"
        "lastResult := adder.Step(sample := 10);\n"
    )

    w = scaffold.writer
    check(
        f"add_pou({LIBRARY_FB})",
        w.add_pou(LIBRARY_FB, POUType.FUNCTION_BLOCK, LIBRARY_FB_DECL, plc_name=scaffold.library_plc),
    )
    check(
        "add_method(Step)",
        w.add_method(LIBRARY_FB, "Step", STEP_METHOD, plc_name=scaffold.library_plc),
    )
    check(
        f"add_pou({CONSUMER_FB})",
        w.add_pou(CONSUMER_FB, POUType.FUNCTION_BLOCK, consumer_code, plc_name=scaffold.tests_plc),
    )
    check(
        f"add_pou({SUITE_FB})",
        w.add_pou(SUITE_FB, POUType.FUNCTION_BLOCK, SUITE_FB_CODE, plc_name=scaffold.tests_plc),
    )
    check(
        f"add_method({TEST_METHOD})",
        w.add_method(SUITE_FB, TEST_METHOD, TEST_METHOD_CODE, plc_name=scaffold.tests_plc),
    )
    check(
        "update_pou_item(MAIN)",
        w.update_pou_item("MAIN", "MAIN", MAIN_CODE, plc_name=scaffold.tests_plc),
    )

    finalise_fixture(scaffold)
    return 0


if __name__ == "__main__":
    sys.exit(main())
