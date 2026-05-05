"""ProjectWriter port — structural writes to TwinCAT project via automation interface."""

from abc import ABC, abstractmethod

from tckit.ports.types import POUType, Result


class ProjectWriter(ABC):
    """Write ST code and structural elements back to a TwinCAT project.

    All writes go through the automation interface (via bridge), which handles
    GUID assignment, .plcproj cross-reference updates, and tree indexing.
    Never manipulate .TcPOU XML or .plcproj files directly for structural changes.
    """

    @abstractmethod
    def open_project(self, solution_path: str) -> Result:
        """Open a TwinCAT solution in XAE.

        :param solution_path: Absolute path to the .sln or .tsproj file.
        """
        ...

    @abstractmethod
    def create_project(self, name: str, path: str) -> Result:
        """Create a new TwinCAT PLC project.

        :param name: Project name.
        :param path: Directory in which to create the project.
        """
        ...

    @abstractmethod
    def add_pou(self, name: str, pou_type: POUType, code: str) -> Result:
        """Add a new POU (FB, program, function, or interface) to the project.

        :param name: Name of the new POU.
        :param pou_type: POUType enum value.
        :param code: Full ST source text including VAR blocks.
        """
        ...

    @abstractmethod
    def add_method(self, pou_name: str, method_name: str, code: str) -> Result:
        """Add a new method to an existing POU.

        :param pou_name: Name of the containing POU.
        :param method_name: Name of the new method.
        :param code: Full ST source text including declaration block.
        """
        ...

    @abstractmethod
    def update_pou_item(self, pou_name: str, item_name: str, code: str) -> Result:
        """Update the body of an existing method, action, or property.

        :param pou_name: Name of the containing POU.
        :param item_name: Name of the method, action, or property.
        :param code: New ST source text.
        """
        ...
