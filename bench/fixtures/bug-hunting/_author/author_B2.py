"""Author the B2 sign/type bug-hunting fixture against a live bridge.

`FB_Counter.GetSignedDelta(a, b : INT) : UDINT` returns an unsigned
result where it should return signed. When ``b > a`` the subtraction
underflows to ~4 billion instead of yielding a negative value.

See ADR-0007 §"Task set (initial six)" §B2 and `_common.py` for the
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B2-sign-type"
SLN_NAME = "B2SignedDelta"
TESTS_PLC = "CounterTests"
LIBRARY_FB = "FB_Counter"
CONSUMER_FB = "FB_CounterConsumer"


LIBRARY_FB_DECL = f"""\
FUNCTION_BLOCK {LIBRARY_FB}
VAR
END_VAR
"""

# Bug: GetSignedDelta returns UDINT (unsigned). For a=5, b=7 the
# correct result is -2, but unsigned wraparound gives 4294967294.
# Fix: declare the return type as DINT.
GETSIGNEDDELTA_METHOD = """\
METHOD GetSignedDelta : UDINT
VAR_INPUT
    a : INT;
    b : INT;
END_VAR
GetSignedDelta := DINT_TO_UDINT(INT_TO_DINT(a) - INT_TO_DINT(b));
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
        f"    counter : {scaffold.library_plc}.{LIBRARY_FB};\n"
        "    delta : DINT;\n"
        "END_VAR\n"
        "delta := UDINT_TO_DINT(counter.GetSignedDelta(a := 5, b := 7));\n"
    )

    w = scaffold.writer
    check(
        f"add_pou({LIBRARY_FB})",
        w.add_pou(LIBRARY_FB, POUType.FUNCTION_BLOCK, LIBRARY_FB_DECL, plc_name=scaffold.library_plc),
    )
    check(
        "add_method(GetSignedDelta)",
        w.add_method(LIBRARY_FB, "GetSignedDelta", GETSIGNEDDELTA_METHOD, plc_name=scaffold.library_plc),
    )
    check(
        f"add_pou({CONSUMER_FB})",
        w.add_pou(CONSUMER_FB, POUType.FUNCTION_BLOCK, consumer_code, plc_name=scaffold.tests_plc),
    )

    finalise_fixture(scaffold)
    return 0


if __name__ == "__main__":
    sys.exit(main())
