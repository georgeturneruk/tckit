"""Author the T1 Schmitt-trigger TDD fixture against a live bridge.

Unlike B1, T1 is a TDD task: `FB_SchmittTrigger.Step` is fully
declared (signature, VAR_INPUT, VAR_OUTPUT, hysteresis state) but
its method body is empty (just `;`). The accompanying TcUnit test
suite asserts five behaviours covering the Schmitt-trigger hysteresis
band; no hardcoded return value can satisfy all five, so the model
must implement the logic.

See ADR-0007 task T1 (under "Task set") and `_common.py` for the
shared scaffolding.
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "T1-schmitt-trigger"
SLN_NAME = "T1SchmittTrigger"
TESTS_PLC = "SchmittTriggerTests"
LIBRARY_FB = "FB_SchmittTrigger"
CONSUMER_FB = "FB_SchmittTriggerConsumer"


LIBRARY_FB_DECL = f"""\
FUNCTION_BLOCK {LIBRARY_FB}
VAR_INPUT
    fInput : REAL;
    fLowThreshold : REAL := 0.3;
    fHighThreshold : REAL := 0.7;
END_VAR
VAR_OUTPUT
    bState : BOOL;
END_VAR
"""

# Empty body — the TDD task is to implement Schmitt-trigger
# hysteresis: latch HIGH above fHighThreshold, latch LOW below
# fLowThreshold, hold previous state in the hysteresis band.
STEP_METHOD = """\
METHOD Step : BOOL
;
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
        f"    trigger : {scaffold.library_plc}.{LIBRARY_FB};\n"
        "    bLastState : BOOL;\n"
        "END_VAR\n"
        "trigger(fInput := 0.5);\n"
        "bLastState := trigger.Step();\n"
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
