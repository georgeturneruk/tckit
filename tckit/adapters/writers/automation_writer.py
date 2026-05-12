"""automation_writer — ProjectWriter adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell.DTE.17.0 COM interface.
Requires the bridge service to be running on the Windows machine with XAE
installed. The Docker container reads ``BRIDGE_URL`` from the environment.

Each method posts to the corresponding bridge route. The active project +
PLC sub-project name are forwarded from ``PLC_PROJECT_PATH`` and
``PLC_PROJECT_NAME`` env vars when not provided explicitly. A per-call
``plc_name`` keyword always wins over the env var; see ADR-0005.
``PLC_PROJECT_NAME`` is optional — the harness auto-resolves it when
there's only one PLC project in the sln.
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

    def add_pou(
        self,
        name: str,
        pou_type: POUType,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/pou",
            self._with_project(
                {
                    "Name": name,
                    "PouType": pou_type.value,
                    "Code": code,
                },
                plc_name=plc_name,
            ),
        )

    def add_method(
        self,
        pou_name: str,
        method_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/method",
            self._with_project(
                {
                    "PouName": pou_name,
                    "MethodName": method_name,
                    "Code": code,
                },
                plc_name=plc_name,
            ),
        )

    def update_pou_item(
        self,
        pou_name: str,
        item_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/item",
            self._with_project(
                {
                    "PouName": pou_name,
                    "ItemName": item_name,
                    "Code": code,
                },
                plc_name=plc_name,
            ),
        )

    def update_pou_item_patch(
        self,
        pou_name: str,
        item_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/item-patch",
            self._with_project(
                {
                    "PouName": pou_name,
                    "ItemName": item_name,
                    "OldString": old_string,
                    "NewString": new_string,
                },
                plc_name=plc_name,
            ),
        )

    def add_variable(
        self,
        pou_name: str,
        scope: str,
        declaration: str,
        item_name: str | None = None,
        *,
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "PouName": pou_name,
            "Scope": scope,
            "Declaration": declaration,
        }
        if item_name is not None:
            payload["ItemName"] = item_name
        return self._call(
            "/add-variable", self._with_project(payload, plc_name=plc_name)
        )

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _with_project(
        self,
        payload: dict[str, Any],
        *,
        plc_name: str | None = None,
    ) -> dict[str, Any]:
        """Attach the active solution path + PLC-project name to the payload.

        Per-call ``plc_name`` wins over ``PLC_PROJECT_NAME`` env var; both
        are optional. When neither is set, the bridge auto-resolves on a
        single-project sln and throws on a multi-project sln (see
        ``Resolve-TcPlcName`` in bridge/harness/_TcDte.psm1).
        """
        merged: dict[str, Any] = {
            "ProjectPath": os.getenv("PLC_PROJECT_PATH", "")
        }
        resolved_plc = plc_name or os.getenv("PLC_PROJECT_NAME")
        if resolved_plc:
            merged["PlcName"] = resolved_plc
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
