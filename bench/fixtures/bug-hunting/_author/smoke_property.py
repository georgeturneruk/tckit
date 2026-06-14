"""Round-trip smoke for `add_property` (and `add_dut`) against the bridge.

Catches regressions in the bridge -> COM tree path for property and
DUT authoring without needing a bench run, build, deploy, or runtime.
Just authors a handful of properties + one DUT against a throwaway
project, then reads the .TcPOU XML back from disk and asserts the
shape.

Why this smoke exists: `add_property` and `add_dut` shipped with
unit tests against a fake bridge (tests/unit/test_automation_writer.py)
but no end-to-end exercise against XAE. `author_T2.py` defers all
property authoring to the bench-arm LLM, so commit c32dfd7 did NOT
actually hit the bridge -> COM property path. This smoke closes
that gap.

Prerequisites:
- Bridge service reachable at $BRIDGE_URL (default localhost:8765).
- TcXaeShell available for the bridge to drive (the bridge handles
  attach / headless mode itself).

Exits 0 on success, non-zero on the first failing assertion.
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tckit.adapters.readers.xml_reader import XmlReader  # noqa: E402
from tckit.adapters.writers.automation_writer import AutomationWriter  # noqa: E402
from tckit.ports.types import DUTKind, POUType, Result  # noqa: E402
from tckit.utils.bridge_client import BridgeClient  # noqa: E402

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "_smoke-property"
SLN_NAME = "PropertySmoke"
LIBRARY_PLC = f"{SLN_NAME}_Plc"
FB_NAME = "FB_PropertySmoke"
DUT_NAME = "E_PropertyMode"

FB_DECL = f"""\
FUNCTION_BLOCK {FB_NAME}
VAR
    _readonly  : LREAL := 42.0;
    _validated : LREAL;
    _bumps     : DINT;
END_VAR
"""

# Getter only. Returns the backing field. Verifies the setter_code=None
# branch through the bridge.
READONLY_GETTER = """\
Readonly := _readonly;
"""

# Setter only. The body sees the new value via the implicit local
# named after the property (`Bumps`), but here we ignore the value and
# just bump a counter so the side-effect is observable. Verifies the
# getter_code=None branch.
BUMPS_SETTER = """\
_bumps := _bumps + 1;
"""

# Both accessors with a non-trivial setter (rejects negatives). Mirrors
# `SetterRejectsNegativeKp` in the T2-pid-anti-windup fixture.
VALIDATED_GETTER = """\
Validated := _validated;
"""

VALIDATED_SETTER = """\
IF Validated >= 0 THEN
    _validated := Validated;
