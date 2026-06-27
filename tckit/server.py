"""MCP server entry point — exposes TcKit ports as MCP tools."""

from __future__ import annotations

import argparse
import json
import os
from dataclasses import asdict
from typing import Any

from mcp.server.fastmcp import FastMCP

from tckit.config import load_config

_cfg = load_config()


# ---------------------------------------------------------------------------
# Safety gate for destructive / irreversible operations
#
# Controlled by two env vars in docker/.env:
#   SAFETY_CONFIRMATIONS=true   (default) — require confirmed=True on deployment tools
#   SAFETY_CONFIRMATIONS=false  — disable gates entirely (trusted closed network)
#   BLOCKED_NETIDS=a.b.c.d.e.f,... — permanent blacklist, bypassed by nothing
# ---------------------------------------------------------------------------


def _resolve_target_ams_id(arg: str) -> str:
    """Return ``arg`` if set, else fall back to TARGET_AMS_ID config / env.

    Empty string means unresolved — the caller should error out with the
    "where to set it" hint rather than passing an empty target down to
    the bridge (which would surface as an unhelpful COM error).
    """
    if arg:
        return arg
    return str(_cfg.get("TARGET_AMS_ID", "") or "")


_TARGET_AMS_ID_REQUIRED_HINT = (
    "target_ams_id is required. Pass it explicitly, set the TARGET_AMS_ID "
    "env var, or add TARGET_AMS_ID = \"...\" to ~/.tckit/config.toml."
)


def _safety_check(action: str, target_ams_id: str, confirmed: bool) -> str | None:
    """Return an error/preview string if the action should not proceed, else None.

    None means the caller may proceed normally.
    A non-None return should be returned directly from the MCP tool.

    Precedence (highest to lowest):
      1. BLOCKED_NETIDS  — always rejected, cannot be bypassed
      2. ALLOWED_NETIDS  — always permitted without confirmation (e.g. test VMs)
      3. SAFETY_CONFIRMATIONS=false — disables gate for all targets
      4. confirmed=True  — explicit per-call approval
      5. Default         — return awaiting_confirmation
    """
    def _parse_netids(env_var: str) -> list[str]:
        return [n.strip() for n in os.getenv(env_var, "").split(",") if n.strip()]

    # 1. Blacklist — always enforced, cannot be bypassed
    if target_ams_id in _parse_netids("BLOCKED_NETIDS"):
        return _err(
            f"NetId {target_ams_id!r} is in BLOCKED_NETIDS and cannot be targeted. "
            f"Remove it from BLOCKED_NETIDS in docker/.env to allow access."
        )

    # 2. Whitelist — these targets bypass the confirmation gate (e.g. test VMs)
    allowed = _parse_netids("ALLOWED_NETIDS")
    if allowed and target_ams_id in allowed:
        return None  # always permitted, no confirmation needed

    # 3. Global disable — trusted closed network, fully autonomous operation
    if os.getenv("SAFETY_CONFIRMATIONS", "true").lower() == "false":
        return None

    # 4 & 5. Confirmation gate
    if not confirmed:
        return _ok({
            "action": action,
            "target_ams_id": target_ams_id,
            "status": "awaiting_confirmation",
            "warning": (
                f"This will {action} on {target_ams_id!r}. "
                "Verify this is the correct target and not a production system."
            ),
            "instruction": (
                f"Call {action}() again with confirmed=True to proceed, "
                "or stop if you are unsure about the target."
            ),
            "override_info": (
                "To skip confirmation for known-safe targets (e.g. a test VM), "
                "add the NetId to ALLOWED_NETIDS in docker/.env. "
                "To disable all confirmations set SAFETY_CONFIRMATIONS=false. "
                "To permanently block a NetId set BLOCKED_NETIDS=<netid>."
            ),
        })

    return None


def _ok(data: Any) -> str:
    if hasattr(data, "__dataclass_fields__"):
        return json.dumps(asdict(data), indent=2)  # type: ignore[call-overload]
    return json.dumps(data, indent=2)


def _err(message: str) -> str:
    return json.dumps({"error": message})


# ---------------------------------------------------------------------------
# ProjectReader tools
# ---------------------------------------------------------------------------


def _plc(name: str) -> str | None:
    """Normalise the optional plc_name MCP arg ("" → None)."""
    return name or None


