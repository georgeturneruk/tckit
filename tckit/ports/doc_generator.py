"""DocGenerator port — Sphinx documentation generation from RST-commented ST source."""

from abc import ABC, abstractmethod

from tckit.ports.types import DocStatus, Result


class DocGenerator(ABC):
    """Generate Sphinx documentation from RST-commented TwinCAT source.

    Trigger modes (set in config.json):
      ``on_demand`` — explicit generate() call only.
      ``on_build``  — automatically called after a successful build (default).

    Always write RST-format comments in ST code. See CLAUDE.md for conventions.
    """

    @abstractmethod
    def generate(self, project_path: str, output_path: str) -> Result:
        """Generate documentation for a TwinCAT project.

        :param project_path: Absolute path to the .tsproj or .plcproj file.
        :param output_path: Directory to write generated HTML docs into.
        """
        ...

    @abstractmethod
    def get_status(self) -> DocStatus:
        """Return the current documentation generation status."""
        ...
