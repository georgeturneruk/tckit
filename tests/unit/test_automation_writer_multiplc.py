"""Tests for ADR-0009 multi-PLC adapter methods on AutomationWriter."""

from __future__ import annotations

from typing import Any

import pytest

from tckit.adapters.writers.automation_writer import AutomationWriter


class FakeBridgeClient:
    """Records calls and returns a configured response."""

    def __init__(self, response: dict[str, Any] | None = None):
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.response = response or {"success": True}

    def post(
        self,
        path: str,
        payload: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        self.calls.append((path, payload or {}))
        return self.response


# ---------------------------------------------------------------------------
# add_plc_project
# ---------------------------------------------------------------------------


def test_add_plc_project_posts_to_add_plc_project_endpoint() -> None:
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_plc_project("C:/work/B1.sln", "Tests")

    assert client.calls == [
        (
            "/add-plc-project",
            {
                "ProjectPath": "C:/work/B1.sln",
                "PlcName": "Tests",
                "ProjectType": "standard",
            },
        )
    ]


def test_add_plc_project_rejects_library_project_type() -> None:
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    result = writer.add_plc_project("C:/x.sln", "Lib", project_type="library")

    assert result.success is False
    assert "not yet supported" in (result.error or "")
    # Rejection happens before bridge call.
    assert client.calls == []


def test_add_plc_project_ignores_plc_project_path_env(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # add_plc_project takes sln_path explicitly — it should never pick up
    # PLC_PROJECT_PATH because the operation is solution-scoped.
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/wrong.sln")
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_plc_project("C:/right.sln", "Tests")

    _, payload = client.calls[0]
    assert payload["ProjectPath"] == "C:/right.sln"


# ---------------------------------------------------------------------------
# save_plc_as_library
# ---------------------------------------------------------------------------


def test_save_plc_as_library_payload_shape(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/work/B1.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.save_plc_as_library("Library", "C:/out/Library.library")

    path, payload = client.calls[0]
    assert path == "/save-as-library"
    assert payload == {
        "ProjectPath": "C:/work/B1.sln",
        "PlcName": "Library",
        "OutputPath": "C:/out/Library.library",
        "Install": True,
        "Repository": "System",
    }


def test_save_plc_as_library_install_false_passes_through(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/work/B1.sln")
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.save_plc_as_library(
        "Library", "C:/out/Library.library", install=False, repository="UserScope"
    )

    _, payload = client.calls[0]
    assert payload["Install"] is False
    assert payload["Repository"] == "UserScope"


# ---------------------------------------------------------------------------
# add_library_reference
# ---------------------------------------------------------------------------


def test_add_library_reference_payload_shape(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/work/B1.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_library_reference("Tests", "Library")

    path, payload = client.calls[0]
    assert path == "/add-library-reference"
    assert payload == {
        "ProjectPath": "C:/work/B1.sln",
        "PlcName": "Tests",
        "LibraryName": "Library",
        "Version": "*",
        "Distributor": "Tc3 Project",
    }


def test_add_library_reference_explicit_version_and_distributor(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/work/B1.sln")
    client = FakeBridgeClient({"success": True})
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    writer.add_library_reference(
        "Tests", "Tc2_Standard", version="3.3.0.0", distributor="Beckhoff Automation GmbH"
    )

    _, payload = client.calls[0]
    assert payload["LibraryName"] == "Tc2_Standard"
    assert payload["Version"] == "3.3.0.0"
    assert payload["Distributor"] == "Beckhoff Automation GmbH"


def test_add_library_reference_failure_translated_to_result() -> None:
    client = FakeBridgeClient(
        {"success": False, "error": "Library 'Library' (version *) not found in System repo."}
    )
    writer = AutomationWriter(client=client)  # type: ignore[arg-type]

    result = writer.add_library_reference("Tests", "Library")

    assert result.success is False
    assert "not found" in (result.error or "")
