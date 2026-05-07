"""html_generator — DocGenerator adapter producing HTML from TwinCAT source.

Replaces sphinx_generator. No Sphinx, no plcdoc, no subprocess.

Pipeline:
  1. _doc_model.build_project_doc()  — parse project + extract comments
  2. Jinja2 templates                — render index.html + one page per object
  3. Write HTML + search-index.json + hierarchy.html to output_path/
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from jinja2 import Environment, PackageLoader
from markupsafe import Markup

from tckit.adapters.doc_generators._doc_model import (
    ObjectDoc,
    ProjectDoc,
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
    """Build a lunr-compatible search index from the project objects."""
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

    Output is a self-contained set of HTML files — no Sphinx or plcdoc required.
    Features: cross-references, client-side search, hierarchy page, dark/light
    toggle, "used by" back-references, and a "Built with TcKit" footer.
    """

    def __init__(self) -> None:
        self._status = DocStatus.IDLE

    def generate(self, project_path: str, output_path: str) -> Result:
        """Generate HTML documentation for a TwinCAT PLC project.

        Args:
            project_path: Path to the TwinCAT PLC project directory.
            output_path: Directory where HTML files will be written.

        Returns:
            Result with success=True and details["index"] pointing to index.html,
            or success=False with an error message.
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
            # Pre-compute cross-reference data
            known_names: set[str] = {obj.name for obj in project_doc.objects}
            hierarchy = _build_hierarchy(project_doc.objects)
            search_index = _build_search_index(project_doc.objects)

            env = Environment(
                loader=PackageLoader("tckit.adapters.doc_generators"),
                autoescape=True,
            )
            env.filters["link_type"] = _make_link_type(known_names)

            ctx_base = {
                "project": project_doc,
                "known_names": known_names,
            }

            # index.html
            index_tpl = env.get_template("index.html")
            (output / "index.html").write_text(
                index_tpl.render(**ctx_base),
                encoding="utf-8",
            )

            # one page per object
            obj_tpl = env.get_template("object.html")
            for obj in project_doc.objects:
                page = output / f"{obj.name}.html"
                page.write_text(
                    obj_tpl.render(obj=obj, **ctx_base),
                    encoding="utf-8",
                )

            # hierarchy.html
            hier_tpl = env.get_template("hierarchy.html")
            (output / "hierarchy.html").write_text(
                hier_tpl.render(hierarchy=hierarchy, **ctx_base),
                encoding="utf-8",
            )

            # search-index.json
            (output / "search-index.json").write_text(
                json.dumps(search_index, ensure_ascii=False),
                encoding="utf-8",
            )

        except Exception as exc:
            self._status = DocStatus.ERROR
            return Result(success=False, error=f"Failed to render templates: {exc}")

        self._status = DocStatus.COMPLETE
        return Result(
            success=True,
            details={
                "index": str(output / "index.html"),
                "output_path": str(output),
                "objects": len(project_doc.objects),
            },
        )

    def get_status(self) -> DocStatus:
        """Return the current generation status."""
        return self._status
