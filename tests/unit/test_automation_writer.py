"""Tests for the automation_writer adapter (with a fake BridgeClient)."""

from __future__ import annotations

from typing import Any

import pytest

from tckit.adapters.writers.automation_writer import AutomationWriter
from tckit.ports.types import POUType
from tckit.utils.bridge_client import BridgeUnavailableError


class FakeBridgeClient:
    """Records calls and returns a configured response."""

    def __init__(self, response: dict[str, Any] | None = None, raise_exc: Exception | None = None):
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.response = response or {"success": True}
        self.raise_exc = raise_exc

    def post(
        self,
        path: str,
        payload: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        self.calls.append((path, payload or {}))
        if self.raise_exc is not None:
            raise self.raise_exc
        return self.response


def test_open_project_posts_to_open_endpoint() -> None:
    client = FakeBridgeClient({"success": True, "details": {"solution": "C:/x.sln"}})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    result = writer.open_project("C:/x.sln")

    assert result.success is True
    assert client.calls == [("/open", {"SolutionPath": "C:/x.sln"})]
    assert result.details == {"details": {"solution": "C:/x.sln"}}


def test_create_project_posts_to_create_endpoint() -> None:
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.create_project("MyProj", "C:/work")

    assert client.calls == [("/create", {"Name": "MyProj", "Path": "C:/work"})]


def test_add_pou_includes_project_path_and_translates_pou_type(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_pou("FB_Test", POUType.FUNCTION_BLOCK, "VAR END_VAR")

    path, payload = client.calls[0]
    assert path == "/pou"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "Name": "FB_Test",
        "PouType": "function_block",
        "Code": "VAR END_VAR",
    }
    # When PLC_PROJECT_NAME is unset, the harness auto-resolves — we omit PlcName.
    assert "PlcName" not in payload


def test_add_pou_includes_plc_name_when_env_set(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.setenv("PLC_PROJECT_NAME", "MyPlc")
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_pou("FB_X", POUType.FUNCTION_BLOCK, "decl")

    _, payload = client.calls[0]
    assert payload["PlcName"] == "MyPlc"


def test_add_method_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_method("FB_X", "DoThing", "code")

    path, payload = client.calls[0]
    assert path == "/method"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "MethodName": "DoThing",
        "Code": "code",
    }


def test_update_pou_item_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.update_pou_item("FB_X", "Execute", "BODY")

    path, payload = client.calls[0]
    assert path == "/item"
    assert payload["PouName"] == "FB_X"
    assert payload["ItemName"] == "Execute"
    assert payload["Code"] == "BODY"


def test_update_pou_item_patch_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.update_pou_item_patch("FB_X", "Execute", "OLD", "NEW")

    path, payload = client.calls[0]
    assert path == "/item-patch"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "ItemName": "Execute",
        "OldString": "OLD",
        "NewString": "NEW",
    }


def test_update_pou_item_patch_failure_translated_to_result() -> None:
    client = FakeBridgeClient(
        {"success": False, "error": "OldString appears 2 times; anchor must be unique."}
    )
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    result = writer.update_pou_item_patch("FB_X", "Execute", "x", "y")
    assert result.success is False
    assert "unique" in (result.error or "")


def test_add_variable_payload_shape_fb_level(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_variable("FB_X", "VAR_INPUT", "bNewParam : BOOL;")

    path, payload = client.calls[0]
    assert path == "/add-variable"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "Scope": "VAR_INPUT",
        "Declaration": "bNewParam : BOOL;",
    }
    # Default (FB-level) call omits ItemName so the harness targets the POU itself.
    assert "ItemName" not in payload


def test_add_variable_payload_shape_method_level(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_variable("FB_X", "VAR", "nLocal : INT;", item_name="Execute")

    _, payload = client.calls[0]
    assert payload["ItemName"] == "Execute"
    assert payload["Scope"] == "VAR"


def test_failure_response_translated_to_result() -> None:
    client = FakeBridgeClient({"success": False, "error": "POU not found"})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    result = writer.update_pou_item("Missing", "X", "code")
    assert result.success is False
    assert result.error == "POU not found"


def test_bridge_unavailable_returned_as_failure_result() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("not reachable at http://foo"))
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    result = writer.open_project("C:/x.sln")
    assert result.success is False
    assert "not reachable" in (result.error or "")
