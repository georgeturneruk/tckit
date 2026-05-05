"""sphinx_generator — DocGenerator adapter using plcdoc + Sphinx.

Runs in Docker. Generates HTML documentation from RST-commented ST source.
Triggered on_build (after successful build) or on_demand.
"""

from tckit.ports.doc_generator import DocGenerator
from tckit.ports.types import DocStatus, Result


class SphinxGenerator(DocGenerator):
    """Generates Sphinx documentation from RST-commented TwinCAT source."""

    def generate(self, project_path: str, output_path: str) -> Result:
        raise NotImplementedError("sphinx_generator.generate() not yet implemented")

    def get_status(self) -> DocStatus:
        raise NotImplementedError("sphinx_generator.get_status() not yet implemented")
