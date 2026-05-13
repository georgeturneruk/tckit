"""Tests for the xae_com_builder adapter (with a fake BridgeClient)."""

from __future__ import annotations

from typing import Any

from tckit.adapters.builders.xae_com_builder import XaeComBuilder
from tckit.ports.types import BuildStatus
from tckit.utils.bridge_client import BridgeUnavailableError


class FakeBridgeClient:
    def __init__(self, response: dict[str, Any] | None = None, raise_exc: Exception | None = None):
        self.calls: list[tuple[str, dict[str, Any], float | None]] = []
        self.response = response or {"success": True}
        self.raise_exc = raise_exc

    def post(
        self,
        path: str,
        payload: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        self.calls.append((path, payload or {}, timeout))
        if self.raise_exc is not None:
            raise self.raise_exc
        return self.response


def test_build_success_parsed_into_build_result() -> None:
    client = FakeBridgeClient(
        {
            "success": True,
            "errors": [],
            "warnings": [],
            "duration_seconds": 4.25,
        }
    )
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build("C:/proj.sln")

    assert result.success is True
    assert result.duration_seconds == 4.25
    assert result.errors == []
    assert builder.get_status() == BuildStatus.SUCCESS

    path, payload, timeout = client.calls[0]
    assert path == "/build"
    assert payload == {"ProjectPath": "C:/proj.sln"}
    assert timeout is not None and timeout > 0


def test_build_errors_parsed_with_severity() -> None:
    client = FakeBridgeClient(
        {
            "success": False,
            "errors": [
                {"file": "FB_X.TcPOU", "line": 42, "message": "undeclared", "severity": "error"}
            ],
            "warnings": [
                {"file": "FB_Y.TcPOU", "line": 5, "message": "unused"}
            ],
            "duration_seconds": 1.0,
        }
    )
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build("C:/proj.sln")

    assert result.success is False
    assert len(result.errors) == 1
    assert result.errors[0].file == "FB_X.TcPOU"
    assert result.errors[0].line == 42
    assert result.errors[0].severity == "error"
    assert len(result.warnings) == 1
    assert result.warnings[0].severity == "warning"
    assert builder.get_status() == BuildStatus.ERROR


def test_build_bridge_unavailable_yields_error_result() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("nope"))
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build("C:/proj.sln")

    assert result.success is False
    assert len(result.errors) == 1
    assert "nope" in result.errors[0].message
    assert builder.get_status() == BuildStatus.ERROR


def test_deploy_posts_to_deploy_endpoint() -> None:
    client = FakeBridgeClient({"success": True, "details": {"target": "1.2.3.4.1.1"}})
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.deploy("1.2.3.4.1.1")

    assert result.success is True
    path, payload, _ = client.calls[0]
    assert path == "/deploy"
    assert payload == {"TargetAmsId": "1.2.3.4.1.1"}


def test_start_runtime_posts_to_runtime_endpoint() -> None:
    client = FakeBridgeClient({"success": True})
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    builder.start_runtime("1.2.3.4.1.1")

    path, payload, _ = client.calls[0]
    assert path == "/runtime"
    assert payload == {
        "TargetAmsId": "1.2.3.4.1.1",
        "Mode": "Run",
        "Wait": True,
    }


def test_get_status_starts_idle() -> None:
    builder = XaeComBuilder(client=FakeBridgeClient())  # type: ignore[arg-type]
    assert builder.get_status() == BuildStatus.IDLE


def test_deploy_failure_translated() -> None:
    client = FakeBridgeClient({"success": False, "error": "no route to host"})
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]
    result = builder.deploy("9.9.9.9.1.1")
    assert result.success is False
    assert result.error == "no route to host"
