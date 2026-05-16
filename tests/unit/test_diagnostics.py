"""Tests for tckit.utils.diagnostics — netid validation, config checks, bridge ping."""

from __future__ import annotations

import httpx
import pytest

from tckit.config import TcKitConfig
from tckit.utils.bridge_client import BridgeClient
from tckit.utils.diagnostics import (
    bridge_dependencies,
    bridge_health,
    config_file_status,
    install_bridge_dependency,
    is_valid_ams_netid,
    validate_config,
)

# ---------------------------------------------------------------------------
# is_valid_ams_netid — six dot-separated octets
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "value",
    [
        "192.168.1.5.1.1",
        "10.0.0.1.1.1",
        "127.0.0.1.1.1",
        "255.255.255.255.1.1",
    ],
)
def test_valid_ams_netids(value: str) -> None:
    assert is_valid_ams_netid(value) is True


@pytest.mark.parametrize(
    "value",
    [
        "",
        "192.168.1.1",  # only four octets
        "192.168.1.1.1",  # only five
        "192.168.1.1.1.1.1",  # seven
        "192.168.1.5.1.x",  # non-numeric octet
        "not-a-netid",
        "192.168..1.5.1.1",  # empty octet
    ],
)
def test_invalid_ams_netids(value: str) -> None:
    assert is_valid_ams_netid(value) is False


# ---------------------------------------------------------------------------
# validate_config — surfaces malformed values
# ---------------------------------------------------------------------------


def test_validate_config_clean_returns_no_issues(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    for var in ("TARGET_AMS_ID", "ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        monkeypatch.delenv(var, raising=False)
    cfg = TcKitConfig({"TARGET_AMS_ID": "192.168.1.5.1.1"})
    assert validate_config(cfg) == []


def test_validate_config_flags_bad_target_netid(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    for var in ("TARGET_AMS_ID", "ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        monkeypatch.delenv(var, raising=False)
    cfg = TcKitConfig({"TARGET_AMS_ID": "not-a-netid"})
    issues = validate_config(cfg)
    assert len(issues) == 1
    assert "TARGET_AMS_ID" in issues[0]
    assert "not-a-netid" in issues[0]


def test_validate_config_flags_bad_allowed_netid(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    for var in ("TARGET_AMS_ID", "ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        monkeypatch.delenv(var, raising=False)
    cfg = TcKitConfig({"ALLOWED_NETIDS": "192.168.1.5.1.1, bogus, 10.0.0.1.1.1"})
    issues = validate_config(cfg)
    assert len(issues) == 1
    assert "ALLOWED_NETIDS" in issues[0]
    assert "bogus" in issues[0]


def test_validate_config_flags_bad_blocked_netid(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    for var in ("TARGET_AMS_ID", "ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        monkeypatch.delenv(var, raising=False)
    cfg = TcKitConfig({"BLOCKED_NETIDS": "192.168.1.5"})  # missing octets
    issues = validate_config(cfg)
    assert len(issues) == 1
    assert "BLOCKED_NETIDS" in issues[0]


def test_validate_config_skips_unset_optional_keys(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    for var in ("TARGET_AMS_ID", "ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        monkeypatch.delenv(var, raising=False)
    cfg = TcKitConfig({})
    assert validate_config(cfg) == []


# ---------------------------------------------------------------------------
# config_file_status — surfaces missing-config / unset-target for the doctor
# ---------------------------------------------------------------------------


def test_config_file_status_reports_missing(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)
    cfg = TcKitConfig({})
    file_exists, target_set = config_file_status(cfg)
    assert file_exists is False
    assert target_set is False


def test_config_file_status_reports_present_with_target(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)
    (tmp_path / "config.toml").touch()
    cfg = TcKitConfig({"TARGET_AMS_ID": "192.168.1.5.1.1"})
    file_exists, target_set = config_file_status(cfg)
    assert file_exists is True
    assert target_set is True


def test_config_file_status_target_set_via_env(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Env var alone counts as 'target set' even with no file."""
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.setenv("TARGET_AMS_ID", "192.168.1.5.1.1")
    cfg = TcKitConfig({})
    file_exists, target_set = config_file_status(cfg)
    assert file_exists is False
    assert target_set is True


# ---------------------------------------------------------------------------
# bridge_health — pings /health, surfaces both reachable and not-reachable
# ---------------------------------------------------------------------------


def _build_with_mock(handler) -> BridgeClient:  # type: ignore[no-untyped-def]
    client = BridgeClient(base_url="http://test-bridge")
    client._client = httpx.Client(  # type: ignore[attr-defined]
        base_url="http://test-bridge",
        transport=httpx.MockTransport(handler),
    )
    return client


def test_bridge_health_ok_when_endpoint_returns_status_ok(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"status": "ok", "version": "0.1.0"})

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    ok, msg = bridge_health()
    assert ok is True
    assert "test-bridge" in msg


def test_bridge_health_fails_when_endpoint_returns_non_ok(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"status": "starting"})

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    ok, msg = bridge_health()
    assert ok is False
    assert "not reachable" in msg


def test_bridge_health_returns_error_on_connection_failure(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused")

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    ok, msg = bridge_health()
    assert ok is False
    # ConnectError gets wrapped into BridgeUnavailableError by BridgeClient,
    # which makes health() return False — so the message reads "not reachable".
    assert "test-bridge" in msg


# ---------------------------------------------------------------------------
# bridge_dependencies — extracts dependencies block from /health
# ---------------------------------------------------------------------------


def test_bridge_dependencies_extracts_versions(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "status": "ok",
                "version": "0.1.0",
                "dependencies": {"TcXaeMgmt": "6.2.127"},
            },
        )

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    deps = bridge_dependencies()
    assert deps == {"TcXaeMgmt": "6.2.127"}


def test_bridge_dependencies_marks_missing_as_none(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "status": "ok",
                "dependencies": {"TcXaeMgmt": None},
            },
        )

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    deps = bridge_dependencies()
    assert deps == {"TcXaeMgmt": None}


def test_bridge_dependencies_returns_empty_when_block_absent(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        # Older bridge with no dependencies block.
        return httpx.Response(200, json={"status": "ok", "version": "0.1.0"})

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    deps = bridge_dependencies()
    assert deps == {}


def test_bridge_dependencies_returns_empty_on_connection_failure(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("nope")

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    assert bridge_dependencies() == {}


# ---------------------------------------------------------------------------
# install_bridge_dependency — POST to /install-dependency
# ---------------------------------------------------------------------------


def test_install_bridge_dependency_success(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/install-dependency"
        return httpx.Response(
            200,
            json={
                "success": True,
                "details": {
                    "name": "TcXaeMgmt",
                    "version": "6.2.127",
                    "scope": "CurrentUser",
                },
            },
        )

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    ok, msg = install_bridge_dependency("TcXaeMgmt")
    assert ok is True
    assert "TcXaeMgmt" in msg
    assert "6.2.127" in msg


def test_install_bridge_dependency_failure_surfaces_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={"success": False, "error": "Install-Module timed out"},
        )

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    ok, msg = install_bridge_dependency("TcXaeMgmt")
    assert ok is False
    assert "Install-Module timed out" in msg


def test_install_bridge_dependency_handles_bridge_unreachable(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("nope")

    fake = _build_with_mock(handler)
    monkeypatch.setattr(
        "tckit.utils.diagnostics.BridgeClient",
        lambda *a, **kw: fake,
    )
    ok, msg = install_bridge_dependency("TcXaeMgmt")
    assert ok is False
    assert "bridge" in msg.lower()
