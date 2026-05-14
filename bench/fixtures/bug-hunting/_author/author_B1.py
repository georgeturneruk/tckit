"""Author the B1 off-by-one bug-hunting fixture against a live bridge.

Drives the ADR-0009 multi-PLC + library chain end-to-end:

    create_project (Library PLC, named ${SlnName}_Plc)
        -> add_plc_project (Tests PLC, sibling)
        -> add_pou (FB_RollingAverage in Library, with the seeded bug)
        -> add_method (Step in FB_RollingAverage, with the off-by-one)
        -> add_pou (FB_RollingAverageConsumer in Tests, calls FB_RollingAverage)
        -> save_plc_as_library (Library, install=True)
        -> add_library_reference (Tests -> Library)
        -> add_library_placeholder (Tests -> TcUnit)
        -> add_pou (GVL_TcUnit in Tests, with TcUnit_ResultExportXmlPath)
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

    finalise_fixture(scaffold)
    return 0


if __name__ == "__main__":
    sys.exit(main())
