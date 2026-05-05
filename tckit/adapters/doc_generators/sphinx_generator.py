"""sphinx_generator — DocGenerator adapter using plcdoc + Sphinx.

Runs in Docker. Generates HTML documentation from RST-commented ST source.
Triggered on_build (after successful build) or on_demand.

Pipeline:
  1. plcdoc  — extracts RST docstrings from .TcPOU files, generates .rst files
  2. sphinx-build — compiles .rst files into HTML

Both tools are called as subprocesses so they use the system Python environment.
"""

from __future__ import annotations

import subprocess

from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result


class SphinxGenerator(DocGenerator):
    """Generates Sphinx documentation from RST-commented TwinCAT source."""

    def __init__(self) -> None:
        self._status = DocStatus.IDLE

    def generate(self, project_path: str, output_path: str) -> Result:
        """Run plcdoc + sphinx-build to generate HTML docs.

        Args:
            project_path: Path to the TwinCAT PLC project directory.
            output_path: Directory where HTML output should be written.

        Returns:
            Result with success=True on success, or success=False with error details.
        """
        self._status = DocStatus.GENERATING

        # Step 1: plcdoc — extract RST from .TcPOU files
        plcdoc_result = self._run(
            ["plcdoc", project_path, "--output", output_path],
            step="plcdoc",
        )
        if not plcdoc_result.success:
            self._status = DocStatus.ERROR
            return plcdoc_result

        # Step 2: sphinx-build — compile RST into HTML
        sphinx_result = self._run(
            ["sphinx-build", "-b", "html", output_path, f"{output_path}/_build/html"],
            step="sphinx-build",
        )
        if not sphinx_result.success:
            self._status = DocStatus.ERROR
            return sphinx_result

        self._status = DocStatus.COMPLETE
        return Result(success=True, details={"output_path": output_path})

    def get_status(self) -> DocStatus:
        """Return the current generation status."""
        return self._status

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

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
