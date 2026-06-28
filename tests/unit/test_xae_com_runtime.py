"""Tests for the XaeComRuntime adapter (with a fake BridgeClient)."""

from __future__ import annotations

import json
from typing import Any

from tckit.adapters.runtime.xae_com_runtime import XaeComRuntime
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


# ---------------------------------------------------------------------------
# start_runtime
# ---------------------------------------------------------------------------


def test_start_runtime_posts_to_runtime_endpoint() -> None:
    client = FakeBridgeClient({"success": True})
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    runtime.start_runtime("1.2.3.4.1.1")

    path, payload, _ = client.calls[0]
    assert path == "/runtime"
    assert payload == {"TargetAmsId": "1.2.3.4.1.1", "Mode": "Run", "Wait": True}
    assert "ProjectPath" not in payload


def test_start_runtime_bridge_error_returns_failure() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("down"))
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.start_runtime("1.2.3.4.1.1")

    assert result.success is False
    assert "down" in (result.error or "")


# ---------------------------------------------------------------------------
# read_symbols
# ---------------------------------------------------------------------------


def test_read_symbols_returns_values_dict() -> None:
    client = FakeBridgeClient(
        {
            "success": True,
            "values": {
                "MAIN.suite.Tests[1].TestIsFailed": "FALSE",
                "MAIN.suite.Tests[2].TestIsFailed": "TRUE",
            },
        }
    )
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.read_symbols(
        "1.2.3.4.1.1",
        ["MAIN.suite.Tests[1].TestIsFailed", "MAIN.suite.Tests[2].TestIsFailed"],
    )

    assert result == {
        "MAIN.suite.Tests[1].TestIsFailed": "FALSE",
        "MAIN.suite.Tests[2].TestIsFailed": "TRUE",
    }
    path, payload, _ = client.calls[0]
    assert path == "/symbols"
    assert payload["TargetAmsId"] == "1.2.3.4.1.1"
    # Newline-separated, not a JSON array — bridge convention.
    assert payload["Paths"].startswith("MAIN.suite.Tests[1].TestIsFailed")


def test_read_symbols_empty_paths_short_circuits() -> None:
    client = FakeBridgeClient({"success": True})
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.read_symbols("1.2.3.4.1.1", [])

    assert result == {}
    assert client.calls == []


def test_read_symbols_unreadable_path_returns_none() -> None:
    client = FakeBridgeClient(
        {"success": True, "values": {"MAIN.has": "OK"}}  # MAIN.missing absent
    )
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.read_symbols("1.2.3.4.1.1", ["MAIN.has", "MAIN.missing"])

    assert result == {"MAIN.has": "OK", "MAIN.missing": None}


def test_read_symbols_bridge_unavailable_returns_all_none() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("nope"))
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.read_symbols("1.2.3.4.1.1", ["MAIN.foo", "MAIN.bar"])

    assert result == {"MAIN.foo": None, "MAIN.bar": None}


def test_read_symbols_bridge_error_returns_none_for_all() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("down"))
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.read_symbols("1.2.3.4.1.1", ["MAIN.nCounter"])

    assert result == {"MAIN.nCounter": None}


# ---------------------------------------------------------------------------
# write_symbols
# ---------------------------------------------------------------------------


def test_write_symbols_posts_to_write_symbols_endpoint() -> None:
    client = FakeBridgeClient(
        {"success": True, "written": {"MAIN.nCounter": "42"}, "errors": {}}
    )
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    runtime.write_symbols("1.2.3.4.1.1", {"MAIN.nCounter": 42})

    path, payload, _ = client.calls[0]
    assert path == "/write-symbols"
    assert payload["TargetAmsId"] == "1.2.3.4.1.1"
    writes = json.loads(payload["WritesJson"])
    assert writes == {"MAIN.nCounter": 42}


def test_write_symbols_empty_writes_short_circuits() -> None:
    client = FakeBridgeClient()
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.write_symbols("1.2.3.4.1.1", {})

    assert result.success is True
    assert result.details == {"written": {}, "errors": {}}
    assert client.calls == []


def test_write_symbols_partial_failure_reported_in_details() -> None:
    resp = {
        "success": False,
        "written": {"MAIN.nCounter": "42"},
        "errors": {"MAIN.badSym": "Symbol not found"},
    }
    client = FakeBridgeClient(resp)
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.write_symbols(
        "1.2.3.4.1.1", {"MAIN.nCounter": 42, "MAIN.badSym": 99}
    )

    assert result.success is False
    assert result.details["written"] == {"MAIN.nCounter": "42"}
    assert "MAIN.badSym" in result.details["errors"]


def test_write_symbols_encodes_complex_values() -> None:
    client = FakeBridgeClient({"success": True, "written": {}, "errors": {}})
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    runtime.write_symbols(
        "1.2.3.4.1.1",
        {"GVL.arr": [1, 2, 3], "GVL.bFlag": True, "GVL.fVal": 3.14},
    )

    _, payload, _ = client.calls[0]
    writes = json.loads(payload["WritesJson"])
    assert writes["GVL.arr"] == [1, 2, 3]
    assert writes["GVL.bFlag"] is True
    assert writes["GVL.fVal"] == 3.14


def test_write_symbols_bridge_error_returns_failure() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("down"))
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.write_symbols("1.2.3.4.1.1", {"MAIN.nCounter": 1})

    assert result.success is False
    assert "down" in (result.error or "")


# ---------------------------------------------------------------------------
# invoke_rpc
# ---------------------------------------------------------------------------


def test_invoke_rpc_posts_to_invoke_rpc_endpoint() -> None:
    client = FakeBridgeClient(
        {"success": True, "return_value": "7", "return_type": "Int16"}
    )
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.invoke_rpc(
        "1.2.3.4.1.1", "MAIN.fbCalc", "M_Add", [3, 4]
    )

    path, payload, _ = client.calls[0]
    assert path == "/invoke-rpc"
    assert payload["TargetAmsId"] == "1.2.3.4.1.1"
    assert payload["SymbolPath"] == "MAIN.fbCalc"
    assert payload["MethodName"] == "M_Add"
    params = json.loads(payload["ParamsJson"])
    assert params == [3, 4]
    assert result.success is True
    assert result.details["return_value"] == "7"


def test_invoke_rpc_none_params_sends_empty_list() -> None:
    client = FakeBridgeClient({"success": True})
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    runtime.invoke_rpc("1.2.3.4.1.1", "MAIN", "M_Reset", None)

    _, payload, _ = client.calls[0]
    assert json.loads(payload["ParamsJson"]) == []


def test_invoke_rpc_bridge_error_returns_failure() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("down"))
    runtime = XaeComRuntime(client=client)  # type: ignore[arg-type]

    result = runtime.invoke_rpc("1.2.3.4.1.1", "MAIN", "M_Reset")

    assert result.success is False
    assert "down" in (result.error or "")
