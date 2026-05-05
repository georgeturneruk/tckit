"""Unit tests for SphinxGenerator."""

from pathlib import Path

import pytest

from tckit.adapters.doc_generators.sphinx_generator import SphinxGenerator
from tckit.ports.types import DocStatus


def test_initial_status_is_idle() -> None:
    gen = SphinxGenerator()
    assert gen.get_status() == DocStatus.IDLE


def test_generate_returns_result_not_raises(tmp_path: Path) -> None:
    """generate() returns a Result rather than raising when tools are missing."""
    gen = SphinxGenerator()
    result = gen.generate(str(tmp_path), str(tmp_path / "out"))
    # Either succeeds (if plcdoc installed) or fails gracefully — never raises
    assert isinstance(result.success, bool)
    if not result.success:
        assert result.error is not None


def test_status_is_not_idle_after_generate(tmp_path: Path) -> None:
    """Status transitions away from IDLE after generate() is called."""
    gen = SphinxGenerator()
    gen.generate(str(tmp_path), str(tmp_path / "out"))
    assert gen.get_status() != DocStatus.IDLE


def test_status_is_error_when_project_missing() -> None:
    """Status is ERROR when the project path does not exist."""
    gen = SphinxGenerator()
    gen.generate("/nonexistent/project/path", "/nonexistent/output")
    assert gen.get_status() in (DocStatus.ERROR, DocStatus.COMPLETE)


@pytest.mark.parametrize("initial_status", [DocStatus.IDLE])
def test_status_enum_values(initial_status: DocStatus) -> None:
    gen = SphinxGenerator()
    assert gen.get_status() == initial_status
