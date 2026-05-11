"""Tests for the config adapter registry."""

import pytest

from tckit.config import TcKitConfig
from tckit.ports.builder import BuildRunner
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.reader import ProjectReader
from tckit.ports.test_runner import TestRunner
from tckit.ports.writer import ProjectWriter


def _cfg(overrides: dict | None = None) -> TcKitConfig:
    base = {
        "reader": "xml",
        "writer": "automation_interface",
        "builder": "xae_com",
        "test_runner": "tcunit",
        "doc_generator": "html",
        "docs_searcher": "beckhoff_infosys",
    }
    if overrides:
        base.update(overrides)
    return TcKitConfig(base)


def test_reader_returns_project_reader() -> None:
    assert isinstance(_cfg().reader(), ProjectReader)


def test_reader_is_cached_across_calls() -> None:
    """Successive cfg.reader() calls return the same instance.

    The XmlReader caches its file-name index between calls. The MCP server
    invokes ``cfg.reader()`` once per request, so the instance must persist
    or get_structure→get_pou_interface chains lose the index. See #42.
    """
    cfg = _cfg()
    assert cfg.reader() is cfg.reader()


def test_writer_returns_project_writer() -> None:
    assert isinstance(_cfg().writer(), ProjectWriter)


def test_builder_returns_build_runner() -> None:
    assert isinstance(_cfg().builder(), BuildRunner)


def test_test_runner_returns_test_runner() -> None:
    assert isinstance(_cfg().test_runner(), TestRunner)


def test_doc_generator_returns_doc_generator() -> None:
    assert isinstance(_cfg().doc_generator(), DocGenerator)


def test_docs_searcher_returns_docs_searcher() -> None:
    assert isinstance(_cfg().docs_searcher(), DocsSearcher)


def test_unknown_reader_raises() -> None:
    with pytest.raises(ValueError, match="Unknown reader adapter"):
        _cfg({"reader": "nonexistent"}).reader()


def test_unknown_builder_raises() -> None:
    with pytest.raises(ValueError, match="Unknown builder adapter"):
        _cfg({"builder": "nonexistent"}).builder()
