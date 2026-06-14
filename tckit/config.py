"""Config loader — resolves config from layered sources, returns adapter instances.

Resolution order, highest precedence first:

1. Process environment variables — always win when set.
2. Project-local ``config.json`` (or path from ``TCKIT_CONFIG`` env var).
3. User-global ``~/.tckit/config.toml`` (Python 3.11 stdlib ``tomllib``).
4. Built-in defaults supplied by callers of :meth:`TcKitConfig.get`.

The user-global location can be redirected by setting ``TCKIT_HOME``. ``.env``
files are loaded by walking up from the current working directory and falling
back to ``$TCKIT_HOME/.env``; existing OS env vars are not overridden.
"""

from __future__ import annotations

import json
import os
import tomllib
from pathlib import Path
from typing import Any

from dotenv import find_dotenv, load_dotenv

from tckit.ports.builder import BuildRunner
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.docs_searcher import DocsSearcher
from tckit.ports.reader import ProjectReader
from tckit.ports.test_runner import TestRunner
from tckit.ports.writer import ProjectWriter
from tckit.utils.bridge_client import BridgeClient


def _user_home() -> Path:
    """Resolve the TcKit user-global directory.

    Honours ``TCKIT_HOME`` if set, otherwise ``~/.tckit``.
    """
    override = os.getenv("TCKIT_HOME")
    if override:
        return Path(override)
    return Path.home() / ".tckit"


def _load_dotenv_layered() -> None:
    """Load a ``.env`` file by walking up from cwd, then falling back to user home.

    Existing OS env vars are not overridden by either source. The walk-up uses
    ``python-dotenv``'s :func:`find_dotenv` so behaviour matches the rest of
    the ecosystem.
    """
    found = find_dotenv(usecwd=True)
    if found:
        load_dotenv(found)
        return
    user_env = _user_home() / ".env"
    if user_env.exists():
        load_dotenv(user_env)


_load_dotenv_layered()

_READER_REGISTRY: dict[str, type[ProjectReader]] = {}
_WRITER_REGISTRY: dict[str, type[ProjectWriter]] = {}
_BUILDER_REGISTRY: dict[str, type[BuildRunner]] = {}
_TEST_RUNNER_REGISTRY: dict[str, type[TestRunner]] = {}
_DOC_GENERATOR_REGISTRY: dict[str, type[DocGenerator]] = {}
_DOCS_SEARCHER_REGISTRY: dict[str, type[DocsSearcher]] = {}


def _load_registries() -> None:
    """Populate registries lazily to avoid importing adapters at module level."""
    from tckit.adapters.builders.xae_com_builder import XaeComBuilder
    from tckit.adapters.doc_generators.html_generator import HtmlGenerator
    from tckit.adapters.doc_generators.markdown_generator import MarkdownGenerator
    from tckit.adapters.docs_searchers.beckhoff_infosys_searcher import BeckhoffInfosysSearcher
    from tckit.adapters.readers.xml_reader import XmlReader
    from tckit.adapters.test_runners.tcunit_runner import TcUnitRunner
    from tckit.adapters.writers.automation_writer import AutomationWriter

    _READER_REGISTRY["xml"] = XmlReader
    _WRITER_REGISTRY["automation_interface"] = AutomationWriter
    _BUILDER_REGISTRY["xae_com"] = XaeComBuilder
    _TEST_RUNNER_REGISTRY["tcunit"] = TcUnitRunner
    _DOC_GENERATOR_REGISTRY["html"] = HtmlGenerator
    _DOC_GENERATOR_REGISTRY["markdown"] = MarkdownGenerator
    _DOCS_SEARCHER_REGISTRY["beckhoff_infosys"] = BeckhoffInfosysSearcher


_registries_loaded = False


def _ensure_registries() -> None:
    global _registries_loaded
    if not _registries_loaded:
        _load_registries()
        _registries_loaded = True


def _user_toml_path() -> Path:
    """Path to the user-global ``config.toml``."""
    return _user_home() / "config.toml"


def _project_config_path() -> Path:
    """Path to the project ``config.json`` (or ``TCKIT_CONFIG`` override)."""
    return Path(os.getenv("TCKIT_CONFIG", "config.json"))


def _load_user_toml() -> dict[str, Any]:
    """Read ``$TCKIT_HOME/config.toml`` if present, else return an empty dict."""
    path = _user_toml_path()
    if not path.exists():
        return {}
    with path.open("rb") as f:
        return tomllib.load(f)


def _load_project_config() -> dict[str, Any]:
    """Read project ``config.json`` (or path from ``TCKIT_CONFIG`` env)."""
    config_path = _project_config_path()
    if not config_path.exists():
        return {}
    with config_path.open() as f:
        return json.load(f)  # type: ignore[no-any-return]


def _config_source_mtimes() -> dict[str, float | None]:
    """Snapshot the mtimes of the layered config files (None when absent).

    Used to detect edits so :meth:`TcKitConfig.get` can re-read the files
    per request instead of only at server start.
    """
    out: dict[str, float | None] = {}
    for path in (_user_toml_path(), _project_config_path()):
        try:
            out[str(path)] = path.stat().st_mtime if path.exists() else None
        except OSError:
            out[str(path)] = None
    return out


