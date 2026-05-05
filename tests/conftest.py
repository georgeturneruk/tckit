"""Shared pytest fixtures for TcKit tests."""

from pathlib import Path

import pytest

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "sample_project"


@pytest.fixture()
def sample_project_path() -> Path:
    """Return the path to the sample TwinCAT project fixture directory."""
    return FIXTURES_DIR


@pytest.fixture()
def fb_example_path(sample_project_path: Path) -> Path:
    """Return the path to the sample FB_Example.TcPOU fixture file."""
    return sample_project_path / "FB_Example.TcPOU"


@pytest.fixture()
def gvl_params_path(sample_project_path: Path) -> Path:
    """Return the path to the sample GVL_Params.TcGVL fixture file."""
    return sample_project_path / "GVL_Params.TcGVL"
