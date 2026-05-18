"""xml_reader — ProjectReader adapter using XML + regex.

Reads .TcPOU, .TcGVL, and .TcDUT files directly from the filesystem.
Runs in Docker. No XAE, Windows, or blark dependency.

All structural information is extracted from XML attributes and element names.
ST code is returned as raw strings — no ST grammar parsing is performed.

Multi-project sln support (ADR-0005): every per-symbol method accepts an
optional ``plc_name`` keyword. ``None`` resolves via ``PLC_PROJECT_NAME``
env var, then auto-resolves on single-PLC-project solutions, then raises.
The PLC-project name is the ``.plcproj`` filename stem (matches the TIPC
child name in standard TwinCAT solutions).

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
    PLCSection,
    POUDeclaration,
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
        # Per-PLC-project file indices. Outer key is the PLC-project name
        # (.plcproj filename stem), inner key is the symbol name (POU /
        # GVL / DUT). Populated by get_structure(); reused by all
        # subsequent calls.
        self._file_index: dict[str, dict[str, Path]] = {}
        # Discovered .plcproj files keyed by PLC-project name, plus their
        # mtimes for staleness checks. TwinCAT rewrites a .plcproj on any
        # structural change to its PLC project (add/remove/rename of POUs),
        # so per-.plcproj mtime is a cheap, semantically meaningful
        # invalidation trigger. ADR-0004 introduced the mtime guard for
        # single-project solutions; ADR-0005 extends it to per-.plcproj.
        self._plcproj_by_name: dict[str, Path] = {}
        self._plcproj_mtimes: dict[str, float] = {}
        self._index_project_path: str | None = None

    # ------------------------------------------------------------------
    # ProjectReader interface
    # ------------------------------------------------------------------

    def get_structure(
        self, project_path: str, *, plc_name: str | None = None
    ) -> ProjectStructure:
        """Scan project_path for TwinCAT project files and build the structure.

        Walks every ``.plcproj`` under the project root and indexes each PLC
        project's POUs / GVLs / DUTs separately. Tasks come from .TcTTO
        (preferred) and .tsproj (fallback) and live at the solution level —
        not per PLC project — because TwinCAT tasks are sln-wide.

        :param project_path: Absolute path to the solution root.
        :param plc_name: When given, restrict the index and the returned
            ProjectStructure to a single PLC project. Otherwise scan
            every .plcproj.

        Raises:
            FileNotFoundError: If project_path does not exist or contains
                no .plcproj.
        """
        root = Path(project_path)
        if not root.exists():
            raise FileNotFoundError(f"Project path not found: {project_path}")

        sln_paths = sorted(root.rglob("*.sln"))
        solution_path = str(sln_paths[0].resolve()) if sln_paths else ""

        plcproj_paths = sorted(root.rglob("*.plcproj"))

        # Reset the index. Even on a scoped (plc_name=...) walk we rebuild
        # everything: the cost of indexing the rest of the sln is small,
        # and it keeps the index coherent for follow-up calls that switch
        # PLC project.
        self._file_index = {}
        self._plcproj_by_name = {}
        self._plcproj_mtimes = {}

        for plcproj in plcproj_paths:
            name = plcproj.stem
            self._plcproj_by_name[name] = plcproj
            try:
                self._plcproj_mtimes[name] = plcproj.stat().st_mtime
            except OSError:
                self._plcproj_mtimes[name] = 0.0

        # Build per-PLC sections.
        plcs: dict[str, PLCSection] = {}
        if self._plcproj_by_name:
            for name, plcproj in self._plcproj_by_name.items():
                if plc_name is not None and name != plc_name:
                    continue
                plcs[name] = _build_section(name, plcproj, self._file_index)

            if plc_name is not None and plc_name not in plcs:
                available = ", ".join(sorted(self._plcproj_by_name)) or "(none)"
                raise ValueError(
                    f"plc_name {plc_name!r} does not match any PLC project. "
                    f"Available: {available}."
                )
        else:
            # No .plcproj anywhere — fall back to an anonymous walk so loose
            # project trees (used by some tests and the doc generator's
            # degraded path) still produce a usable structure. The synthetic
            # PLC takes the directory basename as its name.
            anon_name = plc_name or root.name
            plcs[anon_name] = _build_section_from_root(
                anon_name, root, self._file_index
            )

        tasks = _collect_tasks(root)
        self._index_project_path = project_path

        return ProjectStructure(
            project_path=project_path,
            solution_path=solution_path,
            plcs=plcs,
            tasks=tasks,
        )

    def get_pou_interface(
        self, pou_name: str, *, plc_name: str | None = None
    ) -> POUInterface:
        """Return declarations and method/property signatures for a POU or interface.

        Raises:
            FileNotFoundError: If pou_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(pou_name, ".TcPOU", plc_name)
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

    def get_pou_declaration(
        self, pou_name: str, *, plc_name: str | None = None
    ) -> POUDeclaration:
        """Return only the FB-level declaration of a POU.

        Subset of ``get_pou_interface``: no methods, no signatures, no body.
        Cheaper to read when preparing a variable add. See ADR-0003.

        Raises:
            FileNotFoundError: If pou_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(pou_name, ".TcPOU", plc_name)
        info = parse_tcpou(path)
        return POUDeclaration(
            pou_name=pou_name,
            pou_type=POUType(info["pou_type"]),
            declaration=info["declaration"],
        )

    def get_pou_item(
        self,
        pou_name: str,
        item_name: str,
        *,
        plc_name: str | None = None,
    ) -> POUItem:
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
        path = self._resolve(pou_name, ".TcPOU", plc_name)
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

    def get_gvl(self, gvl_name: str, *, plc_name: str | None = None) -> GVL:
        """Return declaration for a Global Variable List.

        Raises:
            FileNotFoundError: If gvl_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(gvl_name, ".TcGVL", plc_name)
        info = parse_tcgvl(path)
        return GVL(name=gvl_name, path=str(path), declaration=info["declaration"])

    def get_dut(self, dut_name: str, *, plc_name: str | None = None) -> DUT:
        """Return declaration for a Data Unit Type (STRUCT, ENUM, UNION).

        Raises:
            FileNotFoundError: If dut_name is not in the file index.
            ValueError: If the file cannot be parsed.
        """
        path = self._resolve(dut_name, ".TcDUT", plc_name)
        info = parse_tcdut(path)
        return DUT(name=dut_name, path=str(path), declaration=info["declaration"])

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _refresh_index_if_stale(self) -> None:
        """Rebuild the index if any tracked .plcproj has changed on disk.

        TwinCAT rewrites a .plcproj on any structural change (POU added,
        removed, renamed) within that PLC project. Body-only edits do not
        touch it, so the warm path stays warm. One stat() per tracked
        .plcproj per read.
        """
        if not self._plcproj_by_name or self._index_project_path is None:
            return
        for name, plcproj in self._plcproj_by_name.items():
            try:
                current = plcproj.stat().st_mtime
            except OSError:
                current = None
            if current != self._plcproj_mtimes.get(name):
                # Any single PLC project being stale forces a full rebuild.
                # Finer granularity would be more code for little gain at
                # this scale — see ADR-0005.
                self.get_structure(self._index_project_path)
                return

    def _resolve(
        self, name: str, extension: str, plc_name: str | None
    ) -> Path:
        """Return the file path for a named POU, GVL, or DUT.

        Resolution rule (ADR-0005):
          1. If ``plc_name`` is given, look up only in that PLC project.
          2. Otherwise, use ``PLC_PROJECT_NAME`` env var as the default.
          3. Otherwise, if the symbol is unique across all PLC projects,
             return it.
          4. Otherwise, raise listing the PLC projects that contain it.

        Falls back to scanning ``PLC_PROJECT_PATH`` if the index is empty
        (e.g. ``get_structure`` was never called this session).

        Raises:
            FileNotFoundError: If the file cannot be located.
            ValueError: If the symbol is ambiguous across PLC projects.
        """
        self._refresh_index_if_stale()

        # Lazy index hydration from PLC_PROJECT_PATH when get_structure
        # was never called this session.
        if not self._file_index:
            env_path = os.getenv("PLC_PROJECT_PATH")
            if env_path:
                self.get_structure(env_path)

        if not self._file_index:
            searched = os.getenv("PLC_PROJECT_PATH", "(no PLC_PROJECT_PATH set)")
            raise FileNotFoundError(
                f"No {extension} file found for {name!r}. "
                f"Call get_structure() first, or set PLC_PROJECT_PATH. "
                f"Searched: {searched}"
            )

        # Caller asked for a specific PLC project.
        if plc_name is not None:
            if plc_name not in self._file_index:
                available = ", ".join(sorted(self._file_index)) or "(none)"
                raise ValueError(
                    f"plc_name {plc_name!r} does not match any PLC project. "
                    f"Available: {available}."
                )
            section = self._file_index[plc_name]
            if name in section:
                return section[name]
            raise FileNotFoundError(
                f"No {extension} file found for {name!r} in PLC project "
                f"{plc_name!r}."
            )

        # No explicit plc_name. Try env default.
        env_default = os.getenv("PLC_PROJECT_NAME", "").strip()
        if env_default and env_default in self._file_index:
            section = self._file_index[env_default]
            if name in section:
                return section[name]
            raise FileNotFoundError(
                f"No {extension} file found for {name!r} in PLC project "
                f"{env_default!r} (PLC_PROJECT_NAME env default)."
            )

        # Unique-symbol fallback / ambiguous error.
        owning = [
            plc for plc, section in self._file_index.items() if name in section
        ]
        if len(owning) == 1:
            return self._file_index[owning[0]][name]
        if len(owning) > 1:
            raise ValueError(
                f"Symbol {name!r} exists in multiple PLC projects "
                f"({', '.join(sorted(owning))}). Pass plc_name to disambiguate."
            )

        # Symbol not present anywhere known to the index.
        raise FileNotFoundError(
            f"No {extension} file found for {name!r} in any indexed PLC "
            f"project. Indexed: {', '.join(sorted(self._file_index)) or '(none)'}."
        )


