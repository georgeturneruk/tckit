"""markdown_generator — DocGenerator adapter producing Markdown from TwinCAT source.

Same ProjectDoc model as HtmlGenerator, different renderer.
Outputs GitHub Flavoured Markdown files — useful for GitHub wikis,
Confluence, Notion, Obsidian, and any Markdown-based documentation system.
"""

from __future__ import annotations

from pathlib import Path

from jinja2 import Environment, PackageLoader

from tckit.adapters.doc_generators._doc_model import build_project_doc
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result


class MarkdownGenerator(DocGenerator):
    """Generates Markdown documentation from RST/XML-commented TwinCAT source.

    Produces one .md file per object plus an index.md with a full TOC.
    Uses GitHub Flavoured Markdown (pipe tables, fenced code blocks).
    """

    def __init__(self) -> None:
        self._status = DocStatus.IDLE

    def generate(self, project_path: str, output_path: str) -> Result:
        """Generate Markdown documentation for a TwinCAT PLC project.

        Args:
            project_path: Path to the TwinCAT PLC project directory.
            output_path: Directory where .md files will be written.

        Returns:
            Result with success=True and details["index"] pointing to index.md,
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
                autoescape=False,  # Markdown is plain text, no HTML escaping
                keep_trailing_newline=True,
                trim_blocks=True,
                lstrip_blocks=True,
            )

            ctx = {"project": project_doc}

            # index.md
            index_tpl = env.get_template("md_index.md")
            (output / "index.md").write_text(
                index_tpl.render(**ctx),
                encoding="utf-8",
            )

            # one page per object
            obj_tpl = env.get_template("md_object.md")
            for obj in project_doc.objects:
                page = output / f"{obj.name}.md"
                page.write_text(
                    obj_tpl.render(obj=obj, **ctx),
                    encoding="utf-8",
                )

        except Exception as exc:
            self._status = DocStatus.ERROR
            return Result(success=False, error=f"Failed to render templates: {exc}")

        self._status = DocStatus.COMPLETE
        return Result(
            success=True,
            details={
                "index": str(output / "index.md"),
                "output_path": str(output),
                "objects": len(project_doc.objects),
            },
        )

    def get_status(self) -> DocStatus:
        """Return the current generation status."""
        return self._status
