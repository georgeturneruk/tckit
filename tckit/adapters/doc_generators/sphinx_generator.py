"""sphinx_generator — DocGenerator adapter using plcdoc + Sphinx.

Runs in Docker. Generates HTML documentation from RST-commented ST source.

Pipeline:
  1. Scan project for .TcPOU / .TcGVL / .TcDUT files
  2. Build a minimal Sphinx project in a temp directory:
       conf.py  — enables plcdoc extension with explicit file paths
       index.rst — auto-directives for each discovered POU/DUT
  3. sphinx-build compiles the temp project into HTML at output_path/_build/html

plcdoc 0.0.1 is a Sphinx extension (no CLI). It is configured through conf.py
and used via RST auto-directives. Available auto-directives in 0.0.1:
    .. plc:autofunctionblock:: Name  (function blocks, programs, interfaces)
    .. plc:autofunction:: Name       (functions)
    .. plc:autostruct:: Name         (structs / DUTs)
    .. plc:autofolder:: Name         (all objects in a folder)

Note: GVLs and ENUMs have no auto-directive in plcdoc 0.0.1 and are skipped.
plc_sources must use explicit file paths — glob patterns with ** are not expanded.
"""

from __future__ import annotations

import subprocess
import tempfile
from pathlib import Path

from tckit.adapters.readers._tcpou_parser import (
    detect_pou_type,
    get_cdata,
    parse_file,
)
from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result


class SphinxGenerator(DocGenerator):
    """Generates Sphinx documentation from RST-commented TwinCAT source."""

    def __init__(self) -> None:
        self._status = DocStatus.IDLE

    def generate(self, project_path: str, output_path: str) -> Result:
        """Build HTML documentation for a TwinCAT PLC project.

        Scans project_path for .TcPOU and .TcDUT files, builds a temporary
        Sphinx project using plcdoc, and writes HTML to output_path/_build/html.

        Args:
            project_path: Path to the TwinCAT PLC project directory.
            output_path: Directory where HTML output should be written.

        Returns:
            Result with success=True on success, or success=False with error.
        """
        self._status = DocStatus.GENERATING

        project = Path(project_path).resolve()
        output = Path(output_path).resolve()
        output.mkdir(parents=True, exist_ok=True)

        # Discover all TwinCAT source files (explicit paths — plcdoc 0.0.1
        # does not expand ** globs, so we expand here with pathlib)
        tcpou_files = sorted(project.glob("**/*.TcPOU"))
        tcdut_files = sorted(project.glob("**/*.TcDUT"))
        all_sources = tcpou_files + sorted(project.glob("**/*.TcGVL")) + tcdut_files

        if not all_sources:
            self._status = DocStatus.ERROR
            return Result(
                success=False,
                error=f"No TwinCAT source files found in {project_path}",
            )

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp)

            # conf.py — explicit file paths, not glob patterns
            source_list = ",\n".join(f'    "{p}"' for p in all_sources)
            (src / "conf.py").write_text(
                f'extensions = ["sphinx.ext.autodoc", "plcdoc"]\n\n'
                f"plc_sources = [\n{source_list}\n]\n\n"
                f'project = "{project.name}"\n'
                f'html_theme = "alabaster"\n'
                f'master_doc = "index"\n'
                f'exclude_patterns = ["_build"]\n'
            )

            # index.rst — one auto-directive per discovered object
            title = project.name
            rst_lines = [title, "=" * len(title), ""]

            for path in tcpou_files:
                try:
                    root = parse_file(path)
                    container = root.find("POU") or root.find("Itf")
                    if container is None:
                        continue
                    name = container.get("Name", "")
                    if not name:
                        continue
                    declaration = get_cdata(container.find("Declaration"))
                    pou_type = detect_pou_type(declaration, container.tag)

                    if pou_type == "function":
                        rst_lines.append(f".. plc:autofunction:: {name}")
                    else:
                        # function_block / program / interface → autofunctionblock
                        rst_lines.append(f".. plc:autofunctionblock:: {name}")
                        rst_lines.append("   :members:")
                    rst_lines.append("")
                except Exception:
                    continue

            for path in tcdut_files:
                try:
                    root = parse_file(path)
                    dut_el = root.find("DUT")
                    if dut_el is None:
                        continue
                    name = dut_el.get("Name", "")
                    if not name:
                        continue
                    declaration = get_cdata(dut_el.find("Declaration"))
                    # plcdoc 0.0.1 has no autoenum — use autostruct for both
                    if "ENUM" not in declaration.upper():
                        rst_lines.append(f".. plc:autostruct:: {name}")
                        rst_lines.append("")
                except Exception:
                    continue

            (src / "index.rst").write_text("\n".join(rst_lines))

            html_out = output / "_build" / "html"
            result = self._run(
                ["sphinx-build", "-b", "html", str(src), str(html_out)],
                step="sphinx-build",
            )

        if result.success:
            self._status = DocStatus.COMPLETE
            return Result(
                success=True,
                details={
                    "output_path": str(output),
                    "index": str(output / "_build" / "html" / "index.html"),
                },
            )

        self._status = DocStatus.ERROR
        return result

    def get_status(self) -> DocStatus:
        """Return the current generation status."""
        return self._status

    def _run(self, cmd: list[str], step: str) -> Result:
        """Run a subprocess command and return a Result."""
        try:
            proc = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=300,
            )
        except FileNotFoundError:
            return Result(
                success=False,
                error=f"{step}: executable not found ({cmd[0]!r}). "
                      f"Is it installed in the Docker image?",
            )
        except subprocess.TimeoutExpired:
            return Result(success=False, error=f"{step}: timed out after 300 seconds")

        if proc.returncode != 0:
            stderr = proc.stderr.strip() or proc.stdout.strip()
            return Result(
                success=False,
                error=f"{step} exited with code {proc.returncode}",
                details={"stderr": stderr, "stdout": proc.stdout.strip()},
            )

        return Result(
            success=True,
            details={"stdout": proc.stdout.strip()},
        )
