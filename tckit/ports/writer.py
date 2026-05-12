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

    @abstractmethod
    def update_pou_item_patch(
        self,
        pou_name: str,
        item_name: str,
        old_string: str,
        new_string: str,
    ) -> Result:
        """Replace one occurrence of ``old_string`` with ``new_string`` in a POU item.

        Edit-style anchored replacement on an existing method, action, or
        property item, or (when ``item_name`` equals ``pou_name``) the FB-level
        declaration + cyclic body. Fails when ``old_string`` is not found, or
        appears more than once: mirror of Claude Code's own Edit semantics.
        See ADR-0003.

        :param pou_name: Name of the containing POU.
        :param item_name: Name of the method, action, or property
            (or ``pou_name`` itself to target the FB-level item).
        :param old_string: Text to match. Must appear exactly once in the item.
        :param new_string: Replacement text.
        """
        ...

    @abstractmethod
    def add_variable(
        self,
        pou_name: str,
        scope: str,
        declaration: str,
        item_name: str | None = None,
    ) -> Result:
        """Add one variable declaration to a named scope block.

        Operates on the FB-level declaration by default; pass ``item_name`` to
        target a method's local-VAR block instead. Creates the scope block if
        it does not already exist on the item.

        :param pou_name: Name of the containing POU.
        :param scope: One of ``VAR_INPUT``, ``VAR_OUTPUT``, ``VAR_IN_OUT``,
            ``VAR``, ``VAR_PERSISTENT``, ``VAR_TEMP``, ``VAR CONSTANT``.
        :param declaration: Single variable declaration, e.g. ``bNewParam : BOOL;``.
        :param item_name: Method name to target instead of the FB-level item.
            ``None`` (default) targets the FB declaration.
        """
        ...
