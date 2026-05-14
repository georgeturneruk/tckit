"""Author the B5 default-initialisation bug-hunting fixture against a live bridge.

`FB_PIDController.VAR` initialises ``fGain`` to ``0.0`` where it
should be ``1.0`` (multiplicative identity). The first ``Step``
call returns zero regardless of the error input.

See ADR-0007 §"Task set (initial six)" §B5 and `_common.py` for the
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B5-default-init"
SLN_NAME = "B5PIDController"
TESTS_PLC = "PIDControllerTests"
LIBRARY_FB = "FB_PIDController"
CONSUMER_FB = "FB_PIDControllerConsumer"


# Bug seed: fGain defaults to 0.0 where the multiplicative identity
# (1.0) is what gives a sensible first-call output. Step() multiplies
# the error by fGain, so any non-zero error returns 0.0 on the first
# call until the consumer explicitly sets fGain.
LIBRARY_FB_DECL = f"""\
FUNCTION_BLOCK {LIBRARY_FB}
VAR_INPUT
    fError : REAL;
END_VAR
VAR_OUTPUT
    fOutput : REAL;
END_VAR
VAR
    fGain : REAL := 0.0;
END_VAR
"""

STEP_METHOD = """\
METHOD Step : REAL
fOutput := fError * fGain;
Step := fOutput;
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
        f"    controller : {scaffold.library_plc}.{LIBRARY_FB};\n"
        "    output : REAL;\n"
        "END_VAR\n"
        "controller(fError := 2.5);\n"
        "output := controller.Step();\n"
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
