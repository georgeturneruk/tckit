"""Unit tests for HtmlGenerator."""

from pathlib import Path

import pytest

from tckit.adapters.doc_generators.html_generator import HtmlGenerator
from tckit.ports.types import DocStatus


def test_initial_status_is_idle() -> None:
    gen = HtmlGenerator()
    assert gen.get_status() == DocStatus.IDLE


def test_generate_returns_result_not_raises(tmp_path: Path) -> None:
    """generate() returns a Result rather than raising on any input."""
    gen = HtmlGenerator()
    result = gen.generate(str(tmp_path), str(tmp_path / "out"))
    assert isinstance(result.success, bool)
    if not result.success:
        assert result.error is not None


def test_status_is_not_idle_after_generate(tmp_path: Path) -> None:
    """Status transitions away from IDLE after generate() is called."""
    gen = HtmlGenerator()
    gen.generate(str(tmp_path), str(tmp_path / "out"))
    assert gen.get_status() != DocStatus.IDLE


def test_status_is_error_when_project_missing() -> None:
    """Status is ERROR when the project path has no TwinCAT files."""
    gen = HtmlGenerator()
    gen.generate("/nonexistent/project/path", "/nonexistent/output")
    assert gen.get_status() == DocStatus.ERROR


def test_generate_produces_index_html(tmp_path: Path) -> None:
    """generate() writes index.html when the project has TwinCAT files."""
    project = Path("tests/fixtures/sample_project")
    output = tmp_path / "docs"
    gen = HtmlGenerator()
    result = gen.generate(str(project), str(output))
    assert result.success
    assert (output / "index.html").exists()


def test_generate_produces_object_pages(tmp_path: Path) -> None:
    """generate() produces one HTML page per discovered object."""
    project = Path("tests/fixtures/sample_project")
    output = tmp_path / "docs"
    gen = HtmlGenerator()
    gen.generate(str(project), str(output))
    assert (output / "FB_Example.html").exists()
    assert (output / "GVL_Params.html").exists()
    assert (output / "ST_ExampleConfig.html").exists()


def test_generate_status_complete_on_success(tmp_path: Path) -> None:
    """Status is COMPLETE after a successful generate()."""
    project = Path("tests/fixtures/sample_project")
    gen = HtmlGenerator()
    gen.generate(str(project), str(tmp_path / "docs"))
    assert gen.get_status() == DocStatus.COMPLETE


@pytest.mark.parametrize("initial_status", [DocStatus.IDLE])
def test_status_enum_values(initial_status: DocStatus) -> None:
    gen = HtmlGenerator()
    assert gen.get_status() == initial_status
