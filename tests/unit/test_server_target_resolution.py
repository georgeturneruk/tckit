"""Tests for ``target_ams_id`` resolution in the MCP tool functions.

The resolver is the small layer between the model's bare call and the
adapters: it accepts an explicit ``target_ams_id`` arg, falls back to
``TARGET_AMS_ID`` env / config, and surfaces a clear "where to set it"
error when nothing resolves. The tool functions (``deploy``,
``start_runtime``, ``run_tests``) all run this resolver before any
bridge work, so the unresolved case never reaches the bridge.
"""

from __future__ import annotations

import json

import pytest

from tckit import server


def test_resolve_prefers_explicit_arg(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TARGET_AMS_ID", "5.5.5.5.1.1")
    assert server._resolve_target_ams_id("1.2.3.4.1.1") == "1.2.3.4.1.1"


def test_resolve_falls_back_to_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TARGET_AMS_ID", "5.5.5.5.1.1")
    assert server._resolve_target_ams_id("") == "5.5.5.5.1.1"


def test_resolve_falls_back_to_config_when_no_env(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)
    monkeypatch.setattr(server._cfg, "_raw", {"TARGET_AMS_ID": "7.7.7.7.1.1"})
    assert server._resolve_target_ams_id("") == "7.7.7.7.1.1"


def test_resolve_returns_empty_when_unresolved(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)
    monkeypatch.setattr(server._cfg, "_raw", {})
    assert server._resolve_target_ams_id("") == ""


# ---------------------------------------------------------------------------
# The tool functions surface a clear error when the target is unresolvable,
# without touching the bridge.
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "tool",
    [server.deploy, server.start_runtime, server.run_tests],
)
def test_tool_errors_when_target_unresolvable(
    tool: object, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)
    monkeypatch.setattr(server._cfg, "_raw", {})

    raw = tool()  # type: ignore[operator]
    payload = json.loads(raw)
    assert "error" in payload
    assert "target_ams_id is required" in payload["error"]
    assert "TARGET_AMS_ID" in payload["error"]
    assert "~/.tckit/config.toml" in payload["error"]
