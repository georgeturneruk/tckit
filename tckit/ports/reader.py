"""ProjectReader port — read-only access to TwinCAT project structure and code."""

from abc import ABC, abstractmethod

from tckit.ports.types import GVL, POUInterface, POUItem, ProjectStructure


class ProjectReader(ABC):
    """Read-only access to a TwinCAT project.

    Always use the layered approach — never fetch more than needed:
      1. get_structure()        → names and types only
      2. get_pou_interface()    → declarations + method signatures
      3. get_pou_item()         → single method/action/property body
    """

    @abstractmethod
    def get_structure(self, project_path: str) -> ProjectStructure:
        """Return the top-level map of POUs, GVLs, and tasks.

        :param project_path: Absolute path to the .tsproj or .plcproj file.
        :returns: ProjectStructure with names and types — no code bodies.
        """
        ...

    @abstractmethod
    def get_pou_interface(self, pou_name: str) -> POUInterface:
        """Return declarations and method signatures for a POU, without method bodies.

        :param pou_name: Name of the POU (e.g. ``FB_MotorControl``).
        :returns: POUInterface with VAR blocks and method signatures.
        """
        ...

    @abstractmethod
    def get_pou_item(self, pou_name: str, item_name: str) -> POUItem:
        """Return the body of a single method, action, or property.

        :param pou_name: Name of the containing POU.
        :param item_name: Name of the method, action, or property.
        :returns: POUItem with declaration and body text.
        """
        ...

    @abstractmethod
    def get_gvl(self, gvl_name: str) -> GVL:
        """Return the declaration block of a Global Variable List.

        :param gvl_name: Name of the GVL (e.g. ``GVL_Parameters``).
        :returns: GVL with full declaration text.
        """
        ...
