"""Integration tests for XmlReader — runs against fixture files, no network."""

from pathlib import Path

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


def test_get_structure_finds_gvl_params(sample_project_path: Path) -> None:
    reader = XmlReader()
    structure = reader.get_structure(str(sample_project_path))
    assert "GVL_Params" in structure.gvls


# ---------------------------------------------------------------------------
# get_pou_interface
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


# ---------------------------------------------------------------------------
# get_pou_item
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
# Error handling
# ---------------------------------------------------------------------------


def test_unknown_pou_raises(reader: XmlReader) -> None:
    with pytest.raises(FileNotFoundError):
        reader.get_pou_interface("NonExistentPOU")


def test_unknown_item_raises(reader: XmlReader) -> None:
    with pytest.raises(FileNotFoundError):
        reader.get_pou_item("FB_Example", "NonExistentMethod")


def test_get_structure_bad_path_raises() -> None:
    reader = XmlReader()
    with pytest.raises(FileNotFoundError):
        reader.get_structure("/nonexistent/path/to/project")
