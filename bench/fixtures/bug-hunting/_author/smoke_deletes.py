"""Round-trip smoke for ADR-0013 deletes (Waves 1-3) against the bridge.

Exercises the per-item delete tools end-to-end:

  Wave 1: delete_pou (incl. task-bound PROGRAM refusal)
  Wave 2: delete_method, delete_property, delete_gvl, delete_dut
  Wave 3: delete_variable (single-name + multi-name refusal),
          delete_folder (refuses non-empty, succeeds with recursive=True)

After each mutation the on-disk .plcproj / .TcPOU / .TcGVL / .TcDUT
state is re-read via XmlReader to verify the change round-tripped.

Prereqs: same as smoke_property.py (bridge reachable; TcXaeShell).
Exits 0 on success, non-zero on the first failing assertion.
"""

from __future__ import annotations

import os
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

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "_smoke-deletes"
SLN_NAME = "SmokeDeletes"
PLC = f"{SLN_NAME}_Plc"

FB_DOOMED_DECL = """\
FUNCTION_BLOCK FB_Doomed
VAR
    x : INT;
END_VAR
"""

FB_METHODS_DECL = """\
FUNCTION_BLOCK FB_Methods
VAR
    _status : INT;
END_VAR
"""

METHOD_DECL = """\
METHOD DoIt : BOOL
VAR_INPUT
    n : INT;
END_VAR
DoIt := n > 0;
"""

PROPERTY_GETTER = "Status := _status;\n"

GVL_DOOMED_DECL = """\
VAR_GLOBAL
    counter : DINT := 0;
END_VAR
"""

DUT_DOOMED_DECL = """\
TYPE ST_Doomed :
STRUCT
    a : INT;
    b : BOOL;
END_STRUCT
END_TYPE
"""

FB_VARS_DECL = """\
FUNCTION_BLOCK FB_Vars
VAR
    bToRemove : BOOL := FALSE;
    nKeep     : INT  := 1;
END_VAR
"""

MULTI_NAME_DECL = """\
FUNCTION_BLOCK FB_MultiVar
VAR
    bA, bB : BOOL := FALSE;
END_VAR
"""

FB_DRIVER_DECL = """\
FUNCTION_BLOCK FB_Driver
VAR
END_VAR
"""


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


