"""markdown_generator — DocGenerator adapter producing Markdown from TwinCAT source.

Same ProjectDoc model as HtmlGenerator, different renderer.
Outputs GitHub Flavoured Markdown files — useful for GitHub wikis,
Confluence, Notion, Obsidian, and any Markdown-based documentation system.

Multi-project sln support (ADR-0005): top-level ``index.md`` lists each
PLC project; per-PLC pages live under ``<plc_name>/`` sub-directories.
"""

from __future__ import annotations

from pathlib import Path

from jinja2 import Environment, PackageLoader

from tckit.adapters.doc_generators._doc_model import build_project_doc
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result


class MarkdownGenerator(DocGenerator):
    """Generates Markdown documentation from RST/XML-commented TwinCAT source.

    Output layout for a multi-project sln:
      ``index.md``                       — solution-level TOC of PLC projects
      ``<plc_name>/index.md``            — per-PLC TOC of objects
      ``<plc_name>/<object>.md``         — one page per object

    Single-project solutions produce the same layout with a single PLC
    sub-directory.
    """

    def __init__(self) -> None:
        self._status = DocStatus.IDLE

    def generate(self, project_path: str, output_path: str) -> Result:
        """Generate Markdown documentation for a TwinCAT solution.

        Args:
            project_path: Path to the TwinCAT solution directory.
            output_path: Directory where .md files will be written.

        Returns:
            Result with success=True and details["index"] pointing to the
            top-level index.md, or success=False with an error message.
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
                autoescape=False,  # Markdown is plain text, no HTML escaping
                keep_trailing_newline=True,
                trim_blocks=True,
                lstrip_blocks=True,
            )

            # Top-level solution index
            solution_tpl = env.get_template("md_solution_index.md")
            (output / "index.md").write_text(
                solution_tpl.render(project=project_doc),
                encoding="utf-8",
            )

            # Per-PLC index + object pages
            plc_index_tpl = env.get_template("md_index.md")
            obj_tpl = env.get_template("md_object.md")
            total_objects = 0
            for plc in project_doc.plcs.values():
                plc_dir = output / plc.name
                plc_dir.mkdir(parents=True, exist_ok=True)

                # The per-PLC index template was written for the old
                # single-project ProjectDoc shape. ``PLCDoc`` mirrors that
                # shape (``name`` + ``objects``) so we feed it straight
                # in as ``project`` for backward compatibility.
                (plc_dir / "index.md").write_text(
                    plc_index_tpl.render(project=plc),
                    encoding="utf-8",
                )

                for obj in plc.objects:
                    page = plc_dir / f"{obj.name}.md"
                    page.write_text(
                        obj_tpl.render(obj=obj, project=plc),
                        encoding="utf-8",
                    )
                total_objects += len(plc.objects)

        except Exception as exc:
            self._status = DocStatus.ERROR
            return Result(success=False, error=f"Failed to render templates: {exc}")

        self._status = DocStatus.COMPLETE
        return Result(
            success=True,
            details={
                "index": str(output / "index.md"),
                "output_path": str(output),
                "plcs": len(project_doc.plcs),
                "objects": total_objects,
            },
        )

    def get_status(self) -> DocStatus:
        """Return the current generation status."""
        return self._status
