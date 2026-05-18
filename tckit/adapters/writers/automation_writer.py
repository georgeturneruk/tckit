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
from typing import Any, Literal

from tckit.ports.types import DUTKind, POUType, Result
from tckit.ports.writer import ProjectWriter
from tckit.utils.bridge_client import BridgeClient, BridgeError
from tckit.utils.results import to_result


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

    def add_gvl(
        self,
        name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/gvl",
            self._with_project(
                {"Name": name, "Code": code},
                plc_name=plc_name,
            ),
        )

    def add_dut(
        self,
        name: str,
        code: str,
        *,
        dut_kind: DUTKind = DUTKind.STRUCT,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/dut",
            self._with_project(
                {
                    "Name": name,
                    "DutKind": dut_kind.value,
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

    def add_property(
        self,
        pou_name: str,
        property_name: str,
        return_type: str,
        *,
        getter_code: str | None = None,
        setter_code: str | None = None,
        plc_name: str | None = None,
    ) -> Result:
        if getter_code is None and setter_code is None:
            return Result(
                success=False,
                error=(
                    "add_property requires at least one of "
                    "getter_code or setter_code."
                ),
            )
        payload: dict[str, Any] = {
            "PouName": pou_name,
            "PropertyName": property_name,
            "ReturnType": return_type,
        }
        if getter_code is not None:
            payload["GetterCode"] = getter_code
        if setter_code is not None:
            payload["SetterCode"] = setter_code
        return self._call(
            "/property",
            self._with_project(payload, plc_name=plc_name),
        )

    def update_pou_declaration(
        self,
        pou_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/pou-declaration",
            self._with_project(
                {"PouName": pou_name, "Code": code},
                plc_name=plc_name,
            ),
        )

    def update_pou_implementation(
        self,
        pou_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/pou-implementation",
            self._with_project(
                {"PouName": pou_name, "Code": code},
                plc_name=plc_name,
            ),
        )

    def update_method_body(
        self,
        pou_name: str,
        method_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/method-body",
            self._with_project(
                {
                    "PouName": pou_name,
                    "MethodName": method_name,
                    "Code": code,
                },
                plc_name=plc_name,
            ),
        )

    def update_pou_declaration_patch(
        self,
        pou_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/pou-declaration-patch",
            self._with_project(
                {
                    "PouName": pou_name,
                    "OldString": old_string,
                    "NewString": new_string,
                },
                plc_name=plc_name,
            ),
        )

    def update_pou_implementation_patch(
        self,
        pou_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/pou-implementation-patch",
            self._with_project(
                {
                    "PouName": pou_name,
                    "OldString": old_string,
                    "NewString": new_string,
                },
                plc_name=plc_name,
            ),
        )

    def update_method_body_patch(
        self,
        pou_name: str,
        method_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/method-body-patch",
            self._with_project(
                {
                    "PouName": pou_name,
                    "MethodName": method_name,
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

    def add_plc_project(
        self,
        sln_path: str,
        plc_name: str,
        *,
        project_type: Literal["standard", "library"] = "standard",
    ) -> Result:
        if project_type == "library":
            return Result(
                success=False,
                error="project_type='library' not yet supported; pass 'standard'.",
            )
        return self._call(
            "/add-plc-project",
            {
                "ProjectPath": sln_path,
                "PlcName": plc_name,
                "ProjectType": project_type,
            },
        )

    def save_plc_as_library(
        self,
        plc_name: str,
        output_path: str,
        *,
        install: bool = True,
        repository: str = "System",
        overwrite: bool = False,
    ) -> Result:
        return self._call(
            "/save-as-library",
            self._with_project(
                {
                    "OutputPath": output_path,
                    "Install": install,
                    "Repository": repository,
                    "Overwrite": overwrite,
                },
                plc_name=plc_name,
            ),
        )

    def add_library_reference(
        self,
        consumer_plc_name: str,
        library_name: str,
        *,
        version: str = "*",
        distributor: str = "Tc3 Project",
    ) -> Result:
        return self._call(
            "/add-library-reference",
            self._with_project(
                {
                    "LibraryName": library_name,
                    "Version": version,
                    "Distributor": distributor,
                },
                plc_name=consumer_plc_name,
            ),
        )

    def add_library_placeholder(
        self,
        consumer_plc_name: str,
        placeholder_name: str,
        default_library: str,
        *,
        version: str = "*",
        distributor: str = "",
        parameters: dict[str, dict[str, str]] | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "PlaceholderName": placeholder_name,
            "DefaultLibrary": default_library,
            "Version": version,
            "Distributor": distributor,
        }
        if parameters:
            payload["Parameters"] = {
                list_name: dict(keys) for list_name, keys in parameters.items()
            }
        return self._call(
            "/add-library-placeholder",
            self._with_project(payload, plc_name=consumer_plc_name),
        )

    def set_placeholder_parameters(
        self,
        consumer_plc_name: str,
        placeholder_name: str,
        parameters: dict[str, dict[str, str]],
    ) -> Result:
        payload: dict[str, Any] = {
            "PlaceholderName": placeholder_name,
            "Parameters": {
                list_name: dict(keys) for list_name, keys in parameters.items()
            },
        }
        return self._call(
            "/set-placeholder-parameters",
            self._with_project(payload, plc_name=consumer_plc_name),
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
        return to_result(resp)
