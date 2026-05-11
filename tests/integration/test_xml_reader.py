"""Integration tests for XmlReader — runs against fixture files, no network."""

from pathlib import Path
from textwrap import dedent

import pytest

from tckit.adapters.readers.xml_reader import XmlReader
from tckit.ports.types import POUType


@pytest.fixture()
def reader(sample_project_path: Path) -> XmlReader:
    r = XmlReader()
    r.get_structure(str(sample_project_path))
    return r


# ---------------------------------------------------------------------------
# get_structure
# ---------------------------------------------------------------------------


def test_get_structure_finds_fb_example(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    names = [p.name for p in structure.pous]
    assert "FB_Example" in names


def test_get_structure_fb_example_type(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    fb = next(p for p in structure.pous if p.name == "FB_Example")
    assert fb.pou_type == POUType.FUNCTION_BLOCK


def test_get_structure_finds_interface(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    names = [p.name for p in structure.pous]
    assert "I_Example" in names


def test_get_structure_interface_type(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    itf = next(p for p in structure.pous if p.name == "I_Example")
    assert itf.pou_type == POUType.INTERFACE


def test_get_structure_finds_gvl_params(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    assert "GVL_Params" in structure.gvls


def test_get_structure_finds_struct_dut(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    assert "ST_ExampleConfig" in structure.duts


def test_get_structure_finds_enum_dut(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    assert "E_ExampleState" in structure.duts


# ---------------------------------------------------------------------------
# get_pou_interface — function block
# ---------------------------------------------------------------------------


def test_get_pou_interface_returns_methods(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    method_names = [m.name for m in interface.methods]
    assert "Execute" in method_names
    assert "Reset" in method_names


def test_get_pou_interface_execute_return_type(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    execute = next(m for m in interface.methods if m.name == "Execute")
    assert execute.return_type == "BOOL"


def test_get_pou_interface_has_declaration(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    assert "VAR_INPUT" in interface.declaration


def test_get_pou_interface_pou_type(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    assert interface.pou_type == POUType.FUNCTION_BLOCK


def test_get_pou_interface_has_property(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    prop_names = [p.name for p in interface.properties]
    assert "ErrorId" in prop_names


def test_get_pou_interface_property_return_type(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    prop = next(p for p in interface.properties if p.name == "ErrorId")
    assert prop.return_type == "UDINT"


def test_get_pou_interface_property_has_get_and_set(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    prop = next(p for p in interface.properties if p.name == "ErrorId")
    assert prop.has_get is True
    assert prop.has_set is True


# ---------------------------------------------------------------------------
# get_pou_interface — interface (Itf element)
# ---------------------------------------------------------------------------


def test_get_pou_interface_itf_type(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("I_Example")
    assert interface.pou_type == POUType.INTERFACE


def test_get_pou_interface_itf_has_methods(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("I_Example")
    method_names = [m.name for m in interface.methods]
    assert "Execute" in method_names
    assert "Reset" in method_names


def test_get_pou_interface_itf_has_property(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("I_Example")
    prop_names = [p.name for p in interface.properties]
    assert "Status" in prop_names


def test_get_pou_interface_itf_property_has_get_no_set(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("I_Example")
    prop = next(p for p in interface.properties if p.name == "Status")
    assert prop.has_get is True
    assert prop.has_set is False


# ---------------------------------------------------------------------------
# get_pou_interface — local-VAR stripping
# ---------------------------------------------------------------------------


def test_get_pou_interface_strips_method_locals(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    method = next(m for m in interface.methods if m.name == "CalculateInternal")
    # Locals/temp/constant are implementation detail and must not leak.
    assert "nScratch" not in method.declaration
    assert "nTempResult" not in method.declaration
    assert "nMaxValue" not in method.declaration


def test_get_pou_interface_preserves_method_api(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Example")
    method = next(m for m in interface.methods if m.name == "CalculateInternal")
    # API surface must survive the strip.
    assert "VAR_INPUT" in method.declaration
    assert "nFactor" in method.declaration
    assert "VAR_OUTPUT" in method.declaration
    assert "bOverflow" in method.declaration
    assert "// :Description:" in method.declaration


# ---------------------------------------------------------------------------
# get_pou_item — methods and properties
# ---------------------------------------------------------------------------


def test_get_pou_item_execute_body(reader: XmlReader) -> None:
    item = reader.get_pou_item("FB_Example", "Execute")
    assert "bDone" in item.body


def test_get_pou_item_reset_body(reader: XmlReader) -> None:
    item = reader.get_pou_item("FB_Example", "Reset")
    assert "eState" in item.body


def test_get_pou_item_execute_has_declaration(reader: XmlReader) -> None:
    item = reader.get_pou_item("FB_Example", "Execute")
    assert "METHOD Execute" in item.declaration


def test_get_pou_item_keeps_method_locals(reader: XmlReader) -> None:
    # Inverse of the interface strip: the item-level call must include locals
    # so the caller can read the implementation faithfully.
    item = reader.get_pou_item("FB_Example", "CalculateInternal")
    assert "nScratch" in item.declaration
    assert "nTempResult" in item.declaration
    assert "nMaxValue" in item.declaration


def test_get_pou_item_property_get_body(reader: XmlReader) -> None:
    item = reader.get_pou_item("FB_Example", "ErrorId.Get")
    assert "nErrorId" in item.body


def test_get_pou_item_property_set_body(reader: XmlReader) -> None:
    item = reader.get_pou_item("FB_Example", "ErrorId.Set")
    assert "nErrorId" in item.body


def test_get_pou_item_property_bare_returns_declaration(reader: XmlReader) -> None:
    item = reader.get_pou_item("FB_Example", "ErrorId")
    assert "PROPERTY ErrorId" in item.declaration
    assert item.body == ""


def test_get_pou_item_missing_accessor_raises(reader: XmlReader) -> None:
    # I_Example.Status only has Get, not Set
    with pytest.raises(FileNotFoundError):
        reader.get_pou_item("I_Example", "Status.Set")


# ---------------------------------------------------------------------------
# get_gvl
# ---------------------------------------------------------------------------


def test_get_gvl_returns_declaration(reader: XmlReader) -> None:
    gvl = reader.get_gvl("GVL_Params")
    assert "nMaxRetries" in gvl.declaration


def test_get_gvl_has_name(reader: XmlReader) -> None:
    gvl = reader.get_gvl("GVL_Params")
    assert gvl.name == "GVL_Params"


# ---------------------------------------------------------------------------
# get_dut
# ---------------------------------------------------------------------------


def test_get_dut_struct_declaration(reader: XmlReader) -> None:
    dut = reader.get_dut("ST_ExampleConfig")
    assert "STRUCT" in dut.declaration
    assert "nMaxRetries" in dut.declaration


def test_get_dut_enum_declaration(reader: XmlReader) -> None:
    dut = reader.get_dut("E_ExampleState")
    assert "E_ExampleState" in dut.declaration
    assert "Running" in dut.declaration


def test_get_dut_has_name(reader: XmlReader) -> None:
    dut = reader.get_dut("ST_ExampleConfig")
    assert dut.name == "ST_ExampleConfig"


def test_get_dut_has_path(reader: XmlReader) -> None:
    dut = reader.get_dut("ST_ExampleConfig")
    assert dut.path.endswith(".TcDUT")


# ---------------------------------------------------------------------------
# Error handling
# ---------------------------------------------------------------------------


def test_unknown_pou_raises(reader: XmlReader) -> None:
    with pytest.raises(FileNotFoundError):
        reader.get_pou_interface("NonExistentPOU")


def test_unknown_item_raises(reader: XmlReader) -> None:
    with pytest.raises(FileNotFoundError):
        reader.get_pou_item("FB_Example", "NonExistentMethod")


def test_unknown_dut_raises(reader: XmlReader) -> None:
    with pytest.raises(FileNotFoundError):
        reader.get_dut("NonExistentDUT")


def test_get_structure_bad_path_raises() -> None:
    reader = XmlReader()
    with pytest.raises(FileNotFoundError):
        reader.get_structure("/nonexistent/path/to/project")


# ---------------------------------------------------------------------------
# get_structure — tasks, libraries, folder (ADR-0002)
# ---------------------------------------------------------------------------


def test_get_structure_extracts_tctto_task(sample_project_path: Path) -> None:
    structure = XmlReader().get_structure(str(sample_project_path))
    plc_task = next(t for t in structure.tasks if t.name == "PlcTask")
    assert plc_task.cycle_time_us == 10000
    assert plc_task.priority == 20
    assert plc_task.programs == ["PRG_MAIN"]


def test_get_structure_merges_tsproj_only_tasks(sample_project_path: Path) -> None:
    # SlowTask exists in .tsproj but has no .TcTTO; it should still appear,
    # with the .tsproj CycleTime converted from 100ns ticks to microseconds.
    structure = XmlReader().get_structure(str(sample_project_path))
    slow = next(t for t in structure.tasks if t.name == "SlowTask")
    assert slow.cycle_time_us == 50000
    assert slow.priority == 30
    assert slow.programs == []


def test_get_structure_extracts_placeholder_libraries(sample_project_path: Path) -> None:
    structure = XmlReader().get_structure(str(sample_project_path))
    by_name = {lib.name: lib for lib in structure.libraries}
    assert by_name["Tc2_Standard"].placeholder == "Tc2_Standard"
    assert by_name["Tc2_Standard"].version == "*"
    assert by_name["Tc2_System"].version == "3.4.20.0"


def test_get_structure_extracts_direct_library_reference(
    sample_project_path: Path,
) -> None:
    structure = XmlReader().get_structure(str(sample_project_path))
    base = next(lib for lib in structure.libraries if lib.name == "Base Interfaces")
    assert base.version == "newest"
    assert base.placeholder is None


def test_get_structure_folder_empty_for_root_pou(sample_project_path: Path) -> None:
    structure = XmlReader().get_structure(str(sample_project_path))
    fb = next(p for p in structure.pous if p.name == "FB_Example")
    assert fb.folder == ""


def test_get_structure_folder_set_for_nested_pou(tmp_path: Path) -> None:
    # Project root with a .plcproj at the top and a POU one level deep.
    (tmp_path / "Sample.plcproj").write_text(
        '<?xml version="1.0"?>\n<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" />\n',
        encoding="utf-8",
    )
    nested_dir = tmp_path / "POUs" / "Axes"
    nested_dir.mkdir(parents=True)
    (nested_dir / "FB_Motor.TcPOU").write_text(
        dedent("""\
            <?xml version="1.0" encoding="utf-8"?>
            <TcPlcObject Version="1.1.0.1">
              <POU Name="FB_Motor" Id="{00000000-0000-0000-0000-000000000001}" SpecialFunc="None">
                <Declaration><![CDATA[FUNCTION_BLOCK FB_Motor
            VAR_INPUT
            END_VAR]]></Declaration>
                <Implementation>
                  <ST><![CDATA[]]></ST>
                </Implementation>
              </POU>
            </TcPlcObject>
            """),
        encoding="utf-8",
    )

    structure = XmlReader().get_structure(str(tmp_path))
    motor = next(p for p in structure.pous if p.name == "FB_Motor")
    assert motor.folder == "POUs/Axes"


def test_get_structure_no_project_files_returns_empty_lists(tmp_path: Path) -> None:
    # Code files but no .plcproj/.tsproj/.TcTTO. Adapter must degrade gracefully.
    (tmp_path / "FB_Bare.TcPOU").write_text(
        dedent("""\
            <?xml version="1.0" encoding="utf-8"?>
            <TcPlcObject Version="1.1.0.1">
              <POU Name="FB_Bare" Id="{00000000-0000-0000-0000-000000000002}" SpecialFunc="None">
                <Declaration><![CDATA[FUNCTION_BLOCK FB_Bare
            VAR_INPUT
            END_VAR]]></Declaration>
                <Implementation>
                  <ST><![CDATA[]]></ST>
                </Implementation>
              </POU>
            </TcPlcObject>
            """),
        encoding="utf-8",
    )

    structure = XmlReader().get_structure(str(tmp_path))
    assert structure.tasks == []
    assert structure.libraries == []


# ---------------------------------------------------------------------------
# Index staleness — mtime guard on .plcproj
# ---------------------------------------------------------------------------


def _copy_sample_to(tmp_path: Path, sample: Path) -> Path:
    """Mirror the sample_project fixture into a writable temp directory."""
    import shutil

    target = tmp_path / "proj"
    shutil.copytree(sample, target)
    return target


def test_index_rebuilds_when_plcproj_mtime_changes(
    tmp_path: Path, sample_project_path: Path
) -> None:
    """A structural change (recorded as a .plcproj mtime bump) invalidates the index.

    The reader instance lives for the server's lifetime (#42), so a stale
    file-name index would let a new POU go undiscovered until the next
    explicit get_structure call. The mtime guard fixes this.
    """
    proj = _copy_sample_to(tmp_path, sample_project_path)
    reader = XmlReader()
    reader.get_structure(str(proj))
    assert "FB_Example" in reader._file_index

    # Drop a brand-new POU into the project; TwinCAT would rewrite .plcproj
    # in the same operation. Simulate that by bumping the mtime forward.
    new_pou = proj / "FB_New.TcPOU"
    new_pou.write_text(
        dedent("""\
            <?xml version="1.0" encoding="utf-8"?>
            <TcPlcObject Version="1.1.0.1">
              <POU Name="FB_New" Id="{00000000-0000-0000-0000-000000000099}" SpecialFunc="None">
                <Declaration><![CDATA[FUNCTION_BLOCK FB_New
            VAR_INPUT
            END_VAR]]></Declaration>
                <Implementation>
                  <ST><![CDATA[]]></ST>
                </Implementation>
              </POU>
            </TcPlcObject>
            """),
        encoding="utf-8",
    )
    plcproj = proj / "SampleProject.plcproj"
    bumped = plcproj.stat().st_mtime + 5
    import os
    os.utime(plcproj, (bumped, bumped))

    # The next read should trigger a transparent rebuild and pick up FB_New.
    interface = reader.get_pou_interface("FB_New")
    assert interface.pou_name == "FB_New"
    assert "FB_New" in reader._file_index


def test_index_warm_path_does_not_rebuild_on_body_edits(
    tmp_path: Path, sample_project_path: Path
) -> None:
    """Editing a POU body must not trip the mtime guard.

    Body-only edits don't touch .plcproj, so the cached index stays valid.
    Verified by observing that a second read against the same POU returns
    the same file path object identity from the index (no replacement).
    """
    proj = _copy_sample_to(tmp_path, sample_project_path)
    reader = XmlReader()
    reader.get_structure(str(proj))
    original_path = reader._file_index["FB_Example"]

    # Touch the POU body file; .plcproj is untouched.
    pou_file = proj / "FB_Example.TcPOU"
    bumped = pou_file.stat().st_mtime + 5
    import os
    os.utime(pou_file, (bumped, bumped))

    reader.get_pou_interface("FB_Example")
    assert reader._file_index["FB_Example"] is original_path
