"""Round-trip smoke for ADR-0013 folder organisation (Wave 5).

Exercises ``add_folder`` plus ``parent_folder`` threading across every
``add_*`` tool, then reads the on-disk project back to confirm the
folder layout round-tripped.

Coverage:
- ``add_folder`` under POUs and DUTs.
- Nested folder via repeated ``add_folder`` calls.
- ``parent_folder`` on ``add_pou`` / ``add_gvl`` / ``add_dut`` /
  ``add_method`` / ``add_property``.
- Refusal: ``add_folder`` with a missing intermediate segment.

Prereqs: bridge reachable; TcXaeShell available.
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "_smoke-folders"
SLN_NAME = "SmokeFolders"
PLC = f"{SLN_NAME}_Plc"

FB_DRIVE_DECL = """\
FUNCTION_BLOCK FB_Drive
VAR
    _enabled : BOOL;
END_VAR
"""

FB_MOTOR_DECL = """\
FUNCTION_BLOCK FB_Motor
VAR
END_VAR
"""

GVL_DRIVE_DECL = """\
VAR_GLOBAL
    cMaxRpm : DINT := 3000;
END_VAR
"""

DUT_DRIVE_DECL = """\
TYPE ST_DriveConfig :
STRUCT
    accelMs : UDINT := 250;
    decelMs : UDINT := 250;
