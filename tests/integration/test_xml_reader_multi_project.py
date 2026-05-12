"""Multi-project sln integration tests for XmlReader (ADR-0005).

The fixture under ``tests/fixtures/multi_project_sln`` has two ``.plcproj``
files (``Library`` and ``Tests``) with a ``.TcPOU`` unique to each plus a
``.TcDUT`` named ``E_State`` deliberately duplicated across both PLC
projects so we can exercise the ambiguous-symbol error path.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from tckit.adapters.readers.xml_reader import XmlReader


@pytest.fixture()
def reader(multi_project_sln_path: Path) -> XmlReader:
    r = XmlReader()
    r.get_structure(str(multi_project_sln_path))
    return r


# ---------------------------------------------------------------------------
# get_structure shape
# ---------------------------------------------------------------------------


def test_get_structure_returns_one_entry_per_plcproj(
    multi_project_sln_path: Path,
) -> None:
    structure = XmlReader().get_structure(str(multi_project_sln_path))
    assert set(structure.plcs.keys()) == {"Library", "Tests"}


def test_pourefs_carry_owning_plc_name(multi_project_sln_path: Path) -> None:
    structure = XmlReader().get_structure(str(multi_project_sln_path))
    lib_pous = structure.plcs["Library"].pous
    test_pous = structure.plcs["Tests"].pous
    assert all(p.plc_name == "Library" for p in lib_pous)
    assert all(p.plc_name == "Tests" for p in test_pous)


def test_each_plc_has_its_own_libraries(multi_project_sln_path: Path) -> None:
    structure = XmlReader().get_structure(str(multi_project_sln_path))
    lib_names = {lib.name for lib in structure.plcs["Library"].libraries}
    test_names = {lib.name for lib in structure.plcs["Tests"].libraries}
    assert "Tc2_Standard" in lib_names
    assert "TcUnit" in test_names
    # Each PLC's libraries are scoped to its own .plcproj.
    assert "TcUnit" not in lib_names
    assert "Tc2_Standard" not in test_names


def test_scoped_get_structure_restricts_to_one_plc(
    multi_project_sln_path: Path,
) -> None:
    structure = XmlReader().get_structure(
        str(multi_project_sln_path), plc_name="Library"
    )
    assert set(structure.plcs.keys()) == {"Library"}


def test_scoped_get_structure_unknown_plc_raises(
    multi_project_sln_path: Path,
) -> None:
    with pytest.raises(ValueError, match="Ghost"):
        XmlReader().get_structure(
            str(multi_project_sln_path), plc_name="Ghost"
        )


# ---------------------------------------------------------------------------
# Symbol resolution
# ---------------------------------------------------------------------------


def test_explicit_plc_name_resolves_symbol(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_Filter", plc_name="Library")
    assert interface.pou_name == "FB_Filter"


def test_unique_symbol_resolves_without_plc_name(reader: XmlReader) -> None:
    """A POU that exists in only one PLC project resolves without a hint."""
    interface = reader.get_pou_interface("FB_Filter")
    assert interface.pou_name == "FB_Filter"


def test_tests_only_symbol_resolves_without_hint(reader: XmlReader) -> None:
    interface = reader.get_pou_interface("FB_FilterTests")
    assert interface.pou_name == "FB_FilterTests"


def test_ambiguous_symbol_raises_with_plc_names(reader: XmlReader) -> None:
    """E_State exists in both PLCs; ambiguous lookup must name both."""
    with pytest.raises(ValueError, match="Library.*Tests|Tests.*Library"):
        reader.get_dut("E_State")


def test_explicit_plc_name_resolves_ambiguous_symbol(reader: XmlReader) -> None:
    lib = reader.get_dut("E_State", plc_name="Library")
    tests = reader.get_dut("E_State", plc_name="Tests")
    # Library has Idle/Running/Errored; Tests has Pending/Pass/Fail.
    assert "Errored" in lib.declaration
    assert "Pass" in tests.declaration


def test_env_default_resolves_ambiguous_symbol(
    reader: XmlReader, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "Tests")
    result = reader.get_dut("E_State")
    assert "Pass" in result.declaration


def test_explicit_wins_over_env_default(
    reader: XmlReader, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "Tests")
    result = reader.get_dut("E_State", plc_name="Library")
    assert "Errored" in result.declaration


def test_unknown_plc_name_raises(reader: XmlReader) -> None:
    with pytest.raises(ValueError, match="does not match"):
        reader.get_pou_interface("FB_Filter", plc_name="Ghost")


def test_unknown_symbol_in_scoped_plc_raises(reader: XmlReader) -> None:
    with pytest.raises(FileNotFoundError, match="FB_FilterTests"):
        reader.get_pou_interface("FB_FilterTests", plc_name="Library")
