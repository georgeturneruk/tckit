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

    :param project_path: Absolute path to the solution root.
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
    parameters: dict[str, str] | None = None,
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
    :param parameters: Optional library-parameter overrides (mapping of
        name to string value). Equivalent to the IDE's "Library
        Parameters" dialog. Values are written verbatim, so TwinCAT
        booleans need ``"TRUE"`` / ``"FALSE"``.
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


def add_pou(name: str, pou_type: str, code: str, plc_name: str = "") -> str:
    """Add a new POU (function block, program, function, or interface) to the project.

    :param name: Name of the new POU (follow naming conventions: FB_, PRG_, etc.).
    :param pou_type: One of: function_block, function, program, interface.
        For GVLs use ``add_gvl`` instead — the bridge no longer accepts
        ``"gvl"`` as a POU type.
    :param code: Full ST source text including VAR blocks.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    from tckit.ports.types import POUType

    try:
        pt = POUType(pou_type)
        result = _cfg.writer().add_pou(name, pt, code, plc_name=_plc(plc_name))
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_gvl(name: str, code: str, plc_name: str = "") -> str:
    """Add a new Global Variable List (GVL) to the project.

    GVLs aren't POUs in the TwinCAT tree; this is the dedicated path for
    creating one with a ``VAR_GLOBAL`` declaration block. There's no
    implementation body — GVLs are declaration-only.

    :param name: Name of the new GVL (e.g. ``GVL_Settings``).
    :param code: Full ST source text including ``VAR_GLOBAL`` / ``END_VAR``.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().add_gvl(name, code, plc_name=_plc(plc_name))
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_method(
    pou_name: str, method_name: str, code: str, plc_name: str = ""
) -> str:
    """Add a new method to an existing POU.

    :param pou_name: Name of the containing POU.
    :param method_name: Name of the new method (PascalCase, no prefix).
    :param code: Full ST source text including declaration block.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().add_method(
            pou_name, method_name, code, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_item(
    pou_name: str, item_name: str, code: str, plc_name: str = ""
) -> str:
    """Update the body of an existing method, action, or property.

    :param pou_name: Name of the containing POU.
    :param item_name: Name of the method, action, or property.
    :param code: New ST source text.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_pou_item(
            pou_name, item_name, code, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_item_patch(
    pou_name: str,
    item_name: str,
    old_string: str,
    new_string: str,
    plc_name: str = "",
) -> str:
    """Edit-style anchored replacement on an existing POU item.

    Replace one unique occurrence of ``old_string`` with ``new_string`` in
    the item's combined declaration + implementation. Fails when the anchor
    is missing or non-unique; mirror of Claude Code's own Edit semantics.
    Use this instead of Edit/Write on .TcPOU files. Pass ``item_name`` equal
    to ``pou_name`` to target the FB-level declaration + cyclic body.

    :param pou_name: Name of the containing POU.
    :param item_name: Method/action/property name, or ``pou_name`` for the FB item.
    :param old_string: Text to match. Must be unique in the item.
    :param new_string: Replacement text.
    :param plc_name: PLC project to write to. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    """
    try:
        result = _cfg.writer().update_pou_item_patch(
            pou_name, item_name, old_string, new_string, plc_name=_plc(plc_name)
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
    declaration via update_pou_item.

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
    target_ams_id: str,
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
    :param confirmed: Set to True after verifying the target is correct and not production.
    :param plc_name: PLC project to deploy. Leave empty for single-project
        solutions or to use the ``PLC_PROJECT_NAME`` env default.
    :param boot_autostart: When True (default), enable BootProjectAutostart
        and regenerate the boot project so the PLC actually runs once the
        runtime reaches Run mode. Set False if the consumer wants to control
        autostart explicitly (e.g. loaded-but-stopped for manual login).
    """
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


def start_runtime(target_ams_id: str, confirmed: bool = False) -> str:
    """Start or restart the TwinCAT runtime on a target.

    ⚠️  This operation restarts a live PLC runtime. By default it requires
    ``confirmed=True`` to prevent accidental restart of the wrong target.

    Safety behaviour (configurable in docker/.env):
      - ``SAFETY_CONFIRMATIONS=true``  (default) — confirmed=True required
      - ``SAFETY_CONFIRMATIONS=false`` — no confirmation gate (trusted closed network)
      - ``BLOCKED_NETIDS=<id>,...``    — these targets are permanently rejected

    :param target_ams_id: AMS Net ID of the target.
    :param confirmed: Set to True after verifying the target is correct and not production.
    """
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
# TestRunner tools
# ---------------------------------------------------------------------------


def run_tests(target_ams_id: str, plc_name: str = "") -> str:
    """Trigger TcUnit test execution on the target runtime.

    Mirrors the IDE workflow where you pick both the target route and the
    PLC project before running tests. Both arguments are explicit because
    implicit "last deployed target" state would be brittle across MCP calls.

    :param target_ams_id: AMS Net ID of the target runtime (e.g.
        ``192.168.1.100.1.1``).
    :param plc_name: PLC project hosting the TcUnit suites. Leave empty
        for single-project solutions or to use the ``PLC_PROJECT_NAME``
        env default.
    """
    try:
        result = _cfg.test_runner().run_tests(
            target_ams_id, plc_name=_plc(plc_name)
        )
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def get_test_results(target_ams_id: str, plc_name: str = "") -> str:
    """Return parsed TcUnit test results after tests have completed.

    Call run_tests() first, then wait for tests to finish, then call this.
    Returns structured JSON: suite → test → pass/fail/message.

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
    add_pou,
    add_gvl,
    add_method,
    update_pou_item,
    update_pou_item_patch,
    add_variable,
    build,
    deploy,
    start_runtime,
    read_symbols,
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
    mcp = _build_mcp(args.transport)
    _register_tools(mcp)
    mcp.run(transport=args.transport)


if __name__ == "__main__":
    main()