END_STRUCT
END_TYPE
"""

METHOD_DECL = """\
METHOD Enable : BOOL
_enabled := TRUE;
Enable := _enabled;
"""

PROPERTY_GETTER = "Enabled := _enabled;\n"


def _fail(msg: str) -> None:
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def _ok(label: str) -> None:
    print(f"OK   {label}")


def _check(label: str, result: Result) -> None:
    if not result.success:
        _fail(f"[{label}] {result.error}")
    _ok(label)


def _expect_failure(label: str, result: Result, expected_substring: str) -> None:
    if result.success:
        _fail(f"[{label}] expected failure, got success")
    if expected_substring.lower() not in (result.error or "").lower():
        _fail(
            f"[{label}] expected error containing {expected_substring!r}, "
            f"got: {result.error!r}"
        )
    _ok(f"{label} (refused as expected: {result.error!r})")


def _wipe_fixture(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path, ignore_errors=True)


def main() -> int:
    client = BridgeClient()
    if not client.health():
        _fail(f"Bridge not reachable at {client.base_url}. Start the bridge first.")

    writer = AutomationWriter(client=client)
    reader = XmlReader()

    _wipe_fixture(FIXTURE_DIR)
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)

    _check("create_project", writer.create_project(SLN_NAME, str(FIXTURE_DIR)))
    sln_path = FIXTURE_DIR / f"{SLN_NAME}.sln"
    _check("open_project", writer.open_project(str(sln_path)))

    # Create the folder layout: POUs/Drives, POUs/Drives/Motors, DUTs/Custom.
    _check(
        "add_folder(Drives under POUs)",
        writer.add_folder("Drives", parent_path="POUs", plc_name=PLC),
    )
    _check(
        "add_folder(Motors under POUs/Drives)",
        writer.add_folder("Motors", parent_path="POUs/Drives", plc_name=PLC),
    )
    _check(
        "add_folder(Custom under DUTs)",
        writer.add_folder("Custom", parent_path="DUTs", plc_name=PLC),
    )

    # Missing intermediate folder must fail cleanly.
    _expect_failure(
        "add_folder(Nested under POUs/NoSuchParent) fails fast",
        writer.add_folder("Nested", parent_path="POUs/NoSuchParent", plc_name=PLC),
        "not found",
    )

    # Populate each folder with the symbol the parent_folder argument targets.
    _check(
        "add_pou(FB_Drive into POUs/Drives)",
        writer.add_pou(
            "FB_Drive",
            POUType.FUNCTION_BLOCK,
            FB_DRIVE_DECL,
            parent_folder="Drives",
            plc_name=PLC,
        ),
    )
    _check(
        "add_pou(FB_Motor into POUs/Drives/Motors)",
        writer.add_pou(
            "FB_Motor",
            POUType.FUNCTION_BLOCK,
            FB_MOTOR_DECL,
            parent_folder="Drives/Motors",
            plc_name=PLC,
        ),
    )
    _check(
        "add_gvl(GVL_Drive into POUs/Drives)",
        writer.add_gvl(
            "GVL_Drive",
            GVL_DRIVE_DECL,
            parent_folder="Drives",
            plc_name=PLC,
        ),
    )
    _check(
        "add_dut(ST_DriveConfig into DUTs/Custom)",
        writer.add_dut(
            "ST_DriveConfig",
            DUT_DRIVE_DECL,
            dut_kind=DUTKind.STRUCT,
            parent_folder="Custom",
            plc_name=PLC,
        ),
    )
    _check(
        "add_method(FB_Drive.Enable, parent_folder=Drives)",
        writer.add_method(
            "FB_Drive",
            "Enable",
            METHOD_DECL,
            parent_folder="Drives",
            plc_name=PLC,
        ),
    )
    _check(
        "add_property(FB_Drive.Enabled, parent_folder=Drives)",
        writer.add_property(
            "FB_Drive",
            "Enabled",
            "BOOL",
            getter_code=PROPERTY_GETTER,
            parent_folder="Drives",
            plc_name=PLC,
        ),
    )

    # Round-trip check via the reader: folder paths must reflect on-disk layout.
    structure = reader.get_structure(str(FIXTURE_DIR), plc_name=PLC)
    section = structure.plcs[PLC]

    pous = {p.name: p for p in section.pous}
    if "FB_Drive" not in pous:
        _fail(f"FB_Drive missing from pous: {list(pous)}")
    if pous["FB_Drive"].folder != "POUs/Drives":
        _fail(
            f"FB_Drive.folder = {pous['FB_Drive'].folder!r}, "
            "expected 'POUs/Drives'"
        )
    _ok("round-trip: FB_Drive lands at POUs/Drives")

    if "FB_Motor" not in pous:
        _fail(f"FB_Motor missing from pous: {list(pous)}")
    if pous["FB_Motor"].folder != "POUs/Drives/Motors":
        _fail(
            f"FB_Motor.folder = {pous['FB_Motor'].folder!r}, "
            "expected 'POUs/Drives/Motors'"
        )
    _ok("round-trip: FB_Motor lands at POUs/Drives/Motors (nested)")

    gvls = {g.name: g for g in section.gvls}
    if "GVL_Drive" not in gvls:
        _fail(f"GVL_Drive missing from gvls: {list(gvls)}")
    if gvls["GVL_Drive"].folder != "POUs/Drives":
        _fail(
            f"GVL_Drive.folder = {gvls['GVL_Drive'].folder!r}, "
            "expected 'POUs/Drives'"
        )
    _ok("round-trip: GVL_Drive lands at POUs/Drives")

    duts = {d.name: d for d in section.duts}
    if "ST_DriveConfig" not in duts:
        _fail(f"ST_DriveConfig missing from duts: {list(duts)}")
    if duts["ST_DriveConfig"].folder != "DUTs/Custom":
        _fail(
            f"ST_DriveConfig.folder = {duts['ST_DriveConfig'].folder!r}, "
            "expected 'DUTs/Custom'"
        )
    _ok("round-trip: ST_DriveConfig lands at DUTs/Custom")

    # Method + property landed on FB_Drive specifically; the parent_folder
    # hint shouldn't have routed them anywhere else.
    iface = reader.get_pou_interface("FB_Drive", plc_name=PLC)
    if not any(m.name == "Enable" for m in iface.methods):
        _fail(
            f"FB_Drive.Enable method missing: {[m.name for m in iface.methods]}"
        )
    if not any(p.name == "Enabled" for p in iface.properties):
        _fail(
            f"FB_Drive.Enabled property missing: {[p.name for p in iface.properties]}"
        )
    _ok("round-trip: FB_Drive carries Enable method + Enabled property")

    print()
    print("Folder organisation smoke complete: wave 5 round-trip clean.")
    print(f"Clean up the throwaway fixture with: rm -rf {FIXTURE_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
