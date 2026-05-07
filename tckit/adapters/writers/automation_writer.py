"""automation_writer — ProjectWriter adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell.DTE.17.0 COM interface.
Requires the bridge service to be running on the Windows machine with XAE
installed. The Docker container reads ``BRIDGE_URL`` from the environment.

Each method posts to the corresponding bridge route. The active project +
PLC sub-project name are forwarded from ``PLC_PROJECT_PATH`` and
``PLC_PROJECT_NAME`` env vars when not provided explicitly. ``PLC_PROJECT_NAME``
is optional — the harness auto-resolves it when there's only one PLC project.
"""

from __future__ import annotations

import os
from typing import Any

from tckit.ports.types import POUType, Result
from tckit.ports.writer import ProjectWriter
from tckit.utils.bridge_client import BridgeClient, BridgeError


class AutomationWriter(ProjectWriter):
    """Writes to TwinCAT project via the automation interface (bridge → COM)."""

    def __init__(self, client: BridgeClient | None = None) -> None:
        self._client = client or BridgeClient()

    # ------------------------------------------------------------------
    # ProjectWriter interface
    # ------------------------------------------------------------------

    def open_project(self, solution_path: str) -> Result:
        return self._call("/open", {"SolutionPath": solution_path})

    def create_project(self, name: str, path: str) -> Result:
        return self._call("/create", {"Name": name, "Path": path})

    def add_pou(self, name: str, pou_type: POUType, code: str) -> Result:
        return self._call(
            "/pou",
            self._with_project(
                {
                    "Name": name,
                    "PouType": pou_type.value,
                    "Code": code,
                }
            ),
        )

    def add_method(self, pou_name: str, method_name: str, code: str) -> Result:
        return self._call(
            "/method",
            self._with_project(
                {
                    "PouName": pou_name,
                    "MethodName": method_name,
                    "Code": code,
                }
            ),
        )

    def update_pou_item(self, pou_name: str, item_name: str, code: str) -> Result:
        return self._call(
            "/item",
            self._with_project(
                {
                    "PouName": pou_name,
                    "ItemName": item_name,
                    "Code": code,
                }
            ),
        )

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _with_project(self, payload: dict[str, Any]) -> dict[str, Any]:
        """Attach the active solution path + PLC name from env to the payload."""
        merged = {"ProjectPath": os.getenv("PLC_PROJECT_PATH", "")}
        plc_name = os.getenv("PLC_PROJECT_NAME")
        if plc_name:
            merged["PlcName"] = plc_name
        merged.update(payload)
        return merged

    def _call(self, path: str, payload: dict[str, Any]) -> Result:
        try:
            resp = self._client.post(path, payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return _to_result(resp)


def _to_result(resp: dict[str, Any]) -> Result:
    return Result(
        success=bool(resp.get("success", False)),
        error=resp.get("error"),
        details={k: v for k, v in resp.items() if k not in ("success", "error")},
    )
