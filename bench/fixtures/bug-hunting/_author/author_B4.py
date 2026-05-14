"""Author the B4 bError-propagation bug-hunting fixture against a live bridge.

`FB_PipelineStage` wraps an inner `FB_PipelineInner` that raises
``bError`` for a known-bad input. The outer FB never reads
``inner.bError``, so the consumer can't tell that the stage failed.

See ADR-0007 §"Task set (initial six)" §B4 and `_common.py` for the
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B4-bError"
SLN_NAME = "B4Pipeline"
TESTS_PLC = "PipelineTests"
INNER_FB = "FB_PipelineInner"
OUTER_FB = "FB_PipelineStage"
CONSUMER_FB = "FB_PipelineConsumer"


INNER_FB_DECL = f"""\
FUNCTION_BLOCK {INNER_FB}
VAR_INPUT
    value : INT;
END_VAR
VAR_OUTPUT
    bError : BOOL;
END_VAR
// Inner raises an error for any negative input.
bError := value < 0;
"""

OUTER_FB_DECL = f"""\
FUNCTION_BLOCK {OUTER_FB}
VAR_INPUT
    value : INT;
END_VAR
VAR_OUTPUT
    bError : BOOL;
END_VAR
VAR
    inner : {INNER_FB};
END_VAR
"""

# Bug: Step calls inner() but never propagates inner.bError to the
# outer's bError output. Consumer sees bError stuck at FALSE even
# when inner is reporting an error.
STEP_METHOD = """\
METHOD Step : INT
VAR
END_VAR
inner(value := value);
// MISSING: bError := inner.bError;
Step := value;
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
        f"    stage : {scaffold.library_plc}.{OUTER_FB};\n"
        "    bStageError : BOOL;\n"
        "END_VAR\n"
        "stage(value := -1);\n"
        "bStageError := stage.bError;\n"
    )

    w = scaffold.writer
    check(
        f"add_pou({INNER_FB})",
        w.add_pou(INNER_FB, POUType.FUNCTION_BLOCK, INNER_FB_DECL, plc_name=scaffold.library_plc),
    )
    check(
        f"add_pou({OUTER_FB})",
        w.add_pou(OUTER_FB, POUType.FUNCTION_BLOCK, OUTER_FB_DECL, plc_name=scaffold.library_plc),
    )
    check(
        "add_method(Step)",
        w.add_method(OUTER_FB, "Step", STEP_METHOD, plc_name=scaffold.library_plc),
    )
    check(
        f"add_pou({CONSUMER_FB})",
        w.add_pou(CONSUMER_FB, POUType.FUNCTION_BLOCK, consumer_code, plc_name=scaffold.tests_plc),
    )

    finalise_fixture(scaffold)
    return 0


if __name__ == "__main__":
    sys.exit(main())
