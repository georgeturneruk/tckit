"""xml_reader — ProjectReader adapter using XML + regex.

Reads .TcPOU, .TcGVL, and .TcDUT files directly from the filesystem.
Runs in Docker. No XAE, Windows, or blark dependency.

All structural information is extracted from XML attributes and element names.
ST code is returned as raw strings — no ST grammar parsing is performed.

Property access via get_pou_item():
  "PropName"      → property header declaration (no body)
  "PropName.Get"  → getter declaration + body
  "PropName.Set"  → setter declaration + body
"""

import os
from pathlib import Path

from tckit.utils.tc_file_parser import (
    extract_method_return_type,
    extract_property_return_type,
    parse_tcdut,
    parse_tcgvl,
    parse_tcpou,
)
from tckit.ports.reader import ProjectReader
from tckit.ports.types import (
    DUT,
    GVL,
    MethodSignature,
    POUInterface,
    POUItem,
    POURef,
    POUType,
    ProjectStructure,
    PropertySignature,
)


class XmlReader(ProjectReader):
    """Reads TwinCAT project structure and code via XML parsing (stdlib only)."""

    def __init__(self) -> None:
        # Maps POU/GVL/DUT name → absolute file path.
        # Populated by get_structure(); reused by all subsequent calls.
        self._file_index: dict[str, Path] = {}

    # ------------------------------------------------------------------
    # ProjectReader interface
    # ------------------------------------------------------------------

    def get_structure(self, project_path: str) -> ProjectStructure:
        """Scan project_path for .TcPOU, .TcGVL, and .TcDUT files, build file index.

        Raises:
            FileNotFoundError: If project_path does not exist.
        """
        root = Path(project_path)
        if not root.exists():
            raise FileNotFoundError(f"Project path not found: {project_path}")

        self._file_index = {}
        pous: list[POURef] = []
        gvls: list[str] = []
        duts: list[str] = []
        tasks: list[str] = []

        for tc_pou in sorted(root.rglob("*.TcPOU")):
            try:
                info = parse_tcpou(tc_pou)
            except (ValueError, FileNotFoundError):
                continue
            name = info["name"]
            self._file_index[name] = tc_pou
            pous.append(
                POURef(
                    name=name,
                    pou_type=POUType(info["pou_type"]),
                    path=str(tc_pou),
                )
            )

        for tc_gvl in sorted(root.rglob("*.TcGVL")):
            try:
                info = parse_tcgvl(tc_gvl)
            except (ValueError, FileNotFoundError):
                continue
            name = info["name"]
            self._file_index[name] = tc_gvl
            gvls.append(name)

        for tc_dut in sorted(root.rglob("*.TcDUT")):
            try:
                info = parse_tcdut(tc_dut)
            except (ValueError, FileNotFoundError):
                continue
            name = info["name"]
            self._file_index[name] = tc_dut
            duts.append(name)

        return ProjectStructure(
            project_path=project_path,
            pous=pous,
            gvls=gvls,
            duts=duts,
            tasks=tasks,
        )

    def get_pou_interface(self, pou_name: str) -> POUInterface:
        """Return declarations and method/property signatures for a POU or interface.

        Raises:
            FileNotFoundError: If pou_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(pou_name, ".TcPOU")
        info = parse_tcpou(path)

        methods = [
            MethodSignature(
                name=m["name"],
                return_type=extract_method_return_type(m["declaration"]),
                declaration=m["declaration"],
            )
            for m in info["methods"]
        ]

        properties = [
            PropertySignature(
                name=p["name"],
                return_type=extract_property_return_type(p["declaration"]),
                declaration=p["declaration"],
                has_get=p["get"] is not None,
                has_set=p["set"] is not None,
            )
            for p in info["properties"]
        ]

        return POUInterface(
            pou_name=pou_name,
            pou_type=POUType(info["pou_type"]),
            declaration=info["declaration"],
            methods=methods,
            properties=properties,
            actions=[a["name"] for a in info["actions"]],
        )

    def get_pou_item(self, pou_name: str, item_name: str) -> POUItem:
        """Return declaration + body for a single method, action, or property accessor.

        item_name formats:
          "Execute"       → method or action named Execute
          "Status"        → property declaration (no body — use .Get/.Set for bodies)
          "Status.Get"    → property getter declaration + body
          "Status.Set"    → property setter declaration + body

        Raises:
            FileNotFoundError: If pou_name or item_name is not found.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(pou_name, ".TcPOU")
        info = parse_tcpou(path)

        # Check for property accessor syntax first ("PropName.Get" / "PropName.Set")
        if "." in item_name:
            prop_name, accessor = item_name.rsplit(".", 1)
            accessor = accessor.lower()
            for prop in info["properties"]:
                if prop["name"] == prop_name:
                    acc_data = prop.get(accessor)  # prop["get"] or prop["set"]
                    if acc_data is None:
                        raise FileNotFoundError(
                            f"Property {prop_name!r} in {pou_name!r} "
                            f"has no {accessor.capitalize()} accessor"
                        )
                    return POUItem(
                        pou_name=pou_name,
                        item_name=item_name,
                        declaration=acc_data["declaration"],
                        body=acc_data["body"],
                    )
            raise FileNotFoundError(
                f"Property {prop_name!r} not found in POU {pou_name!r} ({path})"
            )

        # Search methods, then actions, then bare property (declaration only)
        for m in info["methods"]:
            if m["name"] == item_name:
                return POUItem(
                    pou_name=pou_name,
                    item_name=item_name,
                    declaration=m["declaration"],
                    body=m["body"],
                )

        for a in info["actions"]:
            if a["name"] == item_name:
                return POUItem(
                    pou_name=pou_name,
                    item_name=item_name,
                    declaration=a["declaration"],
                    body=a["body"],
                )

        for p in info["properties"]:
            if p["name"] == item_name:
                # Return the property header declaration; body is in .Get / .Set
                return POUItem(
                    pou_name=pou_name,
                    item_name=item_name,
                    declaration=p["declaration"],
                    body="",
                )

        raise FileNotFoundError(
            f"Item {item_name!r} not found in POU {pou_name!r} ({path})"
        )

    def get_gvl(self, gvl_name: str) -> GVL:
        """Return declaration for a Global Variable List.

        Raises:
            FileNotFoundError: If gvl_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(gvl_name, ".TcGVL")
        info = parse_tcgvl(path)
        return GVL(name=gvl_name, path=str(path), declaration=info["declaration"])

    def get_dut(self, dut_name: str) -> DUT:
        """Return declaration for a Data Unit Type (STRUCT, ENUM, UNION).

        Raises:
            FileNotFoundError: If dut_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(dut_name, ".TcDUT")
        info = parse_tcdut(path)
        return DUT(name=dut_name, path=str(path), declaration=info["declaration"])

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _resolve(self, name: str, extension: str) -> Path:
        """Return the file path for a named POU, GVL, or DUT.

        Falls back to scanning PLC_PROJECT_PATH if the name is not in the index.

        Raises:
            FileNotFoundError: If the file cannot be located.
        """
        if name in self._file_index:
            return self._file_index[name]

        env_path = os.getenv("PLC_PROJECT_PATH")
        if env_path:
            root = Path(env_path)
            for candidate in root.rglob(f"*{extension}"):
                if candidate.stem == name:
                    self._file_index[name] = candidate
                    return candidate

        searched = os.getenv("PLC_PROJECT_PATH", "(no PLC_PROJECT_PATH set)")
        raise FileNotFoundError(
            f"No {extension} file found for {name!r}. "
            f"Call get_structure() first, or set PLC_PROJECT_PATH. "
            f"Searched: {searched}"
        )
