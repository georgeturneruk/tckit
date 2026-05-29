"""ProjectReader port — read-only access to TwinCAT project structure and code."""

from abc import ABC, abstractmethod

from tckit.ports.types import (
    DUT,
    GVL,
    POUDeclaration,
    POUInterface,
    POUItem,
    ProjectStructure,
)


class ProjectReader(ABC):
    """Read-only access to a TwinCAT project.

    Always use the layered approach — never fetch more than needed:
      1. get_structure()        → names and types only
      2. get_pou_interface()    → declarations + method signatures
      3. get_pou_item()         → single method/action/property body

    ``get_pou_declaration()`` is a narrower companion to ``get_pou_interface``
    that returns only the FB-level VAR sections, no method signatures.

    Multi-project solutions: every per-symbol method accepts an optional
    ``plc_name`` keyword to disambiguate. ``None`` resolves via the
    ``PLC_PROJECT_NAME`` env var, then auto-resolves if the solution has a
    single PLC project. Ambiguous lookups raise. See ADR-0005.
    """

    @abstractmethod
    def get_structure(
        self, project_path: str, *, plc_name: str | None = None
    ) -> ProjectStructure:
        """Return the top-level map of POUs, GVLs, and tasks per PLC project.

        :param project_path: Absolute path to the solution root directory,
            or to a ``.sln`` file inside it (both forms are accepted).
        :param plc_name: When given, restrict the walk to a single PLC project;
            otherwise scan every ``.plcproj`` under ``project_path``.
        :returns: ProjectStructure keyed by PLC-project name.
        """
        ...

    @abstractmethod
    def get_pou_interface(
        self, pou_name: str, *, plc_name: str | None = None
    ) -> POUInterface:
        """Return declarations and method signatures for a POU, without method bodies.

        :param pou_name: Name of the POU (e.g. ``FB_MotorControl``).
        :param plc_name: PLC project to look in; ``None`` follows the
            standard resolution order.
        :returns: POUInterface with VAR blocks and method signatures.
        """
        ...

    @abstractmethod
    def get_pou_declaration(
        self, pou_name: str, *, plc_name: str | None = None
    ) -> POUDeclaration:
        """Return only the FB-level declaration block of a POU.

        Cheaper than ``get_pou_interface`` when preparing a variable add or
        reading FB-level VAR sections; no methods, no signatures, no body.

        :param pou_name: Name of the POU (e.g. ``FB_MotorControl``).
        :param plc_name: PLC project to look in; ``None`` follows the
            standard resolution order.
        :returns: POUDeclaration with the FB-level declaration text only.
        """
        ...

    @abstractmethod
    def get_pou_item(
        self, pou_name: str, item_name: str, *, plc_name: str | None = None
    ) -> POUItem:
        """Return the body of a single method, action, or property.

        :param pou_name: Name of the containing POU.
        :param item_name: Name of the method, action, or property.
        :param plc_name: PLC project to look in; ``None`` follows the
            standard resolution order.
        :returns: POUItem with declaration and body text.
        """
        ...

    @abstractmethod
    def get_gvl(self, gvl_name: str, *, plc_name: str | None = None) -> GVL:
        """Return the declaration block of a Global Variable List.

        :param gvl_name: Name of the GVL (e.g. ``GVL_Parameters``).
        :param plc_name: PLC project to look in; ``None`` follows the
            standard resolution order.
        :returns: GVL with full declaration text.
        """
        ...

    @abstractmethod
    def get_dut(self, dut_name: str, *, plc_name: str | None = None) -> DUT:
        """Return the declaration block of a Data Unit Type (STRUCT, ENUM, UNION).

        :param dut_name: Name of the DUT (e.g. ``ST_MotorConfig``, ``E_State``).
        :param plc_name: PLC project to look in; ``None`` follows the
            standard resolution order.
        :returns: DUT with full declaration text.
        """
        ...
