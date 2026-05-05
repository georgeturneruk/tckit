"""Integration tests for BlarkReader — skipped until adapter is implemented."""

from pathlib import Path

import pytest


@pytest.mark.skip(reason="blark_reader not yet implemented")
def test_get_structure_returns_pous(sample_project_path: Path) -> None:
    from tckit.adapters.readers.blark_reader import BlarkReader
    from tckit.ports.types import ProjectStructure

    reader = BlarkReader()
    result = reader.get_structure(str(sample_project_path))
    assert isinstance(result, ProjectStructure)
    assert len(result.pous) > 0


@pytest.mark.skip(reason="blark_reader not yet implemented")
def test_get_pou_interface_returns_methods(sample_project_path: Path) -> None:
    from tckit.adapters.readers.blark_reader import BlarkReader
    from tckit.ports.types import POUInterface

    reader = BlarkReader()
    result = reader.get_pou_interface("FB_Example")
    assert isinstance(result, POUInterface)
    assert result.pou_name == "FB_Example"


@pytest.mark.skip(reason="blark_reader not yet implemented")
def test_get_pou_item_returns_body(sample_project_path: Path) -> None:
    from tckit.adapters.readers.blark_reader import BlarkReader
    from tckit.ports.types import POUItem

    reader = BlarkReader()
    result = reader.get_pou_item("FB_Example", "Execute")
    assert isinstance(result, POUItem)
    assert result.body