def _load_merged_raw() -> dict[str, Any]:
    """Load + normalise + merge the layered config files into one dict."""
    user_cfg = _normalise_keys(_load_user_toml())
    project_cfg = _normalise_keys(_load_project_config())
    return {**user_cfg, **project_cfg}


class TcKitConfig:
    """Holds resolved config values and provides adapter factory methods."""

    def __init__(
        self, raw: dict[str, Any], sources: dict[str, float | None] | None = None
    ) -> None:
        self._raw = raw
        # mtime snapshot of the config files this ``raw`` was loaded from,
        # so get() can hot-reload when they change without a reconnect. Only
        # configs built via load_config() carry sources and are watched;
        # a config constructed from a literal dict (tests, callers) is left
        # untouched.
        self._watch_sources = sources is not None
        self._sources = sources if sources is not None else {}
        # Single BridgeClient shared by all bridge-backed adapters so the
        # underlying httpx.Client (and its connection pool) lives for the
        # server's lifetime instead of being re-created per MCP call.
        self._bridge_client: BridgeClient | None = None
        # Single ProjectReader shared across MCP requests so the file-name
        # index populated by get_structure survives long enough for the
        # follow-up get_pou_interface / get_pou_item calls to use it.
        self._reader: ProjectReader | None = None

    def _reload_if_changed(self) -> None:
        """Re-read the config files when any has been edited (hot reload).

        Cheap stat-based check so edits to ``~/.tckit/config.toml`` (safety
        stance, AMS IDs, PLC_PROJECT_NAME, ...) take effect on the next tool
        call rather than only after a full reconnect.
        """
        if not self._watch_sources:
            return
        current = _config_source_mtimes()
        if current != self._sources:
            self._raw = _load_merged_raw()
            self._sources = current

    def get(self, key: str, default: Any = None) -> Any:
        """Resolve ``key`` from env (uppercased) first, then file values, then default."""
        self._reload_if_changed()
        env_val = os.getenv(key.upper())
        if env_val is not None:
            return env_val
        return self._raw.get(key, default)

    def bridge_client(self) -> BridgeClient:
        if self._bridge_client is None:
            self._bridge_client = BridgeClient()
        return self._bridge_client

    # ------------------------------------------------------------------
    # Adapter factories
    # ------------------------------------------------------------------

    def reader(self) -> ProjectReader:
        if self._reader is None:
            _ensure_registries()
            name = self.get("reader", "xml")
            cls = _READER_REGISTRY.get(name)
            if cls is None:
                raise ValueError(f"Unknown reader adapter: {name!r}")
            # Inject the active-solution resolver so reads without a prior
            # get_structure() follow whatever solution is open in the
            # attached XAE, instead of a configured path.
            self._reader = cls(  # type: ignore[call-arg]
                active_solution=self.bridge_client().active_solution
            )
        return self._reader

    def writer(self) -> ProjectWriter:
        _ensure_registries()
        name = self.get("writer", "automation_interface")
        cls = _WRITER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown writer adapter: {name!r}")
        return cls(client=self.bridge_client())  # type: ignore[call-arg]

    def builder(self) -> BuildRunner:
        _ensure_registries()
        name = self.get("builder", "xae_com")
        cls = _BUILDER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown builder adapter: {name!r}")
        return cls(client=self.bridge_client())  # type: ignore[call-arg]

    def test_runner(self) -> TestRunner:
        _ensure_registries()
        name = self.get("test_runner", "tcunit")
        cls = _TEST_RUNNER_REGISTRY.get(name)
        if cls is None:
            raise ValueError(f"Unknown test_runner adapter: {name!r}")
        return cls()

    def doc_generator(self) -> DocGenerator:
        _ensure_registries()
        name = self.get("doc_generator", "html")
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


def _normalise_keys(raw: dict[str, Any]) -> dict[str, Any]:
    """Uppercase env-shaped keys so file-source values are found by env-style lookups.

    Adapter-name keys (``reader``, ``writer``, ``builder``, ``test_runner``,
    ``doc_generator``, ``docs_searcher``) and the two ``infosys_*`` knobs are
    kept lowercase because that's how the resolved-key consumers ask for them.
    Everything else (``TARGET_AMS_ID``, ``XAE_MODE``, ``COM_VERSION``, ...) is
    uppercased so a TOML or JSON file that wrote ``xae_mode`` still resolves.
    """
    keep_lower = {
        "reader",
        "writer",
        "builder",
        "test_runner",
        "doc_generator",
        "docs_searcher",
        "infosys_cache_path",
        "infosys_lang",
        "doc_trigger",
        "comment_style",
        "docs_output_path",
    }
    out: dict[str, Any] = {}
    for key, value in raw.items():
        if key in keep_lower:
            out[key] = value
        else:
            out[key.upper()] = value
    return out


def load_config() -> TcKitConfig:
    """Load layered config sources and return a :class:`TcKitConfig`.

    Project ``config.json`` overrides user-global ``config.toml``; env vars
    override both at lookup time via :meth:`TcKitConfig.get`. Raw keys from
    both files are normalised so env-style lookups (``XAE_MODE``) find values
    written as ``xae_mode`` in a JSON or TOML file. The returned config
    re-reads its source files when they change (see
    :meth:`TcKitConfig._reload_if_changed`).
    """
    return TcKitConfig(_load_merged_raw(), _config_source_mtimes())
