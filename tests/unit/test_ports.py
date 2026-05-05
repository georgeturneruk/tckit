"""Tests that port ABCs cannot be instantiated directly."""

import pytest

from tckit.ports.builder import BuildRunner
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.reader import ProjectReader
from tckit.ports.test_runner import TestRunner
from tckit.ports.writer import ProjectWriter


def test_project_reader_is_abstract() -> None:
    with pytest.raises(TypeError):
        ProjectReader()  # type: ignore[abstract]


def test_project_writer_is_abstract() -> None:
    with pytest.raises(TypeError):
        ProjectWriter()  # type: ignore[abstract]


def test_build_runner_is_abstract() -> None:
    with pytest.raises(TypeError):
        BuildRunner()  # type: ignore[abstract]


def test_test_runner_is_abstract() -> None:
    with pytest.raises(TypeError):
        TestRunner()  # type: ignore[abstract]


def test_doc_generator_is_abstract() -> None:
    with pytest.raises(TypeError):
        DocGenerator()  # type: ignore[abstract]


def test_docs_searcher_is_abstract() -> None:
    with pytest.raises(TypeError):
        DocsSearcher()  # type: ignore[abstract]
