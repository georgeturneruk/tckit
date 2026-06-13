"""Tests for the automation_writer adapter (with a fake BridgeClient)."""

from __future__ import annotations

from typing import Any

import pytest

from tckit.adapters.writers.automation_writer import AutomationWriter
from tckit.ports.types import DUTKind, POUType
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
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    result = writer.open_project("C:/x.sln")

    assert result.success is True
    assert client.calls == [("/open", {"SolutionPath": "C:/x.sln"})]
    assert result.details == {"details": {"solution": "C:/x.sln"}}


def test_create_project_posts_to_create_endpoint() -> None:
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.create_project("MyProj", "C:/work")

    assert client.calls == [("/create", {"Name": "MyProj", "Path": "C:/work"})]


def test_add_pou_includes_project_path_and_translates_pou_type(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

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


def test_add_pou_omits_project_path_when_unconfigured(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # With no explicit project_path the writer sends no ProjectPath, so the
    # bridge operates on the solution open in the attached XAE.
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_pou("FB_Test", POUType.FUNCTION_BLOCK, "VAR END_VAR")

    _, payload = client.calls[0]
    assert "ProjectPath" not in payload


def test_add_pou_includes_plc_name_when_env_set(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "MyPlc")
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_pou("FB_X", POUType.FUNCTION_BLOCK, "decl")

    _, payload = client.calls[0]
    assert payload["PlcName"] == "MyPlc"


def test_add_method_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_method("FB_X", "DoThing", "code")

    path, payload = client.calls[0]
    assert path == "/method"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "MethodName": "DoThing",
        "Code": "code",
    }


def test_add_property_with_getter_only(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_property(
        "FB_Pid", "Kp", "LREAL", getter_code="Kp := fKp;"
    )

    path, payload = client.calls[0]
    assert path == "/property"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_Pid",
        "PropertyName": "Kp",
        "ReturnType": "LREAL",
        "GetterCode": "Kp := fKp;",
    }
    assert "SetterCode" not in payload


def test_add_property_with_setter_only(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_property(
        "FB_Pid", "Kp", "LREAL", setter_code="IF Kp >= 0 THEN fKp := Kp; END_IF"
    )

    path, payload = client.calls[0]
    assert path == "/property"
    assert payload["SetterCode"] == "IF Kp >= 0 THEN fKp := Kp; END_IF"
    assert "GetterCode" not in payload


def test_add_property_with_both_accessors(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_property(
        "FB_Pid",
        "Kp",
        "LREAL",
        getter_code="Kp := fKp;",
        setter_code="fKp := Kp;",
    )

    _, payload = client.calls[0]
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_Pid",
        "PropertyName": "Kp",
        "ReturnType": "LREAL",
        "GetterCode": "Kp := fKp;",
        "SetterCode": "fKp := Kp;",
    }


def test_add_property_rejects_when_no_accessors_supplied(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    result = writer.add_property("FB_Pid", "Kp", "LREAL")

    assert result.success is False
    assert "at least one" in result.error.lower()
    assert client.calls == []


def test_add_property_passes_plc_name(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_property(
        "FB_Pid",
        "Kp",
        "LREAL",
        getter_code="Kp := 0;",
        plc_name="LibPlc",
    )

    _, payload = client.calls[0]
    assert payload["PlcName"] == "LibPlc"


def test_add_dut_defaults_to_struct(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_dut(
        "ST_Config",
        "TYPE ST_Config :\nSTRUCT\n    fKp : LREAL;\nEND_STRUCT\nEND_TYPE",
    )

    path, payload = client.calls[0]
    assert path == "/dut"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "Name": "ST_Config",
        "DutKind": "struct",
        "Code": "TYPE ST_Config :\nSTRUCT\n    fKp : LREAL;\nEND_STRUCT\nEND_TYPE",
    }


def test_add_dut_enum_routes_correctly(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_dut(
        "E_PidMode",
        "TYPE E_PidMode : (\n    DIRECT,\n    REVERSE\n) END_TYPE",
        dut_kind=DUTKind.ENUM,
    )

    _, payload = client.calls[0]
    assert payload["DutKind"] == "enum"


def test_add_dut_union_routes_correctly(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_dut(
        "U_FloatBytes",
        "TYPE U_FloatBytes :\nUNION\n    fValue : LREAL;\n    aBytes : ARRAY[0..7] OF BYTE;\nEND_UNION\nEND_TYPE",
        dut_kind=DUTKind.UNION,
    )

    _, payload = client.calls[0]
    assert payload["DutKind"] == "union"


def test_add_dut_passes_plc_name(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_dut(
        "ST_Config",
        "TYPE ST_Config : STRUCT END_STRUCT END_TYPE",
        plc_name="LibPlc",
    )

    _, payload = client.calls[0]
    assert payload["PlcName"] == "LibPlc"


def test_update_pou_declaration_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.update_pou_declaration("FB_X", "FUNCTION_BLOCK FB_X\nVAR\nEND_VAR\n")

    path, payload = client.calls[0]
    assert path == "/pou-declaration"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "Code": "FUNCTION_BLOCK FB_X\nVAR\nEND_VAR\n",
    }


def test_update_pou_implementation_payload_shape(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.update_pou_implementation("FB_X", "x := 1;\n")

    path, payload = client.calls[0]
    assert path == "/pou-implementation"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "Code": "x := 1;\n",
    }


def test_update_method_body_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.update_method_body("FB_X", "Execute", "METHOD Execute : BOOL\nbDone := TRUE;\n")

    path, payload = client.calls[0]
    assert path == "/method-body"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "MethodName": "Execute",
        "Code": "METHOD Execute : BOOL\nbDone := TRUE;\n",
    }


def test_update_pou_declaration_patch_payload_shape(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.update_pou_declaration_patch("FB_X", "OLD", "NEW")

    path, payload = client.calls[0]
    assert path == "/pou-declaration-patch"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "OldString": "OLD",
        "NewString": "NEW",
    }


def test_update_pou_implementation_patch_payload_shape(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.update_pou_implementation_patch("FB_X", "OLD", "NEW")

    path, payload = client.calls[0]
    assert path == "/pou-implementation-patch"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "OldString": "OLD",
        "NewString": "NEW",
    }


def test_update_method_body_patch_payload_shape(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.update_method_body_patch("FB_X", "Execute", "OLD", "NEW")

    path, payload = client.calls[0]
    assert path == "/method-body-patch"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "PouName": "FB_X",
        "MethodName": "Execute",
        "OldString": "OLD",
        "NewString": "NEW",
    }


def test_update_method_body_patch_failure_translated_to_result() -> None:
    client = FakeBridgeClient(
        {"success": False, "error": "OldString appears 2 times; anchor must be unique."}
    )
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    result = writer.update_method_body_patch("FB_X", "Execute", "x", "y")
    assert result.success is False
    assert "unique" in (result.error or "")


def test_add_variable_payload_shape_fb_level(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

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
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    writer.add_variable("FB_X", "VAR", "nLocal : INT;", item_name="Execute")

    _, payload = client.calls[0]
    assert payload["ItemName"] == "Execute"
    assert payload["Scope"] == "VAR"


def test_failure_response_translated_to_result() -> None:
    client = FakeBridgeClient({"success": False, "error": "POU not found"})
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    result = writer.update_method_body("Missing", "X", "code")
    assert result.success is False
    assert result.error == "POU not found"


def test_bridge_unavailable_returned_as_failure_result() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("not reachable at http://foo"))
    writer = AutomationWriter(client=client, project_path="C:/proj/foo.sln")  # type: ignore[arg-type]

    result = writer.open_project("C:/x.sln")
    assert result.success is False
    assert "not reachable" in (result.error or "")
