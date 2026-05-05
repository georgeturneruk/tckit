"""blark_reader — ProjectReader adapter using the blark parser.

Runs in Docker. No XAE or Windows dependency.
Parses .TcPOU and .TcGVL files using the blark Python library.
"""

from tckit.ports.reader import ProjectReader
from tckit.ports.types import GVL, POUInterface, POUItem, ProjectStructure


class BlarkReader(ProjectReader):
    """Reads TwinCAT project structure and code via blark."""

    def get_structure(self, project_path: str) -> ProjectStructure:
        raise NotImplementedError("blark_reader.get_structure() not yet implemented")

    def get_pou_interface(self, pou_name: str) -> POUInterface:
        raise NotImplementedError("blark_reader.get_pou_interface() not yet implemented")

    def get_pou_item(self, pou_name: str, item_name: str) -> POUItem:
        raise NotImplementedError("blark_reader.get_pou_item() not yet implemented")

    def get_gvl(self, gvl_name: str) -> GVL:
        raise NotImplementedError("blark_reader.get_gvl() not yet implemented")
