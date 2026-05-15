"""ProjectWriter port — structural writes to TwinCAT project via automation interface."""

from abc import ABC, abstractmethod
from typing import Literal

from tckit.ports.types import POUType, Result


class ProjectWriter(ABC):
    """Write ST code and structural elements back to a TwinCAT project.

    All writes go through the automation interface (via bridge), which handles
    GUID assignment, .plcproj cross-reference updates, and tree indexing.
    Never manipulate .TcPOU XML or .plcproj files directly for structural changes.

    Multi-project solutions: every PLC-scoped method accepts an optional
    ``plc_name`` keyword to disambiguate. ``None`` resolves via the
    ``PLC_PROJECT_NAME`` env var, then auto-resolves if the solution has a
    single PLC project. ``open_project`` and ``create_project`` are
    solution-scoped and take no ``plc_name``. See ADR-0005.
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
    def add_pou(
        self,
        name: str,
        pou_type: POUType,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Add a new POU (FB, program, function, or interface) to the project.

        :param name: Name of the new POU.
        :param pou_type: POUType enum value.
        :param code: Full ST source text including VAR blocks.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def add_method(
        self,
        pou_name: str,
        method_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Add a new method to an existing POU.

        :param pou_name: Name of the containing POU.
        :param method_name: Name of the new method.
        :param code: Full ST source text including declaration block.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def update_pou_item(
        self,
        pou_name: str,
        item_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Update the body of an existing method, action, or property.

        :param pou_name: Name of the containing POU.
        :param item_name: Name of the method, action, or property.
        :param code: New ST source text.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def update_pou_item_patch(
        self,
        pou_name: str,
        item_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
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
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def add_variable(
        self,
        pou_name: str,
        scope: str,
        declaration: str,
        item_name: str | None = None,
        *,
        plc_name: str | None = None,
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
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def add_plc_project(
        self,
        sln_path: str,
        plc_name: str,
        *,
        project_type: Literal["standard", "library"] = "standard",
    ) -> Result:
        """Add a second (or further) PLC project to an existing TwinCAT solution.

        Wraps the documented ``ITcSmTreeItem.CreateChild`` call on the
        ``TIPC`` node so a single sln can hold a library + application or
        library + test split. v1 implements only ``project_type="standard"``;
        ``"library"`` is reserved and returns an explicit error.

        :param sln_path: Absolute path to the existing .sln file. The
            harness reopens the solution if it isn't already loaded.
        :param plc_name: Name of the new PLC sub-project. Must not collide
            with an existing PLC project name in the same sln.
        :param project_type: ``"standard"`` (default) for a regular
            application PLC project. ``"library"`` reserved; not yet
            implemented.
        """
        ...

    @abstractmethod
    def save_plc_as_library(
        self,
        plc_name: str,
        output_path: str,
        *,
        install: bool = True,
        repository: str = "System",
    ) -> Result:
        """Save a PLC project as a .library file, optionally installing it.

        Wraps ``ITcPlcIECProject.SaveAsLibrary(path, install)``. The IDE
        equivalent is "PLC project → Save as library and install". When
        ``install=True``, the library is registered with the named
        repository in the same COM call so consumer PLC projects can
        resolve a subsequent ``add_library_reference``.

        Required before every consumer build whose source has changed,
        because compiled-library references pull from the installed copy
        rather than rebuilding the source on demand. See the
        ``tc-build-test-loop`` skill for the orchestration rule.

        :param plc_name: PLC project to save as a library.
        :param output_path: Absolute path (directory or file) for the
            generated ``.library`` artefact.
        :param install: ``True`` (default) also installs into the named
            repository in the same call.
        :param repository: Library repository name. Defaults to ``"System"``
            which is the standard TwinCAT installed-libraries repo.
        """
        ...

    @abstractmethod
    def add_library_reference(
        self,
        consumer_plc_name: str,
        library_name: str,
        *,
        version: str = "*",
        distributor: str = "Tc3 Project",
    ) -> Result:
        """Add a library reference to a consumer PLC project.

        Wraps ``ITcPlcLibraryManager.AddLibrary(name, version, distributor)``
        on the consumer PLC's library manager (the ``References`` tree node
        under the PLC project). The referenced library must already be
        installed in the resolved repository — use ``save_plc_as_library``
        with ``install=True`` first for libraries produced from an in-sln
        PLC project.

        :param consumer_plc_name: PLC project receiving the reference.
        :param library_name: Library name as installed (typically matches
            the source PLC project's name).
        :param version: ``"*"`` (default) means latest available.
        :param distributor: Library distributor / company string.
            Defaults to ``"Tc3 Project"``, the conventional value for
            libraries produced from a PLC project via ``SaveAsLibrary``;
            override if the project's company info differs.
        """
        ...

    @abstractmethod
    def add_library_placeholder(
        self,
        consumer_plc_name: str,
        placeholder_name: str,
        default_library: str,
        *,
        version: str = "*",
        distributor: str = "",
        parameters: dict[str, str] | None = None,
    ) -> Result:
        """Add a library placeholder reference to a consumer PLC project.

        Wraps ``ITcPlcLibraryManager.AddPlaceholder(placeholder_name,
        default_lib, default_version, default_distributor)`` on the consumer
        PLC's library manager. Produces a ``<PlaceholderReference>`` entry in
        the consumer's ``.plcproj`` rather than the ``<LibraryReference>``
        produced by ``add_library_reference`` — the placeholder is resolved
        at build time and can be re-pointed without editing the reference.

        Use this for libraries that are conventionally referenced via a
        placeholder (TcUnit, Tc2_System, Tc2_Standard, Tc3_Module, etc.) so
        the on-disk reference matches what the IDE writes when an operator
        adds the library through "Add Library...".

        :param consumer_plc_name: PLC project receiving the reference.
        :param placeholder_name: Placeholder name. By convention typically
            matches ``default_library`` but can differ (e.g. ``Placeholder_NC``
            -> ``Tc2_NC``).
        :param default_library: Library that the placeholder resolves to by
            default. Must already be installed in the system repository for
            the consumer to build.
        :param version: Default library version. ``"*"`` (default) means
            latest available.
        :param distributor: Default library distributor / company string.
            Empty default matches the documented API default; for non-system
            libraries (e.g. ``"www.tcunit.org"`` for TcUnit,
            ``"Beckhoff Automation GmbH"`` for Tc2/Tc3 libraries) pass the
            distributor explicitly so the placeholder resolves correctly.
        :param parameters: Optional library parameter overrides serialised
            into the ``<PlaceholderReference>`` block, equivalent to the
            IDE's "Library Parameters" dialog. Maps parameter name → value
            string; values are written verbatim, so TwinCAT booleans need
            ``"TRUE"`` / ``"FALSE"`` (not Python ``True``/``False``). Use
            this to override defaults like
            ``parameters={"xUnitEnablePublish": "TRUE"}`` on TcUnit.
        """
        ...