END_IF
"""

DUT_CODE = f"""\
TYPE {DUT_NAME} :
(
    DIRECT := 0,
    REVERSE := 1
);
END_TYPE
"""


def _fail(msg: str) -> None:
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def _check(label: str, result: Result) -> None:
    if not result.success:
        _fail(f"[{label}] {result.error}")
    print(f"OK   [{label}]")


def _expect_failure(label: str, result: Result, expected_substring: str) -> None:
    if result.success:
        _fail(f"[{label}] expected failure, got success")
    if expected_substring not in (result.error or ""):
        _fail(
            f"[{label}] expected error containing {expected_substring!r}, "
            f"got: {result.error!r}"
        )
    print(f"OK   [{label}] (failed as expected: {result.error!r})")


def _wipe_fixture(fixture_dir: Path) -> None:
    """Remove any prior smoke output so the bridge can create a fresh project."""
    if fixture_dir.exists():
        shutil.rmtree(fixture_dir, ignore_errors=True)


def main() -> int:
    client = BridgeClient()
    if not client.health():
        _fail(f"Bridge not reachable at {client.base_url}. Start the bridge first.")

    writer = AutomationWriter(client=client)

    _wipe_fixture(FIXTURE_DIR)
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)

    _check("create_project", writer.create_project(SLN_NAME, str(FIXTURE_DIR)))

    sln_path = FIXTURE_DIR / f"{SLN_NAME}.sln"
    _check("open_project", writer.open_project(str(sln_path)))

    _check(
        f"add_pou({FB_NAME})",
        writer.add_pou(FB_NAME, POUType.FUNCTION_BLOCK, FB_DECL, plc_name=LIBRARY_PLC),
    )

    # Property 1: getter only.
    _check(
        "add_property(Readonly, getter only)",
        writer.add_property(
            FB_NAME,
            "Readonly",
            "LREAL",
            getter_code=READONLY_GETTER,
            plc_name=LIBRARY_PLC,
        ),
    )

    # Property 2: setter only.
    _check(
        "add_property(Bumps, setter only)",
        writer.add_property(
            FB_NAME,
            "Bumps",
            "DINT",
            setter_code=BUMPS_SETTER,
            plc_name=LIBRARY_PLC,
        ),
    )

    # Property 3: both accessors with non-trivial setter logic.
    _check(
        "add_property(Validated, both accessors)",
        writer.add_property(
            FB_NAME,
            "Validated",
            "LREAL",
            getter_code=VALIDATED_GETTER,
            setter_code=VALIDATED_SETTER,
            plc_name=LIBRARY_PLC,
        ),
    )

    # Negative case: no accessors at all must fail with a clear error.
    _expect_failure(
        "add_property(NoAccessors) rejects empty",
        writer.add_property(
            FB_NAME,
            "NoAccessors",
            "INT",
            plc_name=LIBRARY_PLC,
        ),
        "at least one of",
    )

    # DUT: enum. Parallel coverage for the other surface that shipped in
    # the same commit as add_property and has the same "unit tests only"
    # status.
    _check(
        f"add_dut({DUT_NAME}, ENUM)",
        writer.add_dut(DUT_NAME, DUT_CODE, dut_kind=DUTKind.ENUM, plc_name=LIBRARY_PLC),
    )

    # Flush in-memory tree state to .TcPOU / .TcDUT on disk so the XML
    # reader can see the writes. SaveAsLibrary persists the PLC project
    # source as a side effect; install=False keeps the system library
    # repository untouched (this smoke is throwaway).
    library_artefact = FIXTURE_DIR / f"{LIBRARY_PLC}.library"
    _check(
        "save_plc_as_library (flush to disk)",
        writer.save_plc_as_library(LIBRARY_PLC, str(library_artefact), install=False),
    )

    # Round-trip read: parse the .TcPOU XML and assert each property
    # round-tripped with the expected accessor presence and return type.
    reader = XmlReader()
    reader.get_structure(str(FIXTURE_DIR), plc_name=LIBRARY_PLC)
    interface = reader.get_pou_interface(FB_NAME, plc_name=LIBRARY_PLC)

    properties = {p.name: p for p in interface.properties}
    expected = {
        "Readonly": ("LREAL", True, False),
        "Bumps": ("DINT", False, True),
        "Validated": ("LREAL", True, True),
    }
    for name, (return_type, has_get, has_set) in expected.items():
        if name not in properties:
            _fail(
                f"round-trip: property {name!r} missing from FB_PropertySmoke. "
                f"Found: {sorted(properties)}"
            )
        prop = properties[name]
        if prop.return_type != return_type:
            _fail(
                f"round-trip: property {name!r} return_type "
                f"{prop.return_type!r} != expected {return_type!r}"
            )
        if prop.has_get != has_get:
            _fail(
                f"round-trip: property {name!r} has_get={prop.has_get} "
                f"!= expected {has_get}"
            )
        if prop.has_set != has_set:
            _fail(
                f"round-trip: property {name!r} has_set={prop.has_set} "
                f"!= expected {has_set}"
            )
        print(
            f"OK   round-trip {name} : {return_type} "
            f"(has_get={has_get}, has_set={has_set})"
        )

    # The negative-case property must not have leaked into the tree.
    if "NoAccessors" in properties:
        _fail(
            "round-trip: NoAccessors was rejected by the port but somehow "
            "ended up in the tree."
        )

    # DUT round-trip.
    dut = reader.get_dut(DUT_NAME, plc_name=LIBRARY_PLC)
    if "REVERSE := 1" not in dut.declaration:
        _fail(
            f"round-trip: DUT {DUT_NAME} declaration missing expected enum "
            f"value. Got:\n{dut.declaration}"
        )
    print(f"OK   round-trip DUT {DUT_NAME} declaration carries enum values")

    print()
    print("Smoke complete: add_property + add_dut survive the bridge -> COM -> disk round-trip.")
    print(f"Clean up the throwaway fixture with: rm -rf {FIXTURE_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
