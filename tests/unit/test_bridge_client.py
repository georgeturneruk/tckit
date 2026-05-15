"""Tests for the bridge HTTP client utility."""

from __future__ import annotations

from typing import Any

import httpx
import pytest

from tckit.utils.bridge_client import (
    DEFAULT_BRIDGE_URL,
    DEFAULT_TIMEOUT,
    BridgeClient,
    BridgeError,
    BridgeUnavailableError,
    build_timeout,
    route_timeout,
)


def _client_with_handler(handler) -> BridgeClient:  # type: ignore[no-untyped-def]
    """Build a BridgeClient whose underlying httpx.Client uses MockTransport."""
    client = BridgeClient(base_url="http://test-bridge")
    client._client = httpx.Client(  # type: ignore[attr-defined]
        base_url="http://test-bridge",
        transport=httpx.MockTransport(handler),
    )
    return client


def test_default_base_url_from_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("BRIDGE_URL", raising=False)
    assert BridgeClient().base_url == DEFAULT_BRIDGE_URL


def test_base_url_env_override(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("BRIDGE_URL", "http://10.0.0.5:9000/")
    # Trailing slash should be stripped.
    assert BridgeClient().base_url == "http://10.0.0.5:9000"


def test_post_returns_parsed_json() -> None:
    captured: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["body"] = request.content.decode()
        return httpx.Response(200, json={"success": True, "echoed": "ok"})

    client = _client_with_handler(handler)
    resp = client.post("/build", {"ProjectPath": "C:\\proj\\foo.sln"})
    assert resp == {"success": True, "echoed": "ok"}
    assert captured["url"].endswith("/build")
    assert "C:\\\\proj\\\\foo.sln" in captured["body"]


def test_post_path_is_normalised() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/pou"
        return httpx.Response(200, json={"success": True})

    client = _client_with_handler(handler)
    # Caller passes path without leading slash.
    assert client.post("pou", {}) == {"success": True}


def test_5xx_with_json_body_returns_body() -> None:
    """5xx responses still return parsed JSON so adapters can read .error."""

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, json={"success": False, "error": "boom"})

    client = _client_with_handler(handler)
    resp = client.post("/build", {})
    assert resp == {"success": False, "error": "boom"}


def test_non_json_response_is_wrapped() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(502, text="<html>bad gateway</html>")

    client = _client_with_handler(handler)
    resp = client.post("/build", {})
    assert resp["success"] is False
    assert "Non-JSON response" in resp["error"]
    assert "502" in resp["error"]


def test_connect_error_raises_bridge_unavailable() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused")

    client = _client_with_handler(handler)
    with pytest.raises(BridgeUnavailableError, match="not reachable"):
        client.post("/build", {})


def test_timeout_error_raises_bridge_error() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("slow")

    client = _client_with_handler(handler)
    with pytest.raises(BridgeError, match="timed out"):
        client.post("/build", {}, timeout=1.0)


def test_health_returns_true_on_ok() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/health"
        return httpx.Response(200, json={"status": "ok"})

    client = _client_with_handler(handler)
    assert client.health() is True


def test_health_returns_false_when_unavailable() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("nope")

    client = _client_with_handler(handler)
    assert client.health() is False


def test_build_timeout_default(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TCKIT_BUILD_TIMEOUT", raising=False)
    assert build_timeout() == 600.0


def test_build_timeout_from_env(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TCKIT_BUILD_TIMEOUT", "120")
    assert build_timeout() == 120.0


def test_build_timeout_invalid_falls_back(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TCKIT_BUILD_TIMEOUT", "not-a-number")
    assert build_timeout() == 600.0


def test_route_timeout_known_routes(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TCKIT_BUILD_TIMEOUT", raising=False)
    monkeypatch.delenv("TCKIT_TEST_RUN_TIMEOUT", raising=False)
    assert route_timeout("/build") == 600.0
    assert route_timeout("/deploy") == 300.0
    assert route_timeout("/runtime") == 180.0
    assert route_timeout("/tcunit-run") == 600.0
    assert route_timeout("/results") == 60.0


def test_route_timeout_unknown_route_falls_back(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TCKIT_BUILD_TIMEOUT", raising=False)
    assert route_timeout("/something-bespoke") == DEFAULT_TIMEOUT


def test_route_timeout_path_normalised() -> None:
    # The leading slash is optional — callers shouldn't have to think
    # about it because BridgeClient.post accepts both.
    assert route_timeout("build") == route_timeout("/build")


def test_route_timeout_env_override_for_tcunit_run(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TCKIT_TEST_RUN_TIMEOUT", "240")
    assert route_timeout("/tcunit-run") == 240.0


def test_post_uses_route_timeout_when_caller_omits_timeout() -> None:
    # The httpx MockTransport doesn't honour timeout itself, but we can
    # verify that BridgeClient passes a non-None timeout to the underlying
    # httpx call — the bug we want to prevent is the per-route default
    # being silently ignored.
    captured: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["timeout"] = request.extensions.get("timeout")
        return httpx.Response(200, json={"success": True})

    client = _client_with_handler(handler)
    client.post("/deploy", {})
    # httpx's timeout extension carries (connect, read, write, pool); the
    # read entry should reflect the /deploy default of 300s rather than
    # the BridgeClient instance default (60s).
    timeout_obj = captured["timeout"]
    assert timeout_obj is not None
    # The shape is a dict like {"connect": 300.0, "read": 300.0, ...}
    assert timeout_obj.get("read") == 300.0