def _section(reader: XmlReader):
    structure = reader.get_structure(str(FIXTURE_DIR), plc_name=PLC)
    return structure.plcs[PLC]


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
    os.environ["PLC_PROJECT_PATH"] = str(sln_path)

    # --- Wave 1: delete_pou ------------------------------------------------

    _check(
        "add_pou(FB_Doomed)",
        writer.add_pou(
            "FB_Doomed", POUType.FUNCTION_BLOCK, FB_DOOMED_DECL, plc_name=PLC
        ),
    )
    _check("delete_pou(FB_Doomed)", writer.delete_pou("FB_Doomed", plc_name=PLC))
    pous = [p.name for p in _section(reader).pous]
    if "FB_Doomed" in pous:
        _fail(f"FB_Doomed still listed after delete_pou: {pous}")
    _ok("round-trip: FB_Doomed gone from section.pous")

    # MAIN is the default PROGRAM `create_project` lays down and PlcTask
    # references it via <PouCall><Name>MAIN</Name></PouCall>. Deletion must
    # refuse and surface the offending task name.
    _expect_failure(
        "delete_pou(MAIN) refuses task-bound PROGRAM",
        writer.delete_pou("MAIN", plc_name=PLC),
        "PlcTask",
    )

    # --- Wave 2: delete_method / property / gvl / dut ---------------------

    _check(
        "add_pou(FB_Methods)",
        writer.add_pou(
            "FB_Methods", POUType.FUNCTION_BLOCK, FB_METHODS_DECL, plc_name=PLC
        ),
    )
    _check(
        "add_method(FB_Methods.DoIt)",
        writer.add_method("FB_Methods", "DoIt", METHOD_DECL, plc_name=PLC),
    )
    _check(
        "add_property(FB_Methods.Status)",
        writer.add_property(
            "FB_Methods",
            "Status",
            "INT",
            getter_code=PROPERTY_GETTER,
            plc_name=PLC,
        ),
    )

    _check(
        "delete_method(FB_Methods.DoIt)",
        writer.delete_method("FB_Methods", "DoIt", plc_name=PLC),
    )
    iface = reader.get_pou_interface("FB_Methods", plc_name=PLC)
    if any(m.name == "DoIt" for m in iface.methods):
        _fail(f"DoIt still in interface after delete: {[m.name for m in iface.methods]}")
    _ok("round-trip: DoIt gone from FB_Methods.methods")

    _check(
        "delete_property(FB_Methods.Status)",
        writer.delete_property("FB_Methods", "Status", plc_name=PLC),
    )
    iface = reader.get_pou_interface("FB_Methods", plc_name=PLC)
    if any(p.name == "Status" for p in iface.properties):
        _fail(
            f"Status still in properties: {[p.name for p in iface.properties]}"
        )
    _ok("round-trip: Status gone from FB_Methods.properties (Get/Set cascaded)")

    _check(
        "add_gvl(GVL_Doomed)",
        writer.add_gvl("GVL_Doomed", GVL_DOOMED_DECL, plc_name=PLC),
    )
    _check("delete_gvl(GVL_Doomed)", writer.delete_gvl("GVL_Doomed", plc_name=PLC))
    if any(g.name == "GVL_Doomed" for g in _section(reader).gvls):
        _fail("GVL_Doomed survived delete_gvl")
    _ok("round-trip: GVL_Doomed gone from section.gvls")

    _check(
        "add_dut(ST_Doomed)",
        writer.add_dut(
            "ST_Doomed", DUT_DOOMED_DECL, dut_kind=DUTKind.STRUCT, plc_name=PLC
        ),
    )
    _check("delete_dut(ST_Doomed)", writer.delete_dut("ST_Doomed", plc_name=PLC))
    if any(d.name == "ST_Doomed" for d in _section(reader).duts):
        _fail("ST_Doomed survived delete_dut")
    _ok("round-trip: ST_Doomed gone from section.duts")

    # Kind-validation refusal: delete_gvl against an FB name must fail.
    _expect_failure(
        "delete_gvl(FB_Methods) refuses non-GVL",
        writer.delete_gvl("FB_Methods", plc_name=PLC),
        "not a GVL",
    )

    # --- Wave 3: delete_variable ------------------------------------------

    _check(
        "add_pou(FB_Vars)",
        writer.add_pou(
            "FB_Vars", POUType.FUNCTION_BLOCK, FB_VARS_DECL, plc_name=PLC
        ),
    )
    _check(
        "delete_variable(FB_Vars.bToRemove)",
        writer.delete_variable("FB_Vars", "bToRemove", plc_name=PLC),
    )
    decl = reader.get_pou_declaration("FB_Vars", plc_name=PLC).declaration
    if "bToRemove" in decl:
        _fail(f"bToRemove still present in declaration:\n{decl}")
    if "nKeep" not in decl:
        _fail(f"nKeep accidentally removed:\n{decl}")
    _ok("round-trip: bToRemove gone, nKeep preserved")

    # Multi-name declaration is refused with a pointer at the patch primitive.
    _check(
        "add_pou(FB_MultiVar)",
        writer.add_pou(
            "FB_MultiVar",
            POUType.FUNCTION_BLOCK,
            MULTI_NAME_DECL,
            plc_name=PLC,
        ),
    )
    _expect_failure(
        "delete_variable(FB_MultiVar.bA) refuses multi-name",
        writer.delete_variable("FB_MultiVar", "bA", plc_name=PLC),
        "multi-name",
    )

    # --- Wave 3: delete_folder -------------------------------------------

    _check(
        "add_folder(Drives)",
        writer.add_folder("Drives", parent_path="POUs", plc_name=PLC),
    )
    _check(
        "add_pou(FB_Driver into Drives)",
        writer.add_pou(
            "FB_Driver",
            POUType.FUNCTION_BLOCK,
            FB_DRIVER_DECL,
            parent_folder="Drives",
            plc_name=PLC,
        ),
    )

    _expect_failure(
        "delete_folder(Drives) refuses non-empty",
        writer.delete_folder("Drives", parent_path="POUs", plc_name=PLC),
        "not empty",
    )

    _check(
        "delete_folder(Drives, recursive=True)",
        writer.delete_folder(
            "Drives", parent_path="POUs", recursive=True, plc_name=PLC
        ),
    )

    section = _section(reader)
    if any(p.name == "FB_Driver" for p in section.pous):
        _fail("FB_Driver survived recursive folder delete")
    if any("Drives" in (p.folder or "") for p in section.pous):
        _fail(
            f"Drives folder still referenced by POU folder paths: "
            f"{[(p.name, p.folder) for p in section.pous]}"
        )
    _ok("round-trip: Drives folder and FB_Driver both gone")

    print()
    print("Delete smokes complete: waves 1, 2, 3 survive bridge -> COM -> disk round-trip.")
    print(f"Clean up the throwaway fixture with: rm -rf {FIXTURE_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
