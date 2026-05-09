"""Tests for the layered config loader (``~/.tckit/config.toml``, env precedence)."""

from __future__ import annotations

from pathlib import Path

import pytest

from tckit.config import (
    TcKitConfig,
    _load_project_config,
    _load_user_toml,
    _user_home,
    load_config,
)

# ---------------------------------------------------------------------------
# _user_home — TCKIT_HOME override semantics
# ---------------------------------------------------------------------------


def test_user_home_defaults_to_dotfile_under_real_home(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("TCKIT_HOME", raising=False)
    home = _user_home()
    assert home == Path.home() / ".tckit"


def test_user_home_honours_tckit_home_override(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    assert _user_home() == tmp_path


# ---------------------------------------------------------------------------
# _load_user_toml — reads ~/.tckit/config.toml
# ---------------------------------------------------------------------------


def test_load_user_toml_returns_empty_when_file_missing(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    assert _load_user_toml() == {}


def test_load_user_toml_reads_existing_file(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    (tmp_path / "config.toml").write_text(
        'reader = "xml"\n'
        'doc_generator = "markdown"\n'
        'infosys_lang = "1031"\n'
    )
    cfg = _load_user_toml()
    assert cfg == {
        "reader": "xml",
        "doc_generator": "markdown",
        "infosys_lang": "1031",
    }


# ---------------------------------------------------------------------------
# _load_project_config — config.json with TCKIT_CONFIG override
# ---------------------------------------------------------------------------


def test_load_project_config_returns_empty_when_file_missing(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)
    assert _load_project_config() == {}


def test_load_project_config_honours_tckit_config_override(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config_file = tmp_path / "weird-name.json"
    config_file.write_text('{"reader": "xml", "writer": "automation_interface"}')
    monkeypatch.setenv("TCKIT_CONFIG", str(config_file))
    cfg = _load_project_config()
    assert cfg == {"reader": "xml", "writer": "automation_interface"}


# ---------------------------------------------------------------------------
# load_config — precedence: project > user > defaults
# ---------------------------------------------------------------------------


def test_load_config_project_overrides_user(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Project config.json takes precedence over user ~/.tckit/config.toml."""
    user_dir = tmp_path / "userhome"
    user_dir.mkdir()
    (user_dir / "config.toml").write_text(
        'reader = "user-reader"\n'
        'doc_generator = "markdown"\n'
    )
    monkeypatch.setenv("TCKIT_HOME", str(user_dir))

    project_dir = tmp_path / "project"
    project_dir.mkdir()
    (project_dir / "config.json").write_text('{"reader": "project-reader"}')
    monkeypatch.chdir(project_dir)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)

    cfg = load_config()
    # Project value wins for reader
    assert cfg._raw["reader"] == "project-reader"
    # User value survives where project didn't override
    assert cfg._raw["doc_generator"] == "markdown"


def test_load_config_no_files_yields_empty_raw(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)
    cfg = load_config()
    assert cfg._raw == {}


# ---------------------------------------------------------------------------
# TcKitConfig.get — env wins over dict, dict wins over default
# ---------------------------------------------------------------------------


def test_get_env_wins_over_raw_dict(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("READER", "env-reader")
    cfg = TcKitConfig({"reader": "dict-reader"})
    assert cfg.get("reader") == "env-reader"


def test_get_dict_used_when_env_unset(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("READER", raising=False)
    cfg = TcKitConfig({"reader": "dict-reader"})
    assert cfg.get("reader") == "dict-reader"


def test_get_default_used_when_neither_env_nor_dict(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("MYSTERY_KEY", raising=False)
    cfg = TcKitConfig({})
    assert cfg.get("mystery_key", "fallback") == "fallback"


def test_get_returns_none_when_no_default_and_unset(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("MYSTERY_KEY", raising=False)
    cfg = TcKitConfig({})
    assert cfg.get("mystery_key") is None
