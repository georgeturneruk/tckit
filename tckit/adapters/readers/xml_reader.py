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

from tckit.ports.reader import ProjectReader
from tckit.ports.types import (
    DUT,
    GVL,
    LibraryRef,
    MethodSignature,
    POUInterface,
    POUItem,
    POURef,
    POUType,
    ProjectStructure,
    PropertySignature,
    TaskInfo,
)
from tckit.utils.tc_file_parser import (
    extract_method_return_type,
    extract_property_return_type,
    parse_plcproj,
    parse_tcdut,
    parse_tcgvl,
    parse_tcpou,
    parse_tctto,
    parse_tsproj,
    strip_method_locals,
)


class XmlReader(ProjectReader):
    """Reads TwinCAT project structure and code via XML parsing (stdlib only)."""

    def __init__(self) -> None:
        # Maps POU/GVL/DUT name → absolute file path.
        # Populated by get_structure(); reused by all subsequent calls.
        self._file_index: dict[str, Path] = {}
        # Remembered context for the index: the project path it was built
        # for, and the .plcproj file (plus its mtime) we use as a staleness
        # signal. TwinCAT rewrites .plcproj on any structural change
        # (add/remove/rename of POUs), so its mtime is a cheap and
        # semantically meaningful invalidation trigger. See ADR-0004.
        self._index_project_path: str | None = None
        self._index_plcproj: Path | None = None
        self._index_mtime: float | None = None

    # ------------------------------------------------------------------
    # ProjectReader interface
    # ------------------------------------------------------------------

    def get_structure(self, project_path: str) -> ProjectStructure:
        """Scan project_path for TwinCAT project files and build the structure.

        Walks the tree once for .TcPOU / .TcGVL / .TcDUT (code) and once for
        the project-shaping files .plcproj / .tsproj / .TcTTO (subsystem
        context). POU folder is computed relative to the .plcproj directory
        when one is found, falling back to ``project_path`` otherwise. Tasks
        prefer .TcTTO data (cycle in µs, POU binding) and merge in any extra
        .tsproj tasks not already represented.

        Raises:
            FileNotFoundError: If project_path does not exist.
        """
        root = Path(project_path)
        if not root.exists():
            raise FileNotFoundError(f"Project path not found: {project_path}")

        # The .plcproj directory anchors the POU folder calculation. If
        # multiple .plcproj files live under project_path, pick the
        # shallowest so we end up with a sensible common root.
        plcproj_paths = sorted(root.rglob("*.plcproj"), key=lambda p: len(p.parts))
        folder_root = plcproj_paths[0].parent if plcproj_paths else root

        self._file_index = {}
        pous: list[POURef] = []
        gvls: list[str] = []
        duts: list[str] = []

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
                    folder=_folder_for(tc_pou, folder_root),
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

        libraries = _collect_libraries(plcproj_paths)
        tasks = _collect_tasks(root)

        # Record the staleness context after a successful rebuild so the
        # next read can decide whether to trust the index.
        self._index_project_path = project_path
        if plcproj_paths:
            self._index_plcproj = plcproj_paths[0]
            try:
                self._index_mtime = plcproj_paths[0].stat().st_mtime
            except OSError:
                self._index_mtime = None
        else:
            self._index_plcproj = None
            self._index_mtime = None

        return ProjectStructure(
            project_path=project_path,
            pous=pous,
            gvls=gvls,
            duts=duts,
            tasks=tasks,
            libraries=libraries,
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
                # Strip method-local VAR blocks: they are implementation detail,
                # not API surface. get_pou_item still returns the full declaration.
                declaration=strip_method_locals(m["declaration"]),
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

    def _refresh_index_if_stale(self) -> None:
        """Rebuild the file index if the recorded .plcproj has changed.

        TwinCAT rewrites .plcproj on any structural change (POU added,
        removed, renamed). Body-only edits do not touch it, so the warm
        path stays warm. One stat() call per read.
        """
        if self._index_plcproj is None or self._index_project_path is None:
            return
        try:
            current = self._index_plcproj.stat().st_mtime
        except OSError:
            # .plcproj disappeared since the last build — assume stale and
            # let get_structure raise FileNotFoundError if the whole tree
            # has gone away.
            current = None
        if current != self._index_mtime:
            self.get_structure(self._index_project_path)

    def _resolve(self, name: str, extension: str) -> Path:
        """Return the file path for a named POU, GVL, or DUT.

        Falls back to scanning PLC_PROJECT_PATH if the name is not in the index.

        Raises:
            FileNotFoundError: If the file cannot be located.
        """
        self._refresh_index_if_stale()
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


# ----------------------------------------------------------------------------
# Module-level helpers for project-shaping data
# ----------------------------------------------------------------------------


def _folder_for(pou_path: Path, folder_root: Path) -> str:
    """Folder of a POU relative to the PLC project root, posix-style.

    Returns "" when the POU sits directly at folder_root or when the path
    cannot be made relative to it.
    """
    try:
        rel = pou_path.parent.relative_to(folder_root)
    except ValueError:
        return ""
    if str(rel) in ("", "."):
        return ""
    return rel.as_posix()


def _collect_libraries(plcproj_paths: list[Path]) -> list[LibraryRef]:
    """Aggregate libraries across one or more .plcproj files (deduplicated)."""
    seen: dict[tuple[str, str | None], LibraryRef] = {}
    for plcproj in plcproj_paths:
        try:
            data = parse_plcproj(plcproj)
        except (ValueError, FileNotFoundError):
            continue
        for lib in data["libraries"]:
            key = (lib["name"], lib["placeholder"])
            if key in seen:
                continue
            seen[key] = LibraryRef(
                name=lib["name"],
                version=lib["version"],
                placeholder=lib["placeholder"],
            )
    return list(seen.values())


def _collect_tasks(root: Path) -> list[TaskInfo]:
    """Build TaskInfo list, preferring .TcTTO over .tsproj for richness.

    .TcTTO carries cycle in µs plus the bound POU; .tsproj carries cycle in
    100ns ticks with no binding. We start from .TcTTO and merge any extra
    .tsproj tasks that lack a .TcTTO counterpart.
    """
    tasks: dict[str, TaskInfo] = {}

    for tctto in sorted(root.rglob("*.TcTTO")):
        try:
            data = parse_tctto(tctto)
        except (ValueError, FileNotFoundError):
            continue
        name = data["name"]
        if not name or name in tasks:
            continue
        tasks[name] = TaskInfo(
            name=name,
            cycle_time_us=data["cycle_time_us"],
            priority=data["priority"],
            programs=list(data["programs"]),
        )

    for tsproj in sorted(root.rglob("*.tsproj")):
        try:
            data = parse_tsproj(tsproj)
        except (ValueError, FileNotFoundError):
            continue
        for entry in data["tasks"]:
            name = entry["name"]
            if not name or name in tasks:
                continue
            tasks[name] = TaskInfo(
                name=name,
                cycle_time_us=entry["cycle_time_us"],
                priority=entry["priority"],
                programs=list(entry["programs"]),
            )

    return list(tasks.values())
