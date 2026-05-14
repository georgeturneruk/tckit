"""Author the B3 state-machine bug-hunting fixture against a live bridge.

`FB_TrafficLight.Step` cycles a CASE state machine. The transition
out of ``Green`` accidentally jumps to ``Red`` instead of ``Amber``,
breaking the standard Red -> RedAmber -> Green -> Amber -> Red cycle.

See ADR-0007 §"Task set (initial six)" §B3 and `_common.py` for the
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B3-state-machine"
SLN_NAME = "B3TrafficLight"
TESTS_PLC = "TrafficLightTests"
LIBRARY_FB = "FB_TrafficLight"
CONSUMER_FB = "FB_TrafficLightConsumer"


LIBRARY_FB_DECL = f"""\
FUNCTION_BLOCK {LIBRARY_FB}
VAR
    state : INT;  // 0=Red, 1=RedAmber, 2=Green, 3=Amber
END_VAR
"""

# Bug: the Green case (state=2) sets state := 0 (Red) instead of
# state := 3 (Amber). The light skips the Amber phase entirely on
# its way down.
STEP_METHOD = """\
METHOD Step : INT
VAR
END_VAR
CASE state OF
    0:
        state := 1;
    1:
        state := 2;
    2:
        state := 0;
    3:
        state := 0;
END_CASE;
Step := state;
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
        f"    light : {scaffold.library_plc}.{LIBRARY_FB};\n"
        "    lastState : INT;\n"
        "END_VAR\n"
        "lastState := light.Step();\n"
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
