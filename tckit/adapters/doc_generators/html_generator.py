"""html_generator — DocGenerator adapter producing HTML from TwinCAT source.

Replaces sphinx_generator. No Sphinx, no plcdoc, no subprocess.

Pipeline:
  1. _doc_model.build_project_doc()  — parse project + extract comments
  2. Jinja2 templates                — render index.html + one page per object
  3. Write HTML to output_path/
"""

from __future__ import annotations

from pathlib import Path

from jinja2 import Environment, PackageLoader

from tckit.adapters.doc_generators._doc_model import build_project_doc
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result


class HtmlGenerator(DocGenerator):
    """Generates HTML documentation from RST/XML-commented TwinCAT source.

    Supports automatic detection of comment styles:
      - ``// :Description: ...`` RST line comments
      - ``(* :Description: ... *)`` RST block comments
      - ``(*~ <docu><summary>...</summary></docu> ~*)`` Beckhoff XML comments

    Output is a self-contained set of HTML files — no Sphinx or plcdoc required.
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
            env = Environment(
                loader=PackageLoader("tckit.adapters.doc_generators"),
                autoescape=True,
            )

            # index.html
            index_tpl = env.get_template("index.html")
            (output / "index.html").write_text(
                index_tpl.render(project=project_doc),
                encoding="utf-8",
            )

            # one page per object
            obj_tpl = env.get_template("object.html")
            for obj in project_doc.objects:
                page = output / f"{obj.name}.html"
                page.write_text(
                    obj_tpl.render(obj=obj, project=project_doc),
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
