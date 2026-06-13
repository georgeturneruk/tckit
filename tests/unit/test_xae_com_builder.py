"""Tests for the xae_com_builder adapter (with a fake BridgeClient)."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest

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


def _make_sln(tmp_path: Path) -> Path:
    """Create an empty .sln so the adapter's project_path validation passes."""
    sln = tmp_path / "proj.sln"
    sln.write_text("")
    return sln


def test_build_success_parsed_into_build_result(tmp_path: Path) -> None:
    client = FakeBridgeClient(
        {
            "success": True,
            "errors": [],
            "warnings": [],
            "duration_seconds": 4.25,
        }
    )
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]
    sln = _make_sln(tmp_path)

    result = builder.build(str(sln))

    assert result.success is True
    assert result.duration_seconds == 4.25
    assert result.errors == []
    assert builder.get_status() == BuildStatus.SUCCESS

    path, payload, timeout = client.calls[0]
    assert path == "/build"
    assert payload == {"ProjectPath": str(sln)}
    # Timeouts now resolve in BridgeClient.post via the central
    # route_timeout map; the adapter no longer overrides per-call.
    assert timeout is None


def test_build_errors_parsed_with_severity(tmp_path: Path) -> None:
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

    result = builder.build(str(_make_sln(tmp_path)))

    assert result.success is False
    assert len(result.errors) == 1
    assert result.errors[0].file == "FB_X.TcPOU"
    assert result.errors[0].line == 42
    assert result.errors[0].severity == "error"
    assert len(result.warnings) == 1
    assert result.warnings[0].severity == "warning"
    assert builder.get_status() == BuildStatus.ERROR


def test_build_bridge_unavailable_yields_error_result(tmp_path: Path) -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("nope"))
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build(str(_make_sln(tmp_path)))

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
    # No explicit project_path was configured, so ProjectPath is omitted and
    # the bridge deploys whatever solution is open in the attached XAE.
    assert payload == {
        "TargetAmsId": "1.2.3.4.1.1",
        "BootAutostart": True,
    }
    assert "ProjectPath" not in payload


def test_deploy_passes_boot_autostart_false() -> None:
    client = FakeBridgeClient({"success": True})
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    builder.deploy("1.2.3.4.1.1", boot_autostart=False)

    _, payload, _ = client.calls[0]
    assert payload["BootAutostart"] is False


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
    assert "ProjectPath" not in payload


def test_get_status_starts_idle() -> None:
    builder = XaeComBuilder(client=FakeBridgeClient())  # type: ignore[arg-type]
    assert builder.get_status() == BuildStatus.IDLE


def test_deploy_failure_translated() -> None:
    client = FakeBridgeClient({"success": False, "error": "no route to host"})
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]
    result = builder.deploy("9.9.9.9.1.1")
    assert result.success is False
    assert result.error == "no route to host"


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
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.read_symbols(
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
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.read_symbols("1.2.3.4.1.1", [])

    assert result == {}
    assert client.calls == []  # no bridge round-trip needed


def test_read_symbols_unreadable_path_returns_none() -> None:
    client = FakeBridgeClient(
        {
            "success": True,
            "values": {"MAIN.has": "OK"},  # MAIN.missing absent
        }
    )
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.read_symbols("1.2.3.4.1.1", ["MAIN.has", "MAIN.missing"])

    assert result == {"MAIN.has": "OK", "MAIN.missing": None}


def test_read_symbols_bridge_unavailable_returns_all_none() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("nope"))
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.read_symbols("1.2.3.4.1.1", ["MAIN.foo", "MAIN.bar"])

    assert result == {"MAIN.foo": None, "MAIN.bar": None}


# ---------------------------------------------------------------------------
# project_path validation
# ---------------------------------------------------------------------------


def test_build_empty_project_path_errors_without_bridge_call() -> None:
    client = FakeBridgeClient()
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build("")

    assert result.success is False
    assert len(result.errors) == 1
    assert "project_path is required" in result.errors[0].message
    assert client.calls == []
    assert builder.get_status() == BuildStatus.ERROR


def test_build_missing_path_errors_without_bridge_call(tmp_path: Path) -> None:
    client = FakeBridgeClient()
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    missing = tmp_path / "nope.sln"
    result = builder.build(str(missing))

    assert result.success is False
    assert "does not exist" in result.errors[0].message
    assert str(missing) in result.errors[0].message
    assert client.calls == []


def test_build_directory_with_candidates_lists_them(tmp_path: Path) -> None:
    (tmp_path / "MySolution.sln").write_text("")
    (tmp_path / "MyPlc.tsproj").write_text("")
    (tmp_path / "readme.txt").write_text("ignore me")
    client = FakeBridgeClient()
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build(str(tmp_path))

    assert result.success is False
    msg = result.errors[0].message
    assert "directory" in msg
    assert "MySolution.sln" in msg
    assert "MyPlc.tsproj" in msg
    assert "readme.txt" not in msg
    assert client.calls == []


def test_build_directory_with_no_candidates_says_none(tmp_path: Path) -> None:
    client = FakeBridgeClient()
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build(str(tmp_path))

    assert result.success is False
    assert "Found in this directory: (none)" in result.errors[0].message


def test_build_wrong_extension_errors_without_bridge_call(tmp_path: Path) -> None:
    bad = tmp_path / "project.txt"
    bad.write_text("")
    client = FakeBridgeClient()
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build(str(bad))

    assert result.success is False
    assert "must end in .sln or .tsproj" in result.errors[0].message
    assert client.calls == []


@pytest.mark.parametrize("suffix", [".sln", ".tsproj", ".SLN", ".TsProj"])
def test_build_accepts_sln_and_tsproj_case_insensitively(
    tmp_path: Path, suffix: str
) -> None:
    proj = tmp_path / f"project{suffix}"
    proj.write_text("")
    client = FakeBridgeClient({"success": True, "errors": [], "warnings": []})
    builder = XaeComBuilder(client=client)  # type: ignore[arg-type]

    result = builder.build(str(proj))

    assert result.success is True
    assert client.calls[0][0] == "/build"