# ----------------------------------------------------------------------------
# Module-level helpers
# ----------------------------------------------------------------------------


def _build_section(
    plc_name: str,
    plcproj: Path,
    file_index: dict[str, dict[str, Path]],
) -> PLCSection:
    """Walk a single .plcproj's sibling tree and emit a PLCSection.

    Also writes into ``file_index[plc_name]`` so the reader's resolver can
    find symbols by name without re-walking.
    """
    return _build_section_from_root(
        plc_name,
        plcproj.parent,
        file_index,
        plcproj_path=str(plcproj),
        libraries_from=[plcproj],
    )


def _build_section_from_root(
    plc_name: str,
    folder_root: Path,
    file_index: dict[str, dict[str, Path]],
    *,
    plcproj_path: str = "",
    libraries_from: list[Path] | None = None,
) -> PLCSection:
    """Walk a directory tree and emit a PLCSection rooted at ``folder_root``.

    The ``.plcproj``-driven path is the common case; the anonymous overload
    (``libraries_from=None``) supports bare project trees without a
    ``.plcproj``.
    """
    section_index: dict[str, Path] = {}
    file_index[plc_name] = section_index

    pous: list[POURef] = []
    for tc_pou in sorted(folder_root.rglob("*.TcPOU")):
        try:
            info = parse_tcpou(tc_pou)
        except (ValueError, FileNotFoundError):
            continue
        name = info["name"]
        section_index[name] = tc_pou
        pous.append(
            POURef(
                name=name,
                pou_type=POUType(info["pou_type"]),
                path=str(tc_pou),
                plc_name=plc_name,
                folder=_folder_for(tc_pou, folder_root),
            )
        )

    gvls: list[str] = []
    for tc_gvl in sorted(folder_root.rglob("*.TcGVL")):
        try:
            info = parse_tcgvl(tc_gvl)
        except (ValueError, FileNotFoundError):
            continue
        name = info["name"]
        section_index[name] = tc_gvl
        gvls.append(name)

    duts: list[str] = []
    for tc_dut in sorted(folder_root.rglob("*.TcDUT")):
        try:
            info = parse_tcdut(tc_dut)
        except (ValueError, FileNotFoundError):
            continue
        name = info["name"]
        section_index[name] = tc_dut
        duts.append(name)

    libraries = _collect_libraries(libraries_from) if libraries_from else []

    return PLCSection(
        name=plc_name,
        plcproj_path=plcproj_path,
        pous=pous,
        gvls=gvls,
        duts=duts,
        libraries=libraries,
    )


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
