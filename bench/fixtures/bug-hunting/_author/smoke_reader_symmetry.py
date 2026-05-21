"""Reader smoke for the ADR-0013 schema lift (alias DUT + GVLRef/DUTRef).

Offline: no bridge, no XAE. Lays down a hand-crafted PLC project tree
on disk (a .plcproj, plus a .TcPOU, .TcGVL, two .TcDUT files including
one alias) and asserts the XmlReader emits the new shape:

- ``DUTKind.ALIAS`` recognised; ``DUT.base_type`` populated for the
  alias and empty for struct/enum/union.
- ``PLCSection.gvls`` is a list of ``GVLRef`` with ``name`` /
  ``folder`` / ``path`` / ``plc_name``.
- ``PLCSection.duts`` is a list of ``DUTRef`` with ``dut_kind`` set.
- Folder paths follow the on-disk layout (relative to the .plcproj
  directory).

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
from tckit.ports.types import DUTKind  # noqa: E402

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "_smoke-reader-symmetry"
PLC_NAME = "ReaderSymmetry"
PLC_DIR = FIXTURE_DIR / PLC_NAME


PLCPROJ_XML = """\
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup>
    <Compile Include="POUs/MAIN.TcPOU"><SubType>Code</SubType></Compile>
    <Compile Include="POUs/Settings/GVL_Settings.TcGVL"><SubType>Code</SubType></Compile>
    <Compile Include="DUTs/ST_Config.TcDUT"><SubType>Code</SubType></Compile>
    <Compile Include="DUTs/Types/Counter.TcDUT"><SubType>Code</SubType></Compile>
  </ItemGroup>
</Project>
"""

MAIN_POU_XML = """\
<?xml version="1.0" encoding="utf-8"?>
<TcPlcObject Version="1.1.0.1">
  <POU Name="MAIN" Id="{00000000-0000-0000-0000-000000000001}" SpecialFunc="None">
    <Declaration><![CDATA[PROGRAM MAIN
VAR
END_VAR]]></Declaration>
    <Implementation><Code><![CDATA[;]]></Code></Implementation>
  </POU>
</TcPlcObject>
"""

GVL_XML = """\
<?xml version="1.0" encoding="utf-8"?>
<TcPlcObject Version="1.1.0.1">
  <GVL Name="GVL_Settings" Id="{00000000-0000-0000-0000-000000000002}">
    <Declaration><![CDATA[VAR_GLOBAL CONSTANT
    cMaxSpeed : LREAL := 1000.0;
END_VAR]]></Declaration>
  </GVL>
</TcPlcObject>
"""

STRUCT_DUT_XML = """\
<?xml version="1.0" encoding="utf-8"?>
<TcPlcObject Version="1.1.0.1">
  <DUT Name="ST_Config" Id="{00000000-0000-0000-0000-000000000003}">
    <Declaration><![CDATA[TYPE ST_Config :
STRUCT
    timeoutMs : UDINT := 250;
    enabled   : BOOL := FALSE;
END_STRUCT
END_TYPE]]></Declaration>
  </DUT>
</TcPlcObject>
"""

ALIAS_DUT_XML = """\
<?xml version="1.0" encoding="utf-8"?>
<TcPlcObject Version="1.1.0.1">
  <DUT Name="Counter" Id="{00000000-0000-0000-0000-000000000004}">
    <Declaration><![CDATA[TYPE Counter : UINT; END_TYPE]]></Declaration>
  </DUT>
</TcPlcObject>
"""


def _fail(msg: str) -> None:
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def _ok(label: str) -> None:
    print(f"OK   {label}")


def _wipe(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path, ignore_errors=True)


def _write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main() -> int:
    _wipe(FIXTURE_DIR)
    PLC_DIR.mkdir(parents=True, exist_ok=True)

    _write(PLC_DIR / f"{PLC_NAME}.plcproj", PLCPROJ_XML)
    _write(PLC_DIR / "POUs" / "MAIN.TcPOU", MAIN_POU_XML)
    _write(PLC_DIR / "POUs" / "Settings" / "GVL_Settings.TcGVL", GVL_XML)
    _write(PLC_DIR / "DUTs" / "ST_Config.TcDUT", STRUCT_DUT_XML)
    _write(PLC_DIR / "DUTs" / "Types" / "Counter.TcDUT", ALIAS_DUT_XML)

    reader = XmlReader()
    structure = reader.get_structure(str(FIXTURE_DIR), plc_name=PLC_NAME)
    section = structure.plcs[PLC_NAME]

    # GVLRef has the same shape as POURef. The settings GVL lives one
    # folder deep, so the folder field must round-trip.
    gvls = {g.name: g for g in section.gvls}
    if "GVL_Settings" not in gvls:
        _fail(f"GVL_Settings missing from section.gvls: {list(gvls)}")
    settings = gvls["GVL_Settings"]
    if settings.folder != "POUs/Settings":
        _fail(f"GVL_Settings.folder = {settings.folder!r}, expected 'POUs/Settings'")
    if settings.plc_name != PLC_NAME:
        _fail(f"GVL_Settings.plc_name = {settings.plc_name!r}")
    if not settings.path.endswith("GVL_Settings.TcGVL"):
        _fail(f"GVL_Settings.path looks wrong: {settings.path!r}")
    _ok("GVLRef carries name + folder + path + plc_name")

    # DUTRef carries dut_kind so callers can prefilter without re-parsing.
    duts = {d.name: d for d in section.duts}
    if "ST_Config" not in duts or "Counter" not in duts:
        _fail(f"DUTs missing from section.duts: {list(duts)}")

    st_config = duts["ST_Config"]
    if st_config.dut_kind != DUTKind.STRUCT:
        _fail(f"ST_Config.dut_kind = {st_config.dut_kind!r}, expected STRUCT")
    if st_config.folder != "DUTs":
        _fail(f"ST_Config.folder = {st_config.folder!r}, expected 'DUTs'")
    _ok("DUTRef recognises struct + folder")

    counter = duts["Counter"]
    if counter.dut_kind != DUTKind.ALIAS:
        _fail(f"Counter.dut_kind = {counter.dut_kind!r}, expected ALIAS")
    if counter.folder != "DUTs/Types":
        _fail(f"Counter.folder = {counter.folder!r}, expected 'DUTs/Types'")
    _ok("DUTRef recognises alias kind + nested folder")

    # get_dut populates dut_kind + base_type on the full DUT dataclass.
    dut = reader.get_dut("Counter", plc_name=PLC_NAME)
    if dut.dut_kind != DUTKind.ALIAS:
        _fail(f"get_dut(Counter).dut_kind = {dut.dut_kind!r}")
    if dut.base_type != "UINT":
        _fail(f"get_dut(Counter).base_type = {dut.base_type!r}, expected 'UINT'")
    _ok("get_dut(alias) returns base_type='UINT'")

    dut = reader.get_dut("ST_Config", plc_name=PLC_NAME)
    if dut.dut_kind != DUTKind.STRUCT:
        _fail(f"get_dut(ST_Config).dut_kind = {dut.dut_kind!r}")
    if dut.base_type != "":
        _fail(f"get_dut(ST_Config).base_type = {dut.base_type!r}, expected empty")
    _ok("get_dut(struct) leaves base_type empty")

    print()
    print("Reader symmetry smoke complete.")
    print(f"Clean up the fixture with: rm -rf {FIXTURE_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
