"""xae_com_builder — BuildRunner adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell automation interface.
Returns structured build errors as ``BuildResult`` with file/line/message/severity.

Multi-project sln support (ADR-0005): ``build`` and ``deploy`` accept an
optional ``plc_name``; the value flows through to the bridge as ``PlcName``
in the POST body. The bridge harness's ``Resolve-TcPlcName`` enforces the
same auto-resolve / disambiguate policy on the Windows side.

Runtime control and ADS symbol I/O (start_runtime, read_symbols, write_symbols,
invoke_rpc) moved to XaeComRuntime (tckit/adapters/runtime/xae_com_runtime.py).
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Any

from tckit.ports.builder import BuildRunner
from tckit.ports.types import BuildError, BuildResult, BuildStatus, Result
from tckit.utils.bridge_client import BridgeClient, BridgeError

_VALID_PROJECT_SUFFIXES = (".sln", ".tsproj")


class XaeComBuilder(BuildRunner):
    """Builds and deploys TwinCAT projects via the XAE COM automation interface."""

    def __init__(
        self,
        client: BridgeClient | None = None,
        project_path: str | None = None,
    ) -> None:
        self._client = client or BridgeClient()
        # Explicit solution path for deploy on the programmatic / headless
        # path. The MCP server leaves this None so deploy targets the
        # solution open in the attached XAE. ``build`` takes its path as an
        # explicit argument and ignores this.
        self._project_path = project_path or None
        self._last_status: BuildStatus = BuildStatus.IDLE

    # ------------------------------------------------------------------
    # BuildRunner interface
    # ------------------------------------------------------------------

    def build(
        self, project_path: str, *, plc_name: str | None = None
    ) -> BuildResult:
        validation_error = _validate_project_path(project_path)
        if validation_error is not None:
            self._last_status = BuildStatus.ERROR
            return BuildResult(success=False, errors=[validation_error])

        self._last_status = BuildStatus.BUILDING
        payload: dict[str, Any] = {"ProjectPath": project_path}
        _attach_plc(payload, plc_name)
        try:
            resp = self._client.post("/build", payload)
        except BridgeError as exc:
            self._last_status = BuildStatus.ERROR
            return BuildResult(
                success=False,
                errors=[BuildError(file="", line=0, message=str(exc))],
            )

        result = _to_build_result(resp)
        self._last_status = BuildStatus.SUCCESS if result.success else BuildStatus.ERROR
        return result

    def deploy(
        self,
        target_ams_id: str,
        *,
        plc_name: str | None = None,
        boot_autostart: bool = True,
    ) -> Result:
        payload: dict[str, Any] = {
            "TargetAmsId": target_ams_id,
            "BootAutostart": bool(boot_autostart),
        }
        if self._project_path:
            payload["ProjectPath"] = self._project_path
        _attach_plc(payload, plc_name)
        try:
            resp = self._client.post("/deploy", payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return _to_result(resp)

    def get_status(self) -> BuildStatus:
        return self._last_status


def _attach_plc(payload: dict[str, Any], plc_name: str | None) -> None:
    """Set ``PlcName`` on the payload from the per-call value or env default."""
    resolved = plc_name or os.getenv("PLC_PROJECT_NAME")
    if resolved:
        payload["PlcName"] = resolved


def _validate_project_path(project_path: str) -> BuildError | None:
    """Pre-flight check that surfaces a clear error before the bridge round-trip.

    Returns a BuildError if the path is obviously wrong, else None. Catches
    the common failure mode where a directory or a bare project name is
    passed instead of an absolute path to a .sln or .tsproj file. Without
    this guard the bridge bubbles a raw COM ``STG_E_FILENOTFOUND`` with an
    empty file field, which leaves the caller guessing.
    """
    if not project_path:
        return BuildError(
            file="",
            line=0,
            message=(
                "project_path is required; pass an absolute path to a "
                ".sln or .tsproj file."
            ),
            severity="error",
        )
    path = Path(project_path)
    if not path.exists():
        return BuildError(
            file="",
            line=0,
            message=f"project_path '{project_path}' does not exist.",
            severity="error",
        )
    if path.is_dir():
        candidates = sorted(
            p.name
            for p in path.iterdir()
            if p.is_file() and p.suffix.lower() in _VALID_PROJECT_SUFFIXES
        )
        found = ", ".join(candidates) if candidates else "(none)"
        return BuildError(
            file="",
            line=0,
            message=(
                f"project_path must be a .sln or .tsproj file, got "
                f"directory '{project_path}'. "
                f"Found in this directory: {found}."
            ),
            severity="error",
        )
    if path.suffix.lower() not in _VALID_PROJECT_SUFFIXES:
        return BuildError(
            file="",
            line=0,
            message=(
                f"project_path must end in .sln or .tsproj, got "
                f"'{project_path}'."
            ),
            severity="error",
        )
    return None


# ---------------------------------------------------------------------------
# Response → dataclass mappers
# ---------------------------------------------------------------------------


def _to_build_result(resp: dict[str, Any]) -> BuildResult:
    duration = resp.get("duration_seconds")
    warnings_raw = resp.get("warnings") or []
    infos_raw = resp.get("infos") or []
    return BuildResult(
        success=bool(resp.get("success", False)),
        errors=[_to_build_error(e) for e in resp.get("errors") or []],
        warnings=[_to_build_error(w, default_severity="warning") for w in warnings_raw],
        infos=[_to_build_error(i, default_severity="info") for i in infos_raw],
        duration_seconds=float(duration) if duration is not None else None,
    )


def _to_build_error(item: dict[str, Any], default_severity: str = "error") -> BuildError:
    return BuildError(
        file=str(item.get("file", "")),
        line=int(item.get("line", 0) or 0),
        message=str(item.get("message", "")),
        severity=str(item.get("severity", default_severity)),
        code=str(item.get("code", "")),
        project=str(item.get("project", "")),
    )


def _to_result(resp: dict[str, Any]) -> Result:
    return Result(
        success=bool(resp.get("success", False)),
        error=resp.get("error"),
        details={k: v for k, v in resp.items() if k not in ("success", "error")},
    )
