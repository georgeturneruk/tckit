"""Tests for tckit.utils.diagnostics — netid validation, config checks, bridge ping."""

from __future__ import annotations

import httpx
import pytest

from tckit.config import TcKitConfig
from tckit.utils.bridge_client import BridgeClient
from tckit.utils.diagnostics import (
    bridge_health,
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
