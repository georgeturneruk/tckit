"""Config loader — reads config.json and .env, returns adapter instances."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from dotenv import load_dotenv

from tckit.ports.builder import BuildRunner
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.reader import ProjectReader
from tckit.ports.test_runner import TestRunner
from tckit.ports.writer import ProjectWriter

load_dotenv()

_READER_REGISTRY: dict[str, type[ProjectReader]] = {}
_WRITER_REGISTRY: dict[str, type[ProjectWriter]] = {}
_BUILDER_REGISTRY: dict[str, type[BuildRunner]] = {}
_TEST_RUNNER_REGISTRY: dict[str, type[TestRunner]] = {}
_DOC_GENERATOR_REGISTRY: dict[str, type[DocGenerator]] = {}
_DOCS_SEARCHER_REGISTRY: dict[str, type[DocsSearcher]] = {}


def _load_registries() -> None:
    """Populate registries lazily to avoid importing adapters at module level."""
    from tckit.adapters.builders.xae_com_builder import XaeComBuilder
    from tckit.adapters.doc_generators.sphinx_generator import SphinxGenerator
    from tckit.adapters.docs_searchers.beckhoff_infosys import BeckhoffInfosys
    from tckit.adapters.readers.xml_reader import XmlReader
    from tckit.adapters.test_runners.tcunit_runner import TcUnitRunner
    from tckit.adapters.writers.automation_writer import AutomationWriter

    _READER_REGISTRY["xml"] = XmlReader
    _WRITER_REGISTRY["automation_interface"] = AutomationWriter
    _BUILDER_REGISTRY["xae_com"] = XaeComBuilder
    _TEST_RUNNER_REGISTRY["tcunit"] = TcUnitRunner
    _DOC_GENERATOR_REGISTRY["sphinx"] = SphinxGenerator
    _DOCS_SEARCHER_REGISTRY["beckhoff_infosys"] = BeckhoffInfosys


_registries_loaded = False


def _ensure_registries() -> None:
    global _registries_loaded
    if not _registries_loaded:
        _load_registries()
        _registries_loaded = True


def _load_config_file() -> dict[str, Any]:
    config_path = Path(os.getenv("TCKIT_CONFIG", "config.json"))
    if config_path.exists():
        with config_path.open() as f:
            return json.load(f)  # type: ignore[no-any-return]
    return {}


class TcKitConfig:
    """Holds resolved config values and provides adapter factory methods."""

    def __init__(self, raw: dict[str, Any]) -> None:
        self._raw = raw

    def get(self, key: str, default: Any = None) -> Any:
        return self._raw.get(key, os.getenv(key.upper(), default))

    # ------------------------------------------------------------------
    # Adapter factories
    # ------------------------------------------------------------------

    def reader(self) -> ProjectReader:
        _ensure_registries()
        name = self.get("reader", "xml")
        cls = _READER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown reader adapter: {name!r}")
        return cls()

    def writer(self) -> ProjectWriter:
        _ensure_registries()
        name = self.get("writer", "automation_interface")
        cls = _WRITER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown writer adapter: {name!r}")
        return cls()

    def builder(self) -> BuildRunner:
        _ensure_registries()
        name = self.get("builder", "xae_com")
        cls = _BUILDER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown builder adapter: {name!r}")
        return cls()

    def test_runner(self) -> TestRunner:
        _ensure_registries()
        name = self.get("test_runner", "tcunit")
        cls = _TEST_RUNNER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown test_runner adapter: {name!r}")
        return cls()

    def doc_generator(self) -> DocGenerator:
        _ensure_registries()
        name = self.get("doc_generator", "sphinx")
        cls = _DOC_GENERATOR_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown doc_generator adapter: {name!r}")
        return cls()

    def docs_searcher(self) -> DocsSearcher:
        _ensure_registries()
        name = self.get("docs_searcher", "beckhoff_infosys")
        cls = _DOCS_SEARCHER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown docs_searcher adapter: {name!r}")
        cache_path = self.get("infosys_cache_path", "./cache/infosys")
        lang = self.get("infosys_lang", "1033")
        return cls(cache_path=cache_path, lang=lang)  # type: ignore[call-arg]


def load_config() -> TcKitConfig:
    """Load config.json + .env and return a TcKitConfig instance."""
    return TcKitConfig(_load_config_file())
