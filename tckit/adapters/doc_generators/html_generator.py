"""html_generator — DocGenerator adapter producing HTML from TwinCAT source.

Self-contained Jinja2-based renderer. No Sphinx, no plcdoc, no subprocess.

Pipeline:
  1. _doc_model.build_project_doc()  — parse solution + extract comments
  2. Jinja2 templates                — render top-level + per-PLC pages
  3. Write HTML + per-PLC search-index.json + hierarchy.html to output_path/

Multi-project sln support (ADR-0005): top-level ``index.html`` lists each
PLC project; per-PLC pages live under ``<plc_name>/`` sub-directories. The
existing per-PLC templates take a PLCDoc as ``project`` (it mirrors the
old ProjectDoc shape).
"""

from __future__ import annotations

import json
from pathlib import Path

from jinja2 import Environment, PackageLoader
from markupsafe import Markup

from tckit.adapters.doc_generators._doc_model import (
    ObjectDoc,
    PLCDoc,
    _base_type,
    build_project_doc,
)
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result

# ---------------------------------------------------------------------------
# Cross-reference helper
# ---------------------------------------------------------------------------

_LUNR_JS_URL = (
    "https://cdn.jsdelivr.net/npm/lunr@2.3.9/lunr.min.js"
)


def _make_link_type(known_names: set[str]) -> object:
    """Return a Jinja2 filter function that links type names to their object pages."""

    def link_type(type_str: str) -> Markup:
        """Convert a variable/return type string to a link if the type is a known object."""
        if not type_str:
            return Markup("")
        base = _base_type(type_str)
        if base in known_names:
            # Preserve the full type string, make the base name a link
            escaped = Markup.escape(type_str)
            linked = escaped.replace(
                Markup.escape(base),
                Markup(f'<a href="{base}.html">{base}</a>'),
                1,
            )
            return linked
        return Markup.escape(type_str)

    return link_type


def _build_hierarchy(objects: list[ObjectDoc]) -> dict:
    """Build inheritance/implements groupings for the hierarchy page."""
    by_base: dict[str, list[ObjectDoc]] = {}
    by_iface: dict[str, list[ObjectDoc]] = {}
    standalone: list[ObjectDoc] = []

    for obj in objects:
        if obj.extends:
            by_base.setdefault(obj.extends, []).append(obj)
        for iface in obj.implements:
            by_iface.setdefault(iface, []).append(obj)
        if not obj.extends and not obj.implements:
            standalone.append(obj)

    return {"by_base": by_base, "by_iface": by_iface, "standalone": standalone}


def _build_search_index(objects: list[ObjectDoc]) -> list[dict]:
    """Build a lunr-compatible search index from a PLC's objects."""
    index = []
    for obj in objects:
        method_names = " ".join(m.name for m in obj.methods)
        prop_names = " ".join(p.name for p in obj.properties)
        var_names = " ".join(
            v.name for v in obj.inputs + obj.outputs + obj.inout + obj.variables
        )
        index.append({
            "id": obj.name,
            "title": obj.name,
            "type": obj.obj_type,
            "description": obj.comment.description,
            "body": f"{method_names} {prop_names} {var_names}".strip(),
        })
    return index


class HtmlGenerator(DocGenerator):
    """Generates HTML documentation from RST/XML-commented TwinCAT source.

    Supports automatic detection of comment styles:
      - ``// :Description: ...`` RST line comments
      - ``(* :Description: ... *)`` RST block comments
      - ``(*~ <docu><summary>...</summary></docu> ~*)`` Beckhoff XML comments

    Output layout (ADR-0005):
      ``index.html``                       — solution-level TOC of PLC projects
      ``<plc_name>/index.html``            — per-PLC TOC of objects
      ``<plc_name>/<object>.html``         — one page per object
      ``<plc_name>/hierarchy.html``        — per-PLC type hierarchy
      ``<plc_name>/search-index.json``     — per-PLC lunr search index

    Features within each PLC sub-tree: cross-references, client-side
    search, hierarchy page, dark/light toggle, "used by" back-references
    (scoped to the PLC), and a "Built with TcKit" footer.
    """

    def __init__(self) -> None:
        self._status = DocStatus.IDLE

    def generate(self, project_path: str, output_path: str) -> Result:
        """Generate HTML documentation for a TwinCAT solution.

        Args:
            project_path: Path to the TwinCAT solution directory.
            output_path: Directory where HTML files will be written.

        Returns:
            Result with success=True and details["index"] pointing to the
            top-level index.html, or success=False with an error message.
        """
        self._status = DocStatus.GENERATING

        try:
            project_doc = build_project_doc(project_path)
        except ValueError as exc:
            self._status = DocStatus.ERROR
            return Result(success=False, error=str(exc))
        except Exception as exc:
            self._status = DocStatus.ERROR
            return Result(success=False, error=f"Failed to parse project: {exc}")

        output = Path(output_path).resolve()
        output.mkdir(parents=True, exist_ok=True)

        try:
            env = Environment(
                loader=PackageLoader("tckit.adapters.doc_generators"),
                autoescape=True,
            )

            # Top-level solution index.
            solution_tpl = env.get_template("solution_index.html")
            (output / "index.html").write_text(
                solution_tpl.render(project=project_doc),
                encoding="utf-8",
            )

            total_objects = 0
            for plc in project_doc.plcs.values():
                total_objects += _render_plc(env, plc, output / plc.name)

        except Exception as exc:
            self._status = DocStatus.ERROR
            return Result(success=False, error=f"Failed to render templates: {exc}")

        self._status = DocStatus.COMPLETE
        return Result(
            success=True,
            details={
                "index": str(output / "index.html"),
                "output_path": str(output),
                "plcs": len(project_doc.plcs),
                "objects": total_objects,
            },
        )

    def get_status(self) -> DocStatus:
        """Return the current generation status."""
        return self._status


def _render_plc(env: Environment, plc: PLCDoc, plc_dir: Path) -> int:
    """Render every page for a single PLC project into ``plc_dir``."""
    plc_dir.mkdir(parents=True, exist_ok=True)

    # Per-PLC cross-references are computed and scoped within this PLC.
    known_names: set[str] = {obj.name for obj in plc.objects}
    hierarchy = _build_hierarchy(plc.objects)
    search_index = _build_search_index(plc.objects)

    # Per-PLC templates use ``project`` as the context name. PLCDoc
    # mirrors the old ProjectDoc shape so the existing templates work
    # without modification.
    env.filters["link_type"] = _make_link_type(known_names)
    ctx_base = {
        "project": plc,
        "known_names": known_names,
    }

    index_tpl = env.get_template("index.html")
    (plc_dir / "index.html").write_text(
        index_tpl.render(**ctx_base),
        encoding="utf-8",
    )

    obj_tpl = env.get_template("object.html")
    for obj in plc.objects:
        page = plc_dir / f"{obj.name}.html"
        page.write_text(
            obj_tpl.render(obj=obj, **ctx_base),
            encoding="utf-8",
        )

    hier_tpl = env.get_template("hierarchy.html")
    (plc_dir / "hierarchy.html").write_text(
        hier_tpl.render(hierarchy=hierarchy, **ctx_base),
        encoding="utf-8",
    )

    (plc_dir / "search-index.json").write_text(
        json.dumps(search_index, ensure_ascii=False),
        encoding="utf-8",
    )

    return len(plc.objects)