def get_structure(project_path: str, plc_name: str = "") -> str:
    """Return the project map: POUs by folder, tasks, libraries, plus GVL and DUT names.

    The single call that orients you on an unfamiliar TwinCAT project.
    On a multi-project sln, the structure groups POUs/GVLs/DUTs by PLC
    project under ``plcs`` (one entry per ``.plcproj``); tasks remain
    sln-wide. Subsystems are visible in each POURef.folder (e.g.
    "POUs/Axes"); task layout includes cycle_time_us, priority, and the
    POUs each task runs; libraries lists Beckhoff and third-party refs.
    Call once at the start of a session; do not refresh per turn.
    Returns names and metadata only, no code bodies; use
    get_pou_interface / get_pou_item for those.

    The returned ``solution_path`` is the resolved absolute path to the
    .sln; use it as ``project_path`` on the follow-up ``build()`` call.
    If ``plcs`` has more than one entry, pass ``plc_name`` on that same
    first ``build()`` call to skip the multi-PLC disambiguation step.

    :param project_path: Absolute path to the solution root directory, or
        to a ``.sln`` file inside it. Both forms are accepted; the ``.sln``
        path is treated as a shorthand for its parent directory.
    :param plc_name: Optional PLC project to restrict the walk to. Leave
        empty to scan every ``.plcproj``.
    """
    try:
        result = _cfg.reader().get_structure(project_path, plc_name=_plc(plc_name))
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_pou_interface(pou_name: str, plc_name: str = "") -> str:
    """Return declarations and method signatures for a POU, without method bodies.

    Call this after get_structure() when you need to understand a POU's interface.
    Never call this for every POU — only for the ones you need.

    :param pou_name: Name of the POU (e.g. FB_MotorControl).
    :param plc_name: PLC project to look in. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.reader().get_pou_interface(pou_name, plc_name=_plc(plc_name))
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_pou_declaration(pou_name: str, plc_name: str = "") -> str:
    """Return only the FB-level declaration block of a POU (VAR sections, no methods).

    Narrower than get_pou_interface — use when preparing a variable add or
    reading FB-level VAR sections and method signatures are noise.

    :param pou_name: Name of the POU (e.g. FB_MotorControl).
    :param plc_name: PLC project to look in. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.reader().get_pou_declaration(pou_name, plc_name=_plc(plc_name))
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_pou_item(pou_name: str, item_name: str, plc_name: str = "") -> str:
    """Return the body of a single method, action, or property.

    The most surgical read operation. Use this when you know exactly which
    method you need to read or modify.

    :param pou_name: Name of the containing POU.
    :param item_name: Name of the method, action, or property.
    :param plc_name: PLC project to look in. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.reader().get_pou_item(
            pou_name, item_name, plc_name=_plc(plc_name)
        )
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_gvl(gvl_name: str, plc_name: str = "") -> str:
    """Return the declaration block of a Global Variable List.

    :param gvl_name: Name of the GVL (e.g. GVL_Parameters).
    :param plc_name: PLC project to look in. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.reader().get_gvl(gvl_name, plc_name=_plc(plc_name))
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_dut(dut_name: str, plc_name: str = "") -> str:
    """Return the declaration block of a Data Unit Type (struct, enum, union, alias).

    :param dut_name: Name of the DUT (e.g. ST_Config, E_State).
    :param plc_name: PLC project to look in. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.reader().get_dut(dut_name, plc_name=_plc(plc_name))
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# ProjectWriter tools
# ---------------------------------------------------------------------------


def open_project(solution_path: str) -> str:
    """Open a TwinCAT solution in XAE.

    Most workflows pre-open the project before any tool call, so this
    is rarely needed mid-session. Only call it if you have a specific
    reason to switch solutions or recover from an unloaded state.
    Idempotent, safe to call when the solution is already open.

    :param solution_path: Absolute path to the .sln file.
    """
    try:
        result = _cfg.writer().open_project(solution_path)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def create_project(name: str, path: str) -> str:
    """Create a new TwinCAT PLC project from the standard template.

    :param name: Project name.
    :param path: Directory in which to create the project.
    """
    try:
        result = _cfg.writer().create_project(name, path)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_plc_project(
    sln_path: str,
    plc_name: str,
    project_type: str = "standard",
) -> str:
    """Add a second (or further) PLC project to an existing TwinCAT solution.

    Use this when authoring a multi-PLC layout, e.g. a Library + Tests split.
    Call ``create_project`` first to author the sln + first PLC project, then
    call this for each additional PLC.

    :param sln_path: Absolute path to the existing .sln file. The harness
        reopens the solution if it isn't already loaded.
    :param plc_name: Name of the new PLC sub-project. Must not collide with
        an existing PLC project name in the same sln.
    :param project_type: ``"standard"`` (default) for a regular application
        PLC project. ``"library"`` is reserved and currently rejected.
    """
    try:
        if project_type not in ("standard", "library"):
            return _err(
                f"project_type must be 'standard' or 'library', got {project_type!r}."
            )
        result = _cfg.writer().add_plc_project(
            sln_path, plc_name, project_type=project_type  # type: ignore[arg-type]
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def save_plc_as_library(
    plc_name: str,
    output_path: str,
    install: bool = True,
    repository: str = "System",
    overwrite: bool = False,
) -> str:
    """Save a PLC project as a .library file, optionally installing it.

    Multi-PLC build orchestration: call this on any PLC project whose source
    has changed before rebuilding a consumer PLC project that holds a
    compiled-library reference to it. Compiled references resolve against
    the installed copy, not the source — without a fresh save+install the
    consumer build will pick up stale code. See the ``tc-build-test-loop``
    skill for the full rule.

    :param plc_name: PLC project to save as a library.
    :param output_path: Absolute path for the generated ``.library`` artefact.
    :param install: ``True`` (default) also installs into the named
        repository in the same call.
    :param repository: Library repository name. Defaults to ``"System"``
        which is the standard TwinCAT installed-libraries repo.
    :param overwrite: When ``True``, delete an existing ``.library`` at
        ``output_path`` before saving. ``False`` (default) keeps the
        underlying COM call's "refuse to overwrite" guard.
    """
    try:
        result = _cfg.writer().save_plc_as_library(
            plc_name,
            output_path,
            install=install,
            repository=repository,
            overwrite=overwrite,
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_library_reference(
    consumer_plc_name: str,
    library_name: str,
    version: str = "*",
    distributor: str = "Tc3 Project",
) -> str:
    """Add a library reference to a consumer PLC project.

    The referenced library must already be installed in the resolved
    repository — use ``save_plc_as_library`` with ``install=True`` first
    for libraries produced from an in-sln PLC project.

    :param consumer_plc_name: PLC project receiving the reference.
    :param library_name: Library name as installed (typically matches the
        source PLC project's name).
    :param version: ``"*"`` (default) means latest available.
    :param distributor: Library distributor / company string. Defaults to
        ``"Tc3 Project"``; override to match the library's actual
        distributor metadata if it differs.
    """
    try:
        result = _cfg.writer().add_library_reference(
            consumer_plc_name,
            library_name,
            version=version,
            distributor=distributor,
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_library_placeholder(
    consumer_plc_name: str,
    placeholder_name: str,
    default_library: str,
    version: str = "*",
    distributor: str = "",
    parameters: dict[str, dict[str, str]] | None = None,
) -> str:
    """Add a library placeholder reference to a consumer PLC project.

    Produces a ``<PlaceholderReference>`` entry in the consumer's
    ``.plcproj`` (vs the ``<LibraryReference>`` produced by
    ``add_library_reference``). Use this for libraries conventionally
    referenced via a placeholder — TcUnit, Tc2_System, Tc2_Standard,
    Tc3_Module, etc.

    The placeholder's default-resolution library must already be installed
    in the system repository for the consumer to build. System placeholders
    resolve against vendor libraries shipped with TwinCAT; in-sln libraries
    produced via ``save_plc_as_library`` resolve against that install.

    :param consumer_plc_name: PLC project receiving the reference.
    :param placeholder_name: Placeholder name (typically matches
        ``default_library`` but can differ).
    :param default_library: Library the placeholder resolves to by default.
    :param version: Default library version. ``"*"`` (default) means latest.
    :param distributor: Default library distributor / company string. Empty
        default matches the documented API default; for non-system libraries
        pass explicitly (e.g. ``"www.tcunit.org"`` for TcUnit,
        ``"Beckhoff Automation GmbH"`` for Tc2/Tc3 libraries).
    :param parameters: Optional library-parameter overrides, grouped by
        the host parameter-list GVL:
        ``{list_name: {key: value, ...}, ...}``. Equivalent to the IDE's
        "Library Parameters" dialog. Both the list name and the inner
        keys are uppercased on disk; values are written verbatim, so
        TwinCAT booleans need ``"TRUE"`` / ``"FALSE"``. Example:
        ``parameters={"GVL_Param_TcUnit": {"xUnitEnablePublish": "TRUE"}}``.
    """
    try:
        result = _cfg.writer().add_library_placeholder(
            consumer_plc_name,
            placeholder_name,
            default_library,
            version=version,
            distributor=distributor,
            parameters=parameters,
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def set_placeholder_parameters(
    consumer_plc_name: str,
    placeholder_name: str,
    parameters: dict[str, dict[str, str]],
) -> str:
    """Set / update library parameter overrides on an existing placeholder.

    Narrower verb than ``add_library_placeholder`` for tuning runs: takes
    the placeholder name and the parameter mapping only;
    ``default_library`` / ``version`` / ``distributor`` are not changed.
    The placeholder must already exist (use ``add_library_placeholder``
    for the initial add). See ADR-0011.

    :param consumer_plc_name: PLC project hosting the placeholder.
    :param placeholder_name: Placeholder name to target.
    :param parameters: Nested mapping ``{list_name: {key: value, ...}, ...}``.
        Both list and key names are uppercased on disk; values written
        verbatim, so TwinCAT booleans need ``"TRUE"`` / ``"FALSE"``.
        Example: ``{"GVL_Param_TcUnit": {"xUnitEnablePublish": "TRUE"}}``.
    """
    try:
        result = _cfg.writer().set_placeholder_parameters(
            consumer_plc_name, placeholder_name, parameters
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_pou(
    name: str,
    pou_type: str,
    code: str,
    parent_folder: str = "",
    plc_name: str = "",
) -> str:
    """Add a new POU (function block, program, function, or interface) to the project.

    :param name: Name of the new POU (follow naming conventions: FB_, PRG_, etc.).
    :param pou_type: One of: function_block, function, program, interface.
        For GVLs use ``add_gvl`` instead — the bridge no longer accepts
        ``"gvl"`` as a POU type.
    :param code: Full ST source text including VAR blocks.
    :param parent_folder: Optional path under POUs, slash-separated
        (e.g. ``"Drives/Motors"``), placing the POU in that folder.
        Intermediate folders must already exist; create them with
        ``add_folder``. Empty (default) puts the POU at the POUs root.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    from tckit.ports.types import POUType

    try:
        pt = POUType(pou_type)
        result = _cfg.writer().add_pou(
            name, pt, code, parent_folder=parent_folder, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_folder(name: str, parent_path: str = "POUs", plc_name: str = "") -> str:
    """Add a folder to a PLC project's source tree.

    Folders organise POUs, GVLs, and DUTs in XAE without affecting the
    build output. Intermediate folders in ``parent_path`` must already
    exist; create deeper layouts bottom-up with repeated calls.

    :param name: Name of the new folder.
    :param parent_path: Path under the PLC project's IDE-level node,
        slash-separated. Examples: ``"POUs"``, ``"POUs/Drives"``,
        ``"DUTs"``, ``"DUTs/Motors"``. Defaults to ``"POUs"``.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().add_folder(
            name, parent_path=parent_path, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_gvl(
    name: str,
    code: str,
    parent_folder: str = "",
    plc_name: str = "",
) -> str:
    """Add a new Global Variable List (GVL) to the project.

    GVLs aren't POUs in the TwinCAT tree; this is the dedicated path for
    creating one with a ``VAR_GLOBAL`` declaration block. There's no
    implementation body — GVLs are declaration-only.

    :param name: Name of the new GVL (e.g. ``GVL_Settings``).
    :param code: Full ST source text including ``VAR_GLOBAL`` / ``END_VAR``.
    :param parent_folder: Optional path under POUs, slash-separated
        (e.g. ``"Settings"``), placing the GVL in that folder. Empty
        (default) puts the GVL at the POUs root.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().add_gvl(
            name, code, parent_folder=parent_folder, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_method(
    pou_name: str,
    method_name: str,
    code: str,
    parent_folder: str = "",
    plc_name: str = "",
) -> str:
    """Add a new method to an existing POU.

    :param pou_name: Name of the containing POU.
    :param method_name: Name of the new method (PascalCase, no prefix).
    :param code: Full ST source text including declaration block.
    :param parent_folder: Optional path under POUs identifying the
        folder the parent POU lives in (e.g. ``"Drives"``). Empty
        (default) searches the POU subtree recursively.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().add_method(
            pou_name,
            method_name,
            code,
            parent_folder=parent_folder,
            plc_name=_plc(plc_name),
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_dut(
    name: str,
    code: str,
    dut_kind: str = "struct",
    parent_folder: str = "",
    plc_name: str = "",
) -> str:
    """Add a new Data Unit Type (struct, enum, or union) to a PLC project.

    DUTs live in their own tree folder under the PLC project, separate
    from POUs and GVLs. The kind discriminator picks the TwinCAT item
    type:

      ``struct`` -> ``TYPE Foo : STRUCT ... END_STRUCT END_TYPE``
      ``enum``   -> ``TYPE Foo : ( ... ) END_TYPE``
      ``union``  -> ``TYPE Foo : UNION ... END_UNION END_TYPE``

    The TwinCAT ALIAS type (``TYPE x : LREAL; END_TYPE``) is not yet
    supported.

    :param name: Name of the new DUT (e.g. ``ST_Config``, ``E_State``).
    :param code: Full ST source text including the ``TYPE`` /
        ``END_TYPE`` wrapper.
    :param dut_kind: One of ``"struct"`` (default), ``"enum"``,
        ``"union"``.
    :param parent_folder: Optional path under DUTs, slash-separated
        (e.g. ``"Drives"``), placing the DUT in that folder. Empty
        (default) puts the DUT at the DUTs root.
    :param plc_name: PLC project to write to. Leave empty for
        single-project solutions or to use the ``PLC_PROJECT_NAME`` env
        default.
    """
    try:
        from tckit.ports.types import DUTKind

        try:
            kind = DUTKind(dut_kind.lower())
        except ValueError:
            return _err(
                f"dut_kind must be one of struct/enum/union, got {dut_kind!r}."
            )
        result = _cfg.writer().add_dut(
            name,
            code,
            dut_kind=kind,
            parent_folder=parent_folder,
            plc_name=_plc(plc_name),
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_property(
    pou_name: str,
    property_name: str,
    return_type: str,
    getter_code: str = "",
    setter_code: str = "",
    parent_folder: str = "",
    plc_name: str = "",
) -> str:
    """Add a new property to an existing POU.

    Creates the property parent (declaration ``PROPERTY <name> : <return_type>``)
    plus a Get accessor when ``getter_code`` is non-empty, a Set accessor
    when ``setter_code`` is non-empty, or both. At least one accessor must
    be supplied.

    Each accessor's code is the body of that accessor (just the ST
    statements) optionally preceded by a local ``VAR`` block. No
    ``METHOD``/``PROPERTY`` header is needed in the accessor code; the
    kind of accessor is implicit from the tree-item type.

    :param pou_name: Name of the containing POU.
    :param property_name: Name of the new property (PascalCase, no prefix).
    :param return_type: TwinCAT type the property exposes (e.g. ``LREAL``).
    :param getter_code: Body of the Get accessor. Empty string skips it.
    :param setter_code: Body of the Set accessor. Empty string skips it.
    :param parent_folder: Optional path under POUs identifying the
        folder the parent POU lives in. Empty (default) searches the
        POU subtree recursively.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().add_property(
            pou_name,
            property_name,
            return_type,
            getter_code=getter_code or None,
            setter_code=setter_code or None,
            parent_folder=parent_folder,
            plc_name=_plc(plc_name),
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_declaration(pou_name: str, code: str, plc_name: str = "") -> str:
    """Replace the POU-level declaration block (VAR sections, signature).

    Targets the ``DeclarationText`` of the POU itself. Methods, actions and
    properties hanging off the POU are left untouched, as is the cyclic
    body — use ``update_pou_implementation`` for that and
    ``update_method_body`` for a named method/action/property.

    :param pou_name: Name of the POU.
    :param code: New declaration source — typically the
        ``FUNCTION_BLOCK Foo`` / ``PROGRAM Foo`` header through the last
        ``END_VAR``.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_pou_declaration(
            pou_name, code, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_implementation(pou_name: str, code: str, plc_name: str = "") -> str:
    """Replace the POU-level implementation block (cyclic body for FBs / PRGs).

    Targets the ``ImplementationText`` of the POU itself. The declaration
    block and any methods/actions/properties are left untouched.

    :param pou_name: Name of the POU.
    :param code: New implementation source. ST statements only — no
        ``FUNCTION_BLOCK`` header, no ``VAR``/``END_VAR``.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_pou_implementation(
            pou_name, code, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_method_body(
    pou_name: str, method_name: str, code: str, plc_name: str = ""
) -> str:
    """Replace the full body of a method, action, or property.

    ``code`` is the combined declaration + implementation for the named
    item; the bridge splits at the last ``END_VAR`` (or the last method
    header line when no ``VAR`` block is present) and writes
    ``DeclarationText`` and ``ImplementationText`` separately.

    :param pou_name: Name of the containing POU.
    :param method_name: Name of the method, action, or property.
    :param code: Full ST source for the item including header and any
        ``VAR`` blocks.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_method_body(
            pou_name, method_name, code, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_declaration_patch(
    pou_name: str,
    old_string: str,
    new_string: str,
    plc_name: str = "",
) -> str:
    """Edit-style anchored replacement on the POU declaration block.

    Replaces one unique occurrence of ``old_string`` with ``new_string``
    inside the POU's ``DeclarationText``. Fails when the anchor is missing
    or non-unique; mirror of Claude Code's own Edit semantics.

    :param pou_name: Name of the POU.
    :param old_string: Text to match. Must be unique in the declaration.
    :param new_string: Replacement text.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_pou_declaration_patch(
            pou_name, old_string, new_string, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_implementation_patch(
    pou_name: str,
    old_string: str,
    new_string: str,
    plc_name: str = "",
) -> str:
    """Edit-style anchored replacement on the POU implementation block.

    Replaces one unique occurrence of ``old_string`` with ``new_string``
    inside the POU's ``ImplementationText``. Fails when the anchor is
    missing or non-unique; mirror of Claude Code's own Edit semantics.

    :param pou_name: Name of the POU.
    :param old_string: Text to match. Must be unique in the implementation.
    :param new_string: Replacement text.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_pou_implementation_patch(
            pou_name, old_string, new_string, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_method_body_patch(
    pou_name: str,
    method_name: str,
    old_string: str,
    new_string: str,
    plc_name: str = "",
) -> str:
    """Edit-style anchored replacement on a method's combined source.

    Reads the method's combined declaration + implementation, replaces one
    unique occurrence of ``old_string`` with ``new_string``, and writes
    the split result back. Fails when the anchor is missing or non-unique;
    mirror of Claude Code's own Edit semantics.

    :param pou_name: Name of the containing POU.
    :param method_name: Name of the method, action, or property.
    :param old_string: Text to match. Must be unique in the combined source.
    :param new_string: Replacement text.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_method_body_patch(
            pou_name, method_name, old_string, new_string, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_variable(
    pou_name: str,
    scope: str,
    declaration: str,
    item_name: str = "",
    plc_name: str = "",
) -> str:
    """Add one variable declaration to a named scope block.

    Targets the FB-level declaration by default; pass ``item_name`` to add a
    method's local variable instead. Creates the scope block if it does not
    already exist on the target item. Use this instead of rewriting the full
    declaration via update_pou_declaration.

    :param pou_name: Name of the containing POU.
    :param scope: One of VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR,
        VAR_PERSISTENT, VAR_TEMP, VAR CONSTANT.
    :param declaration: Single variable declaration, e.g. ``bNewParam : BOOL;``.
    :param item_name: Method name to target. Empty string (default) targets
        the FB-level declaration.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        target = item_name if item_name else None
        result = _cfg.writer().add_variable(
            pou_name, scope, declaration, target, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_pou(name: str, plc_name: str = "") -> str:
    """Delete a POU (function block, function, program, or interface).

    Searches the POUs subtree by name, so POUs nested in folders are
    handled. Refuses to delete a ``PROGRAM`` that is still referenced
    by a ``<PouCall>`` in any task (``.TcTTO``); detach the task first.
    Other POU kinds skip the task-reference scan because they cannot be
    task-bound; orphan-instance risk surfaces at build time.

    :param name: Name of the POU to delete.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().delete_pou(name, plc_name=_plc(plc_name))
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_method(pou_name: str, method_name: str, plc_name: str = "") -> str:
    """Delete a method (or action) from a POU.

    :param pou_name: Name of the containing POU.
    :param method_name: Name of the method or action to delete.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().delete_method(
            pou_name, method_name, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_property(pou_name: str, property_name: str, plc_name: str = "") -> str:
    """Delete a property from a POU, including its Get/Set accessors.

    :param pou_name: Name of the containing POU.
    :param property_name: Name of the property to delete.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().delete_property(
            pou_name, property_name, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_gvl(name: str, plc_name: str = "") -> str:
    """Delete a Global Variable List from a PLC project.

    Refuses if the named item is not a GVL (so a same-named POU or folder
    isn't deleted by mistake).

    :param name: Name of the GVL to delete.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().delete_gvl(name, plc_name=_plc(plc_name))
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_dut(name: str, plc_name: str = "") -> str:
    """Delete a DUT (struct, enum, union, alias) from a PLC project.

    Refuses if the named item is not a DUT.

    :param name: Name of the DUT to delete.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().delete_dut(name, plc_name=_plc(plc_name))
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_variable(
    pou_name: str,
    variable_name: str,
    item_name: str = "",
    plc_name: str = "",
) -> str:
    """Remove a single variable declaration from a POU or method.

    Refuses multi-name declarations (``bA, bB : BOOL;``) and variable
    lines that don't terminate with ``;`` on the same line. For those
    cases, use ``update_pou_declaration_patch``.

    :param pou_name: Name of the containing POU.
    :param variable_name: Name of the variable to remove.
    :param item_name: Method name to target. Empty string (default)
        targets the FB-level declaration.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        target = item_name if item_name else None
        result = _cfg.writer().delete_variable(
            pou_name, variable_name, target, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_folder(
    name: str,
    parent_path: str = "",
    recursive: bool = False,
    plc_name: str = "",
) -> str:
    """Delete a folder from a PLC project's source tree.

    Refuses to delete a non-empty folder unless ``recursive=True``.

    :param name: Name of the folder to delete.
    :param parent_path: Optional explicit parent path under the PLC
        project's IDE-level node, slash-separated (e.g.
        ``"POUs/Drives"``). Use this when a folder name exists in more
        than one subtree.
    :param recursive: When ``True``, allow deletion of a folder that
        still contains children.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().delete_folder(
            name,
            parent_path=parent_path,
            recursive=recursive,
            plc_name=_plc(plc_name),
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_library_reference(
    consumer_plc_name: str,
    library_name: str,
    version: str = "*",
    distributor: str = "Tc3 Project",
) -> str:
    """Remove a library reference from a consumer PLC project.

    Symmetric counterpart to ``add_library_reference``. For placeholder
    references use ``delete_placeholder`` instead.

    :param consumer_plc_name: PLC project carrying the reference.
    :param library_name: Library name as referenced.
    :param version: Library version as referenced. ``"*"`` (default)
        targets the latest / wildcard reference.
    :param distributor: Library distributor / company string. Defaults
        to ``"Tc3 Project"`` matching ``add_library_reference``.
    """
    try:
        result = _cfg.writer().delete_library_reference(
            consumer_plc_name,
            library_name,
            version=version,
            distributor=distributor,
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def delete_placeholder(
    consumer_plc_name: str,
    placeholder_name: str,
) -> str:
    """Remove a placeholder reference from a consumer PLC project.

    Symmetric counterpart to ``add_library_placeholder``. Whether the
    underlying ``RemoveReference`` call also strips an orphan
    ``<Parameters>`` block from ``.plcproj`` is bench-confirmed per
    project.

    :param consumer_plc_name: PLC project carrying the placeholder.
    :param placeholder_name: Placeholder name to remove.
    """
    try:
        result = _cfg.writer().delete_placeholder(
            consumer_plc_name, placeholder_name
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# BuildRunner tools
# ---------------------------------------------------------------------------


def build(project_path: str, plc_name: str = "") -> str:
    """Build the TwinCAT project and return structured errors.

    Always fix all errors before proceeding to deploy.
    Returns JSON with success flag and error list (file/line/message/severity).

    On a successful build, if ``doc_trigger`` is ``on_build`` (the default),
    documentation is regenerated by the configured DocGenerator adapter. Doc
    failures are surfaced as a non-fatal ``docs_warning`` field on the JSON
    response (they do not fail the build).

    :param project_path: Absolute path to the .sln or .tsproj file.
    :param plc_name: PLC project to build. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.builder().build(project_path, plc_name=_plc(plc_name))
        payload = asdict(result)
        if result.success and _cfg.get("doc_trigger", "on_build") == "on_build":
            try:
                _cfg.doc_generator().generate(
                    project_path,
                    _cfg.get("docs_output_path", "./docs/plc"),
                )
            except Exception as doc_exc:  # noqa: BLE001 — doc failures must not fail the build
                payload["docs_warning"] = str(doc_exc)
        return _ok(payload)
    except Exception as exc:
        return _err(str(exc))


def deploy(
    target_ams_id: str = "",
    confirmed: bool = False,
    plc_name: str = "",
    boot_autostart: bool = True,
) -> str:
    """Deploy the built configuration to a target runtime.

    ⚠️  This operation writes to a live PLC. By default it requires
    ``confirmed=True`` to prevent accidental deployment to the wrong target.

    Never call this without a preceding successful build().

    Safety behaviour (configurable in docker/.env):
      - ``SAFETY_CONFIRMATIONS=true``  (default) — confirmed=True required
      - ``SAFETY_CONFIRMATIONS=false`` — no confirmation gate (trusted closed network)
      - ``BLOCKED_NETIDS=<id>,...``    — these targets are permanently rejected

    :param target_ams_id: AMS Net ID of the target (e.g. 192.168.1.100.1.1).
        If empty, falls back to the ``TARGET_AMS_ID`` env var or the
        same field in ``~/.tckit/config.toml``.
    :param confirmed: Set to True after verifying the target is correct and not production.
    :param plc_name: PLC project to deploy. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    :param boot_autostart: When True (default), enable BootProjectAutostart
        and regenerate the boot project so the PLC actually runs once the
        runtime reaches Run mode. Set False if the consumer wants to control
        autostart explicitly (e.g. loaded-but-stopped for manual login).
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    gate = _safety_check("deploy", target_ams_id, confirmed)
    if gate is not None:
        return gate
    try:
        result = _cfg.builder().deploy(
            target_ams_id,
            plc_name=_plc(plc_name),
            boot_autostart=boot_autostart,
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def start_runtime(target_ams_id: str = "", confirmed: bool = False) -> str:
    """Start or restart the TwinCAT runtime on a target.

    ⚠️  This operation restarts a live PLC runtime. By default it requires
    ``confirmed=True`` to prevent accidental restart of the wrong target.

    Safety behaviour (configurable in docker/.env):
      - ``SAFETY_CONFIRMATIONS=true``  (default) — confirmed=True required
      - ``SAFETY_CONFIRMATIONS=false`` — no confirmation gate (trusted closed network)
      - ``BLOCKED_NETIDS=<id>,...``    — these targets are permanently rejected

    :param target_ams_id: AMS Net ID of the target. If empty, falls back to
        the ``TARGET_AMS_ID`` env var or the same field in
        ``~/.tckit/config.toml``.
    :param confirmed: Set to True after verifying the target is correct and not production.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    gate = _safety_check("start_runtime", target_ams_id, confirmed)
    if gate is not None:
        return gate
    try:
        result = _cfg.builder().start_runtime(target_ams_id)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def read_symbols(target_ams_id: str, paths: list[str]) -> str:
    """Read PLC symbols by instance path on a running runtime.

    Read-only ADS query: useful for inspecting the live state of a few
    specific symbols without spinning up TcUnit. The target must be in
    Run mode (deploy + start_runtime first).

    :param target_ams_id: AMS Net ID of the target.
    :param paths: Symbol instance paths (e.g.
        ``["MAIN.suite.Tests[1].TestIsFailed"]``). Empty list returns
        an empty mapping.
    :returns: JSON envelope; ``values`` carries path -> string, with
        ``None`` for any path that couldn't be resolved (not an error).
    """
    try:
        values = _cfg.builder().read_symbols(target_ams_id, list(paths or []))
        return _ok({"success": True, "values": values})
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# HardwareInspector tools
# ---------------------------------------------------------------------------


def list_ethercat_masters(target_ams_id: str = "") -> str:
    """List every EtherCAT master found on a running TwinCAT system.

    Probes AMS port 65535 (0xFFFF) on the target. Most TwinCAT 3 systems
    have exactly one master; the list will have one entry in that case.

    The target must be reachable via ADS (TwinCAT runtime running and an
    AMS route configured). No XAE session is needed.

    :param target_ams_id: AMS Net ID of the target system
        (e.g. ``192.168.1.100.1.1``). If empty, falls back to the
        ``TARGET_AMS_ID`` env var or ``~/.tckit/config.toml``.
    :returns: JSON envelope; ``masters`` is a list of
        ``{net_id, name, port}`` objects, empty when no master is found.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    try:
        masters = _cfg.hardware_inspector().list_ethercat_masters(target_ams_id)
        return _ok({"success": True, "masters": [asdict(m) for m in masters]})
    except Exception as exc:
        return _err(str(exc))


def list_axes(target_ams_id: str = "") -> str:
    """List all configured NC axes and their live state.

    Reads axis IDs from the NC Ring0 manager (AMS port 500) then returns
    name, error code, position, velocity, and lag error for every axis.
    No XAE required; TwinCAT runtime must be in Run mode.

    ``state_name`` is one of:
      - ``"Standstill"`` — axis idle, no error
      - ``"Moving"``     — axis currently in motion
      - ``"Error"``      — non-zero error code

    :param target_ams_id: AMS Net ID of the target system.
        If empty, falls back to ``TARGET_AMS_ID`` env / config.
    :returns: JSON envelope; ``axes`` is a list of axis state objects.
        Empty list when no NC axes are configured.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    try:
        axes = _cfg.hardware_inspector().list_axes(target_ams_id)
        return _ok({"success": True, "axes": [asdict(a) for a in axes]})
    except Exception as exc:
        return _err(str(exc))


def get_axis_state(target_ams_id: str = "", axis_id: int = 0) -> str:
    """Read the live state of a single NC axis.

    Returns the same fields as :func:`list_axes` but for one axis only.
    Use this for a quick focused drill-down on a specific axis after
    identifying it via :func:`list_axes`.

    :param target_ams_id: AMS Net ID of the target system.
    :param axis_id: Axis ID as returned by :func:`list_axes`.
    :returns: JSON envelope; ``axes`` contains exactly one entry.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    if not axis_id:
        return _err("axis_id is required.")
    try:
        state = _cfg.hardware_inspector().get_axis_state(target_ams_id, axis_id)
        return _ok({"success": True, "axes": [asdict(state)]})
    except Exception as exc:
        return _err(str(exc))


def get_ipc_hardware(target_ams_id: str = "") -> str:
    """Read IPC hardware diagnostics from a running TwinCAT system.

    Reads all MDP modules discovered on the target IPC via AMS port 10000
    (SystemService).  No XAE required; TwinCAT runtime must be running.

    Returns a snapshot of:
      - ``twincat_version``  — e.g. ``"3.1.4026"``
      - ``cpu``              — ``temperature_c`` (null if BIOS API absent),
                               ``usage_pct``, ``frequency_mhz``
      - ``memory``           — ``total_mb``, ``free_mb``, ``used_mb``
      - ``fans``             — list of ``{rpm}`` entries, one per fan
      - ``nics``             — list of ``{mac, ipv4}`` entries
      - ``ups``              — ``battery_pct``, ``power_ok``, ``battery_ok``,
                               ``power_fail_count``; null if no UPS found

    Modules not present on the hardware are null or empty lists.

    :param target_ams_id: AMS Net ID of the target system.
        If empty, falls back to ``TARGET_AMS_ID`` env / config.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    try:
        hw = _cfg.hardware_inspector().get_ipc_hardware(target_ams_id)
        return _ok(asdict(hw))
    except Exception as exc:
        return _err(str(exc))


def get_ethercat_status(
    target_ams_id: str = "",
    master_net_id: str = "",
) -> str:
    """Read the full EtherCAT status snapshot for a master.

    Returns master-level diagnostic flags and the complete slave table with
    state-machine states, identity (vendor/product/revision/serial), link
    health, and per-port CRC error counters.

    Use this to answer "which slave is faulted and why" without needing
    to open TwinCAT XAE.  The target must be in Run or Config mode.

    Master state flags (``master.link_error``, ``master.watchdog_triggered``,
    ``master.dc_out_of_sync``) indicate bus-level faults.  Per-slave
    ``state`` values:
      - ``"OP"``         — nominal
      - ``"SAFEOP"``     — safe outputs only (common after a fault)
      - ``"PREOP"``      — not yet operational
      - ``"INIT"``       — just powered on
      - ``"SAFEOP+ERROR"`` / ``"OP+ERROR"`` — state with error flag set

    Non-zero ``crc_errors_a/b/c/d`` on a slave indicate cabling or EMC
    issues on the corresponding EtherCAT port.

    :param target_ams_id: AMS Net ID of the target system.
        If empty, falls back to ``TARGET_AMS_ID`` env / config.
    :param master_net_id: AMS Net ID of the EtherCAT master.
        Defaults to ``target_ams_id`` (the common single-master layout).
    :returns: JSON envelope; ``master`` carries state flags, ``slaves``
        carries the per-slave table.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    try:
        status = _cfg.hardware_inspector().get_ethercat_status(
            target_ams_id,
            master_net_id=master_net_id,
        )
        return _ok(asdict(status))
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# TestRunner tools
# ---------------------------------------------------------------------------


def run_tests(
    target_ams_id: str = "",
    plc_name: str = "",
    wait_for_results: bool = True,
) -> str:
    """Trigger TcUnit test execution on the target runtime.

    Blocks until the suites finish (or the bridge timeout fires). By
    default the response carries parsed pass/fail inline:
    ``details.summary`` (totals across the full run) and
    ``details.failures`` (one entry per failed test with ``suite_name``,
    ``test_name``, ``message``). Passing tests are NOT inlined to keep
    the payload bounded on large green suites; call get_test_results to
    fetch the full per-test list including passes.

    :param target_ams_id: AMS Net ID of the target runtime (e.g.
        ``192.168.1.100.1.1``). If empty, falls back to the
        ``TARGET_AMS_ID`` env var or the same field in
        ``~/.tckit/config.toml``.
    :param plc_name: PLC project hosting the TcUnit suites. Leave empty
        for single-project solutions or to use the ``PLC_PROJECT_NAME``
        env default.
    :param wait_for_results: When True (default), the bridge parses the
        TcUnit XML and inlines summary + failures. Set False only when
        you need to issue your own get_test_results call (e.g. an
        external orchestrator hand-rolling polling). See ADR-0011.
    """
    target_ams_id = _resolve_target_ams_id(target_ams_id)
    if not target_ams_id:
        return _err(_TARGET_AMS_ID_REQUIRED_HINT)
    try:
        result = _cfg.test_runner().run_tests(
            target_ams_id,
            plc_name=_plc(plc_name),
            wait_for_results=wait_for_results,
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def get_test_results(target_ams_id: str, plc_name: str = "") -> str:
    """Return the full per-test TcUnit results (passes + fails).

    Use this when run_tests was called with wait_for_results=False, or
    when you need the green tests too (run_tests inlines failures only
    to keep payload bounded). Returns structured JSON: suite -> test ->
    pass/fail/message.

    :param target_ams_id: AMS Net ID of the target runtime the tests were
        executed on.
    :param plc_name: PLC project hosting the TcUnit suites. Leave empty
        for single-project solutions or to use the ``PLC_PROJECT_NAME``
        env default.
    """
    try:
        result = _cfg.test_runner().get_results(
            target_ams_id, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# DocsSearcher tools
# ---------------------------------------------------------------------------


def find_fb(fb_name: str) -> str:
    """Search and fetch Beckhoff infosys documentation for a Function Block.

    Always call this before writing code that uses an unfamiliar Beckhoff FB.
    Returns inputs, outputs, timing notes, and description.

    :param fb_name: Name of the FB (e.g. FB_EcCoESdoRead).
    """
    try:
        result = _cfg.docs_searcher().find_fb(fb_name)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def search_docs(query: str, section: str = "") -> str:
    """Search Beckhoff infosys documentation.

    :param query: Search term.
    :param section: Optional infosys section to scope the search.
    """
    try:
        result = _cfg.docs_searcher().search(query, section or None)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def get_doc_page(url: str) -> str:
    """Fetch and parse a specific Beckhoff infosys page.

    Pages are cached locally. Prefer find_fb() for looking up specific FBs.

    :param url: Full infosys URL.
    """
    try:
        result = _cfg.docs_searcher().get_page(url)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# DocGenerator tools
# ---------------------------------------------------------------------------


def generate_docs(project_path: str, output_path: str) -> str:
    """Generate documentation from comments embedded in TwinCAT ST source.

    Selects between adapters via the ``doc_generator`` config key:
      ``html``     — self-contained HTML site (default)
      ``markdown`` — GitHub Flavoured Markdown files

    Auto-detects RST line, RST block, and Beckhoff XML ``<docu>`` comments.
    Output is written to ``output_path``; ``index.html`` or ``index.md`` is
    the entry point.

    :param project_path: Absolute path to the TwinCAT PLC project directory.
    :param output_path: Directory where the generated docs should be written.
    """
    try:
        result = _cfg.doc_generator().generate(project_path, output_path)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def get_doc_status() -> str:
    """Return the current documentation generation status.

    Returns one of: idle, generating, complete, error.
    """
    try:
        status = _cfg.doc_generator().get_status()
        return _ok({"status": status.value})
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# Tool registration
# ---------------------------------------------------------------------------

_TOOLS = (
    get_structure,
    get_pou_interface,
    get_pou_declaration,
    get_pou_item,
    get_gvl,
    get_dut,
    open_project,
    create_project,
    add_plc_project,
    save_plc_as_library,
    add_library_reference,
    add_library_placeholder,
    set_placeholder_parameters,
    add_pou,
    add_gvl,
    add_dut,
    add_method,
    add_property,
    add_folder,
    update_pou_declaration,
    update_pou_implementation,
    update_method_body,
    update_pou_declaration_patch,
    update_pou_implementation_patch,
    update_method_body_patch,
    add_variable,
    delete_pou,
    delete_method,
    delete_property,
    delete_gvl,
    delete_dut,
    delete_variable,
    delete_folder,
    delete_library_reference,
    delete_placeholder,
    build,
    deploy,
    start_runtime,
    read_symbols,
    list_ethercat_masters,
    get_ethercat_status,
    get_ipc_hardware,
    list_axes,
    get_axis_state,
    run_tests,
    get_test_results,
    find_fb,
    search_docs,
    get_doc_page,
    generate_docs,
    get_doc_status,
)


def _register_tools(mcp: FastMCP) -> None:
    """Register every tool function on the given FastMCP instance."""
    for fn in _TOOLS:
        mcp.tool()(fn)


def _build_mcp(transport: str) -> FastMCP:
    """Construct a FastMCP instance suitable for the chosen transport.

    stdio transport ignores host/port; SSE/HTTP need them.
    """
    if transport == "stdio":
        return FastMCP("tckit")
    return FastMCP("tckit", host="0.0.0.0", port=8000)


def _build_parser() -> argparse.ArgumentParser:
    """Build the CLI argument parser. Extracted for testability."""
    parser = argparse.ArgumentParser(
        prog="tckit",
        description="TcKit MCP server: connects Claude Code to TwinCAT 3 PLC projects.",
    )
    parser.add_argument(
        "--transport",
        choices=["stdio", "sse"],
        default=os.getenv("TCKIT_TRANSPORT", "stdio"),
        help=(
            "MCP transport to use. Default: stdio (also via TCKIT_TRANSPORT env var). "
            "Use sse for the Docker / long-running server path."
        ),
    )
    return parser


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> None:
    args = _build_parser().parse_args()
    # Best-effort: bring the local bridge up if it's down, so the operator
    # doesn't have to launch it by hand (#121). No-op off Windows or against
    # a remote BRIDGE_URL, and must never block startup or write to stdout
    # (which would corrupt the stdio MCP stream).
    try:
        from tckit.utils.bridge_spawn import ensure_bridge_running

        ensure_bridge_running()
    except Exception:  # noqa: BLE001 — auto-spawn is best-effort
        pass
    mcp = _build_mcp(args.transport)
    _register_tools(mcp)
    mcp.run(transport=args.transport)


if __name__ == "__main__":
    main()
