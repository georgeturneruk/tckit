"""Tests for the best-effort bridge auto-spawn guards (#121)."""

from __future__ import annotations

from pathlib import Path

import pytest

from tckit.utils import bridge_spawn


class _FakePopen:
    """Records construction without spawning a real process."""

    instances = 0

    def __init__(self, *args: object, **kwargs: object) -> None:
        type(self).instances += 1


@pytest.fixture(autouse=True)
def _no_real_spawn(monkeypatch: pytest.MonkeyPatch) -> None:
    _FakePopen.instances = 0
    monkeypatch.setattr(bridge_spawn.subprocess, "Popen", _FakePopen)


def test_returns_true_when_already_healthy(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(bridge_spawn.BridgeClient, "health", lambda self: True)
    assert bridge_spawn.ensure_bridge_running("http://localhost:8765") is True
    assert _FakePopen.instances == 0


def test_disabled_via_env_does_not_spawn(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(bridge_spawn.BridgeClient, "health", lambda self: False)
    monkeypatch.setenv("TCKIT_BRIDGE_AUTOSPAWN", "0")
    assert bridge_spawn.ensure_bridge_running("http://localhost:8765") is False
    assert _FakePopen.instances == 0


def test_remote_url_does_not_spawn(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(bridge_spawn.BridgeClient, "health", lambda self: False)
    monkeypatch.delenv("TCKIT_BRIDGE_AUTOSPAWN", raising=False)
    monkeypatch.setattr(bridge_spawn.sys, "platform", "win32")
    assert (
        bridge_spawn.ensure_bridge_running("http://host.docker.internal:8765") is False
    )
    assert _FakePopen.instances == 0


def test_non_windows_does_not_spawn(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(bridge_spawn.BridgeClient, "health", lambda self: False)
    monkeypatch.delenv("TCKIT_BRIDGE_AUTOSPAWN", raising=False)
    monkeypatch.setattr(bridge_spawn.sys, "platform", "linux")
    assert bridge_spawn.ensure_bridge_running("http://localhost:8765") is False
    assert _FakePopen.instances == 0


def test_spawns_when_down_local_and_launcher_present(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr(bridge_spawn.BridgeClient, "health", lambda self: False)
    monkeypatch.delenv("TCKIT_BRIDGE_AUTOSPAWN", raising=False)
    monkeypatch.setattr(bridge_spawn.sys, "platform", "win32")
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    (tmp_path / "bridge").mkdir()
    (tmp_path / "bridge" / "Start-Bridge.ps1").write_text("# launcher")

    # timeout=0 skips the health poll loop; health never flips in the test.
    result = bridge_spawn.ensure_bridge_running("http://localhost:8765", timeout=0)
    assert result is False
    assert _FakePopen.instances == 1
