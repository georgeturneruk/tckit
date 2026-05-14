"""xae_com_builder — BuildRunner adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell automation interface.
Returns structured build errors as ``BuildResult`` with file/line/message/severity.

Multi-project sln support (ADR-0005): ``build`` and ``deploy`` accept an
optional ``plc_name``; the value flows through to the bridge as ``PlcName``
in the POST body. The bridge harness's ``Resolve-TcPlcName`` enforces the
same auto-resolve / disambiguate policy on the Windows side.
"""

from __future__ import annotations

import os
from typing import Any

from tckit.ports.builder import BuildRunner
from tckit.ports.types import BuildError, BuildResult, BuildStatus, Result
from tckit.utils.bridge_client import BridgeClient, BridgeError, build_timeout


class XaeComBuilder(BuildRunner):
    """Builds and deploys TwinCAT projects via the XAE COM automation interface."""

    def __init__(self, client: BridgeClient | None = None) -> None:
        self._client = client or BridgeClient()
        self._last_status: BuildStatus = BuildStatus.IDLE

    # ------------------------------------------------------------------
    # BuildRunner interface
    # ------------------------------------------------------------------

    def build(
        self, project_path: str, *, plc_name: str | None = None
    ) -> BuildResult:
        self._last_status = BuildStatus.BUILDING
        payload: dict[str, Any] = {"ProjectPath": project_path}
        _attach_plc(payload, plc_name)
        try:
            resp = self._client.post(
                "/build",
                payload,
                timeout=build_timeout(),
            )
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
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> Result:
        payload: dict[str, Any] = {"TargetAmsId": target_ams_id}
        _attach_plc(payload, plc_name)
        try:
            resp = self._client.post("/deploy", payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return _to_result(resp)

    def start_runtime(self, target_ams_id: str) -> Result:
        payload = {
            "TargetAmsId": target_ams_id,
            "Mode": "Run",
            "Wait": True,
        }
        try:
            resp = self._client.post("/runtime", payload)
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


# ---------------------------------------------------------------------------
# Response → dataclass mappers
# ---------------------------------------------------------------------------


def _to_build_result(resp: dict[str, Any]) -> BuildResult:
    duration = resp.get("duration_seconds")
    warnings_raw = resp.get("warnings") or []
    return BuildResult(
        success=bool(resp.get("success", False)),
        errors=[_to_build_error(e) for e in resp.get("errors") or []],
        warnings=[_to_build_error(w, default_severity="warning") for w in warnings_raw],
        duration_seconds=float(duration) if duration is not None else None,
    )


def _to_build_error(item: dict[str, Any], default_severity: str = "error") -> BuildError:
    return BuildError(
        file=str(item.get("file", "")),
        line=int(item.get("line", 0) or 0),
        message=str(item.get("message", "")),
        severity=str(item.get("severity", default_severity)),
    )


def _to_result(resp: dict[str, Any]) -> Result:
    return Result(
        success=bool(resp.get("success", False)),
        error=resp.get("error"),
        details={k: v for k, v in resp.items() if k not in ("success", "error")},
    )
