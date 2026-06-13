"""automation_writer — ProjectWriter adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell.DTE.17.0 COM interface.
Requires the bridge service to be running on the Windows machine with XAE
installed. The Docker container reads ``BRIDGE_URL`` from the environment.

Each method posts to the corresponding bridge route. By default the bridge
operates on the solution already open in the attached XAE; an explicit
solution path is only sent when the writer is constructed with
``project_path`` (programmatic / headless callers). The PLC sub-project name
comes from the per-call ``plc_name`` keyword, falling back to the
``PLC_PROJECT_NAME`` env var (see ADR-0005); both are optional, and the
harness auto-resolves the name when there's only one PLC project in the sln.
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

    def __init__(
        self,
        client: BridgeClient | None = None,
        project_path: str | None = None,
    ) -> None:
        self._client = client or BridgeClient()
        # Explicit solution path for programmatic / headless callers. The
        # MCP server leaves this None so edits land in whatever solution is
        # open in the attached XAE; passing a path here force-opens it,
        # which is only wanted off the interactive path (bench, scripts).
        self._project_path = project_path or None

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
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "Name": name,
            "PouType": pou_type.value,
            "Code": code,
        }
        if parent_folder:
            payload["ParentFolder"] = parent_folder
        return self._call("/pou", self._with_project(payload, plc_name=plc_name))

    def add_folder(
        self,
        name: str,
        *,
        parent_path: str = "POUs",
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/add-folder",
            self._with_project(
                {"Name": name, "ParentPath": parent_path},
                plc_name=plc_name,
            ),
        )

    def add_gvl(
        self,
        name: str,
        code: str,
        *,
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {"Name": name, "Code": code}
        if parent_folder:
            payload["ParentFolder"] = parent_folder
        return self._call("/gvl", self._with_project(payload, plc_name=plc_name))

    def add_dut(
        self,
        name: str,
        code: str,
        *,
        dut_kind: DUTKind = DUTKind.STRUCT,
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "Name": name,
            "DutKind": dut_kind.value,
            "Code": code,
        }
        if parent_folder:
            payload["ParentFolder"] = parent_folder
        return self._call("/dut", self._with_project(payload, plc_name=plc_name))

    def add_method(
        self,
        pou_name: str,
        method_name: str,
        code: str,
        *,
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "PouName": pou_name,
            "MethodName": method_name,
            "Code": code,
        }
        if parent_folder:
            payload["ParentFolder"] = parent_folder
        return self._call("/method", self._with_project(payload, plc_name=plc_name))

    def add_property(
        self,
        pou_name: str,
        property_name: str,
        return_type: str,
        *,
        getter_code: str | None = None,
        setter_code: str | None = None,
        parent_folder: str = "",
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
        if parent_folder:
            payload["ParentFolder"] = parent_folder
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

    def delete_pou(
        self,
        name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/delete-pou",
            self._with_project({"Name": name}, plc_name=plc_name),
        )

    def delete_method(
        self,
        pou_name: str,
        method_name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/delete-method",
            self._with_project(
                {"PouName": pou_name, "MethodName": method_name},
                plc_name=plc_name,
            ),
        )

    def delete_property(
        self,
        pou_name: str,
        property_name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/delete-property",
            self._with_project(
                {"PouName": pou_name, "PropertyName": property_name},
                plc_name=plc_name,
            ),
        )

    def delete_gvl(
        self,
        name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/delete-gvl",
            self._with_project({"Name": name}, plc_name=plc_name),
        )

    def delete_dut(
        self,
        name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        return self._call(
            "/delete-dut",
            self._with_project({"Name": name}, plc_name=plc_name),
        )

    def delete_variable(
        self,
        pou_name: str,
        variable_name: str,
        item_name: str | None = None,
        *,
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "PouName": pou_name,
            "VariableName": variable_name,
        }
        if item_name is not None:
            payload["ItemName"] = item_name
        return self._call(
            "/delete-variable",
            self._with_project(payload, plc_name=plc_name),
        )

    def delete_folder(
        self,
        name: str,
        *,
        parent_path: str = "",
        recursive: bool = False,
        plc_name: str | None = None,
    ) -> Result:
        payload: dict[str, Any] = {
            "Name": name,
            "Recursive": recursive,
        }
        if parent_path:
            payload["ParentPath"] = parent_path
        return self._call(
            "/delete-folder",
            self._with_project(payload, plc_name=plc_name),
        )

    def delete_library_reference(
        self,
        consumer_plc_name: str,
        library_name: str,
        *,
        version: str = "*",
        distributor: str = "Tc3 Project",
    ) -> Result:
        return self._call(
            "/delete-library-reference",
            self._with_project(
                {
                    "LibraryName": library_name,
                    "Version": version,
                    "Distributor": distributor,
                },
                plc_name=consumer_plc_name,
            ),
        )

    def delete_placeholder(
        self,
        consumer_plc_name: str,
        placeholder_name: str,
    ) -> Result:
        return self._call(
            "/delete-placeholder",
            self._with_project(
                {"PlaceholderName": placeholder_name},
                plc_name=consumer_plc_name,
            ),
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
        """Attach the explicit solution path (if any) + PLC-project name.

        ``ProjectPath`` is only sent when this writer was constructed with an
        explicit path; otherwise it is omitted and the bridge operates on the
        solution already open in the attached XAE. Per-call ``plc_name`` wins
        over ``PLC_PROJECT_NAME`` env var; both are optional. When neither is
        set, the bridge auto-resolves on a single-project sln and throws on a
        multi-project sln (see ``Resolve-TcPlcName`` in
        bridge/harness/_TcDte.psm1).
        """
        merged: dict[str, Any] = {}
        if self._project_path:
            merged["ProjectPath"] = self._project_path
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
