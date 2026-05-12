"""Unit tests for plc_resolver — PLC-project name fallback chain (ADR-0005)."""

from __future__ import annotations

import pytest

from tckit.utils.plc_resolver import AmbiguousPLCProjectError, resolve_plc_name


def test_explicit_name_wins(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "EnvDefault")
    assert resolve_plc_name("Library", ["Library", "Tests", "EnvDefault"]) == "Library"


def test_explicit_name_unknown_raises(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    with pytest.raises(ValueError, match="does not match"):
        resolve_plc_name("Ghost", ["Library", "Tests"])


def test_env_default_resolves_when_no_explicit(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "Tests")
    assert resolve_plc_name(None, ["Library", "Tests"]) == "Tests"


def test_env_default_unknown_raises(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "Phantom")
    with pytest.raises(ValueError, match="PLC_PROJECT_NAME"):
        resolve_plc_name(None, ["Library", "Tests"])


def test_single_plc_auto_resolves(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    assert resolve_plc_name(None, ["OnlyOne"]) == "OnlyOne"


def test_multiple_plcs_without_hint_raises(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    with pytest.raises(AmbiguousPLCProjectError, match="Library.*Tests"):
        resolve_plc_name(None, ["Library", "Tests"])


def test_empty_available_raises(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    with pytest.raises(FileNotFoundError, match="No PLC projects"):
        resolve_plc_name(None, [])


def test_empty_env_string_treated_as_unset(monkeypatch: pytest.MonkeyPatch) -> None:
    """An empty PLC_PROJECT_NAME must not poison the resolution."""
    monkeypatch.setenv("PLC_PROJECT_NAME", "")
    # Single PLC: auto-resolve still works.
    assert resolve_plc_name(None, ["Library"]) == "Library"
    # Multi PLC: ambiguous error still fires.
    with pytest.raises(AmbiguousPLCProjectError):
        resolve_plc_name(None, ["Library", "Tests"])
