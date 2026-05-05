"""xml_reader — ProjectReader adapter using XML + regex.

Reads .TcPOU and .TcGVL files directly from the filesystem.
Runs in Docker. No XAE, Windows, or blark dependency.

All structural information is extracted from XML attributes and element names.
ST code is returned as raw strings — no ST grammar parsing is performed.
"""

import os
from pathlib import Path

from tckit.adapters.readers._tcpou_parser import (
    extract_method_return_type,
    parse_tcgvl,
    parse_tcpou,
)
from tckit.ports.reader import ProjectReader
from tckit.ports.types import (
    GVL,
    MethodSignature,
    POUInterface,
    POUItem,
    POURef,
    POUType,
    ProjectStructure,
)


class XmlReader(ProjectReader):
    """Reads TwinCAT project structure and code via XML parsing (stdlib only)."""

    def __init__(self) -> None:
        # Maps POU/GVL name → absolute file path.
        # Populated by get_structure(); reused by all subsequent calls.
        self._file_index: dict[str, Path] = {}

    # ------------------------------------------------------------------
    # ProjectReader interface
    # ------------------------------------------------------------------

    def get_structure(self, project_path: str) -> ProjectStructure:
        """Scan project_path for .TcPOU and .TcGVL files, build file index.

        Raises:
            FileNotFoundError: If project_path does not exist.
        """
        root = Path(project_path)
        if not root.exists():
            raise FileNotFoundError(f"Project path not found: {project_path}")

        self._file_index = {}
        pous: list[POURef] = []
        gvls: list[str] = []
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

        return ProjectStructure(
            project_path=project_path,
            pous=pous,
            gvls=gvls,
            tasks=tasks,
        )

    def get_pou_interface(self, pou_name: str) -> POUInterface:
        """Return declarations and method signatures for a POU.

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

        return POUInterface(
            pou_name=pou_name,
            pou_type=POUType(info["pou_type"]),
            declaration=info["declaration"],
            methods=methods,
            properties=info["properties"],
            actions=[a["name"] for a in info["actions"]],
        )

    def get_pou_item(self, pou_name: str, item_name: str) -> POUItem:
        """Return declaration + body for a single method or action.

        Raises:
            FileNotFoundError: If pou_name or item_name is not found.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(pou_name, ".TcPOU")
        info = parse_tcpou(path)

        # Search methods first, then actions
        for collection in (info["methods"], info["actions"]):
            for item in collection:
                if item["name"] == item_name:
                    return POUItem(
                        pou_name=pou_name,
                        item_name=item_name,
                        declaration=item["declaration"],
                        body=item["body"],
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

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _resolve(self, name: str, extension: str) -> Path:
        """Return the file path for a named POU or GVL.

        If not in the file index, attempt to scan the directory specified
        by the PLC_PROJECT_PATH environment variable as a fallback.

        Raises:
            FileNotFoundError: If the file cannot be located.
        """
        if name in self._file_index:
            return self._file_index[name]

        # Fallback: scan env-var path if index is empty
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
