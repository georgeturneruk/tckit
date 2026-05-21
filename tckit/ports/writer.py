"""ProjectWriter port — structural writes to TwinCAT project via automation interface."""

from abc import ABC, abstractmethod
from typing import Literal

from tckit.ports.types import DUTKind, POUType, Result


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
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        """Add a new POU (FB, program, function, or interface) to the project.

        :param name: Name of the new POU.
        :param pou_type: POUType enum value.
        :param code: Full ST source text including VAR blocks.
        :param parent_folder: Optional path under the POUs subtree where
            the POU should live, slash-separated (e.g. ``"Drives/Motors"``).
            Intermediate folders must already exist; create them with
            :meth:`add_folder` first.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def add_folder(
        self,
        name: str,
        *,
        parent_path: str = "POUs",
        plc_name: str | None = None,
    ) -> Result:
        """Add a folder to a PLC project's source tree.

        Folders organise POUs, GVLs, and DUTs in XAE without affecting
        the build. Wraps ``ITcSmTreeItem.CreateChild(name, 601, ...)``;
        intermediate folders in ``parent_path`` must already exist.

        :param name: Name of the new folder.
        :param parent_path: Path under the PLC project's IDE-level node
            where the folder should live, slash-separated. Examples:
            ``"POUs"``, ``"POUs/Drives"``, ``"DUTs"``,
            ``"DUTs/Motors"``. Defaults to ``"POUs"``.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def add_dut(
        self,
        name: str,
        code: str,
        *,
        dut_kind: DUTKind = DUTKind.STRUCT,
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        """Add a new Data Unit Type (struct, enum, or union) to the project.

        DUTs live in their own tree folder under the PLC project
        (``TIPC^<plc>^<plc> Project^DUTs``), separate from POUs and GVLs.
        The kind discriminator controls which TwinCAT item type is
        created (``Struct``=606, ``Enum``=605, ``Union``=607). The
        TwinCAT ALIAS type (``TYPE x : LREAL; END_TYPE``) is not yet
        supported.

        :param name: Name of the new DUT (e.g. ``ST_Config``,
            ``E_State``).
        :param code: Full ST source text. For a struct, the
            ``TYPE x : STRUCT ... END_STRUCT END_TYPE`` block. For an
            enum, the ``TYPE x : ( ... ) END_TYPE`` block. For a union,
            the ``TYPE x : UNION ... END_UNION END_TYPE`` block.
        :param dut_kind: Discriminator. Defaults to ``STRUCT``.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def add_gvl(
        self,
        name: str,
        code: str,
        *,
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        """Add a new Global Variable List (GVL) to the project.

        GVLs hold ``VAR_GLOBAL`` declarations and are tree items in their own
        right — distinct from POUs, which is why this gets a dedicated method
        rather than being routed through :meth:`add_pou`.

        :param name: Name of the new GVL (e.g. ``GVL_Settings``).
        :param code: Full ST source text including ``VAR_GLOBAL`` /
            ``END_VAR`` blocks. GVLs only carry a declaration block; there
            is no implementation body.
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
        parent_folder: str = "",
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
    def add_property(
        self,
        pou_name: str,
        property_name: str,
        return_type: str,
        *,
        getter_code: str | None = None,
        setter_code: str | None = None,
        parent_folder: str = "",
        plc_name: str | None = None,
    ) -> Result:
        """Add a new property to an existing POU.

        Creates the property parent (with declaration
        ``PROPERTY <property_name> : <return_type>``) plus a Get
        accessor when ``getter_code`` is provided, a Set accessor
        when ``setter_code`` is provided, or both. At least one
        accessor must be supplied.

        Each accessor's code is the body of that accessor (just the
        ST statements) optionally preceded by a local ``VAR`` block.
        The bridge splits at the last ``END_VAR`` (or treats the
        whole input as implementation when no ``VAR`` block is
        present). No ``METHOD``/``PROPERTY`` header should appear in
        the accessor code — the kind of accessor is implicit from
        the tree-item type.

        :param pou_name: Name of the containing POU.
        :param property_name: Name of the new property (PascalCase, no prefix).
        :param return_type: TwinCAT type the property exposes
            (e.g. ``LREAL``, ``BOOL``, ``E_MyEnum``).
        :param getter_code: Body of the Get accessor, optionally with
            a local ``VAR`` block. ``None`` skips the Get accessor.
        :param setter_code: Body of the Set accessor, optionally with
            a local ``VAR`` block. ``None`` skips the Set accessor.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def update_pou_declaration(
        self,
        pou_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Replace the POU-level declaration block (``VAR`` sections, signature).

        Targets only ``DeclarationText`` on the POU item; the implementation
        body and any methods/actions/properties underneath are untouched.
        Use :meth:`update_pou_implementation` for the cyclic body and
        :meth:`update_method_body` for a named method/action/property.

        :param pou_name: Name of the POU.
        :param code: New declaration source — typically the
            ``FUNCTION_BLOCK Foo`` / ``PROGRAM Foo`` header through the last
            ``END_VAR``.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def update_pou_implementation(
        self,
        pou_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Replace the POU-level implementation block (cyclic body for FBs / PRGs).

        Targets only ``ImplementationText`` on the POU item; the declaration
        and any methods/actions/properties underneath are untouched.

        :param pou_name: Name of the POU.
        :param code: New implementation source. ST statements only — no
            ``FUNCTION_BLOCK`` header, no ``VAR``/``END_VAR``.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def update_method_body(
        self,
        pou_name: str,
        method_name: str,
        code: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Replace the full body of a method, action, or property.

        ``code`` is the combined declaration + implementation for the named
        item; the bridge splits at the last ``END_VAR`` (or at the last
        method header line when there is no ``VAR`` block) and writes
        ``DeclarationText`` and ``ImplementationText`` separately.

        :param pou_name: Name of the containing POU.
        :param method_name: Name of the method, action, or property.
        :param code: Full ST source text for the item, including the
            ``METHOD``/``ACTION``/``PROPERTY`` header and any ``VAR``
            blocks.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def update_pou_declaration_patch(
        self,
        pou_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Edit-style anchored replacement on the POU declaration block.

        Reads ``DeclarationText`` on the POU item, replaces exactly one
        occurrence of ``old_string`` with ``new_string``, and writes the
        result back. Fails when the anchor is missing or non-unique. Mirror
        of Claude Code's own Edit semantics; see ADR-0003.
        """
        ...

    @abstractmethod
    def update_pou_implementation_patch(
        self,
        pou_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Edit-style anchored replacement on the POU implementation block.

        Reads ``ImplementationText`` on the POU item, replaces exactly one
        occurrence of ``old_string`` with ``new_string``, and writes the
        result back. Fails when the anchor is missing or non-unique. Mirror
        of Claude Code's own Edit semantics; see ADR-0003.
        """
        ...

    @abstractmethod
    def update_method_body_patch(
        self,
        pou_name: str,
        method_name: str,
        old_string: str,
        new_string: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Edit-style anchored replacement on a method's combined source.

        Reads the method's combined declaration + implementation, replaces
        exactly one occurrence of ``old_string`` with ``new_string``, and
        writes the split result back. Fails when the anchor is missing or
        non-unique. Mirror of Claude Code's own Edit semantics; see ADR-0003.
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
    def delete_pou(
        self,
        name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Delete a POU (function block, function, program, or interface).

        Routes through ``ITcSmTreeItem::DeleteChild``. The implementation
        searches the POUs subtree by name so POUs nested in folders are
        handled. Refuses to delete a ``PROGRAM`` that is still referenced
        by a ``<PouCall>`` in any task (``.TcTTO``); detach the task first.
        Other POU kinds skip the task scan, because they cannot be
        task-bound.

        :param name: Name of the POU to delete.
        :param plc_name: PLC project to write to; ``None`` follows the
            standard resolution order.
        """
        ...

    @abstractmethod
    def delete_method(
        self,
        pou_name: str,
        method_name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Delete a method (or action) from a POU.

        The tree-item display name is the only key ``DeleteChild`` uses,
        so this also covers actions and interface methods.

        :param pou_name: Name of the containing POU.
        :param method_name: Name of the method or action to delete.
        """
        ...

    @abstractmethod
    def delete_property(
        self,
        pou_name: str,
        property_name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Delete a property from a POU.

        Defensively removes any Get/Set accessor children before
        deleting the property body, because cascade behaviour is not
        documented across all XAE versions.

        :param pou_name: Name of the containing POU.
        :param property_name: Name of the property to delete.
        """
        ...

    @abstractmethod
    def delete_gvl(
        self,
        name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Delete a GVL from a PLC project.

        Validates that the named item is a GVL (kind 615) so a same-named
        POU or folder cannot be deleted by mistake. Handles GVLs nested
        in folders by resolving the parent via ``PathName``.

        :param name: Name of the GVL to delete.
        """
        ...

    @abstractmethod
    def delete_dut(
        self,
        name: str,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Delete a DUT (struct, enum, union, alias) from a PLC project.

        Recognises alias DUTs (kind 623) even though writer-side
        creation is not yet supported, so projects that already contain
        alias DUTs can have them removed.

        :param name: Name of the DUT to delete.
        """
        ...

    @abstractmethod
    def delete_variable(
        self,
        pou_name: str,
        variable_name: str,
        item_name: str | None = None,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Remove a single variable declaration from a POU or method.

        Refuses multi-name lists (``bA, bB : BOOL;``) and variable lines
        that don't terminate on the same physical line; both are pointed
        at :meth:`update_pou_declaration_patch` for partial edits.

        :param pou_name: Name of the containing POU.
        :param variable_name: Name of the variable to remove.
        :param item_name: Method name to target instead of the FB-level
            declaration. ``None`` (default) targets the FB declaration.
        """
        ...

    @abstractmethod
    def delete_folder(
        self,
        name: str,
        *,
        parent_path: str = "",
        recursive: bool = False,
        plc_name: str | None = None,
    ) -> Result:
        """Delete a folder from a PLC project's source tree.

        Refuses to delete a non-empty folder unless ``recursive=True``.
        Validates that the named item is a folder (kind 601) so a
        same-named POU/GVL/DUT cannot be deleted by mistake.

        :param name: Name of the folder. With ``parent_path`` empty,
            this is searched by name across the PLC project subtree.
        :param parent_path: Optional explicit parent path under the
            PLC project's IDE-level node, slash-separated (e.g.
            ``"POUs/Drives"``). Disambiguates a name that exists in
            multiple subtrees.
        :param recursive: Allow deleting a folder that still contains
            children.
        """
        ...

    @abstractmethod
    def delete_library_reference(
        self,
        consumer_plc_name: str,
        library_name: str,
        *,
        version: str = "*",
        distributor: str = "Tc3 Project",
    ) -> Result:
        """Remove a library reference from a consumer PLC project.

        Wraps the 3-arg form of ``ITcPlcLibraryManager.RemoveReference``,
        the symmetric counterpart to ``add_library_reference``. For
        placeholder references use :meth:`delete_placeholder` instead.

        :param consumer_plc_name: PLC project carrying the reference.
        :param library_name: Library name as referenced.
        :param version: Library version as referenced; ``"*"`` (default)
            targets the latest / wildcard reference.
        :param distributor: Library distributor / company string.
            Defaults to ``"Tc3 Project"`` matching
            :meth:`add_library_reference`.
        """
        ...

    @abstractmethod
    def delete_placeholder(
        self,
        consumer_plc_name: str,
        placeholder_name: str,
    ) -> Result:
        """Remove a placeholder reference from a consumer PLC project.

        Wraps the 1-arg form of ``ITcPlcLibraryManager.RemoveReference``,
        which targets placeholders. Whether the call also strips an
        orphan ``<Parameters>`` block from ``.plcproj`` is undocumented;
        the bench will confirm.

        :param consumer_plc_name: PLC project carrying the placeholder.
        :param placeholder_name: Placeholder name to remove (the
            ``Include=`` attribute on the ``<PlaceholderReference>``
            element).
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
        overwrite: bool = False,
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
        :param overwrite: When ``True``, delete an existing ``.library`` at
            ``output_path`` before saving. ``False`` (default) preserves
            the underlying COM call's "refuse to overwrite" behaviour so
            an accidental clobber stays caught.
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
        parameters: dict[str, dict[str, str]] | None = None,
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
            IDE's "Library Parameters" dialog. Nested mapping grouped by
            the parameter-list GVL: ``{list_name: {key: value, ...}, ...}``.
            Both the list name and the inner keys are uppercased on disk
            (the IDE's own schema requirement); values are written
            verbatim, so TwinCAT booleans need ``"TRUE"`` / ``"FALSE"``
            (not Python ``True``/``False``). Example for the TcUnit
            publisher:
            ``parameters={"GVL_Param_TcUnit": {"xUnitEnablePublish": "TRUE"}}``.
        """
        ...

    @abstractmethod
    def set_placeholder_parameters(
        self,
        consumer_plc_name: str,
        placeholder_name: str,
        parameters: dict[str, dict[str, str]],
    ) -> Result:
        """Set / update library parameter overrides on an existing placeholder.

        Narrower verb than re-calling ``add_library_placeholder`` for tuning
        runs: takes the placeholder name and the parameter mapping only;
        ``default_library`` / ``version`` / ``distributor`` are not changed.
        The placeholder must already exist in the consumer ``.plcproj``
        (raises an error if it does not — use ``add_library_placeholder``
        for the initial add).

        Parameters use the same nested mapping shape as
        ``add_library_placeholder``: ``{list_name: {key: value, ...}, ...}``.
        Both the list name and inner keys are uppercased on disk; values
        are written verbatim. Existing entries for the same (list, key)
        are replaced; other entries are preserved. See ADR-0011.

        :param consumer_plc_name: PLC project hosting the placeholder.
        :param placeholder_name: Placeholder name to target.
        :param parameters: Nested mapping of list -> key -> value.
        """
        ...
