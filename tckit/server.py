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


def get_structure(project_path: str) -> str:
    """Return the top-level map of POUs, GVLs, and tasks in a TwinCAT project.

    Always call this first when you need to orient yourself in a project.
    Returns names and types only — no code bodies.

    :param project_path: Absolute path to the .tsproj or .plcproj file.
    """
    try:
        result = _cfg.reader().get_structure(project_path)
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_pou_interface(pou_name: str) -> str:
    """Return declarations and method signatures for a POU, without method bodies.

    Call this after get_structure() when you need to understand a POU's interface.
    Never call this for every POU — only for the ones you need.

    :param pou_name: Name of the POU (e.g. FB_MotorControl).
    """
    try:
        result = _cfg.reader().get_pou_interface(pou_name)
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_pou_item(pou_name: str, item_name: str) -> str:
    """Return the body of a single method, action, or property.

    The most surgical read operation. Use this when you know exactly which
    method you need to read or modify.

    :param pou_name: Name of the containing POU.
    :param item_name: Name of the method, action, or property.
    """
    try:
        result = _cfg.reader().get_pou_item(pou_name, item_name)
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_gvl(gvl_name: str) -> str:
    """Return the declaration block of a Global Variable List.

    :param gvl_name: Name of the GVL (e.g. GVL_Parameters).
    """
    try:
        result = _cfg.reader().get_gvl(gvl_name)
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


def get_dut(dut_name: str) -> str:
    """Return the declaration block of a Data Unit Type (struct, enum, union, alias).

    :param dut_name: Name of the DUT (e.g. ST_Config, E_State).
    """
    try:
        result = _cfg.reader().get_dut(dut_name)
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# ProjectWriter tools
# ---------------------------------------------------------------------------


def open_project(solution_path: str) -> str:
    """Open a TwinCAT solution in XAE.

    Idempotent — safe to call when the solution is already open.

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


def add_pou(name: str, pou_type: str, code: str) -> str:
    """Add a new POU (function block, program, function, or interface) to the project.

    :param name: Name of the new POU (follow naming conventions: FB_, PRG_, etc.).
    :param pou_type: One of: function_block, function, program, interface.
    :param code: Full ST source text including VAR blocks.
    """
    from tckit.ports.types import POUType

    try:
        pt = POUType(pou_type)
        result = _cfg.writer().add_pou(name, pt, code)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def add_method(pou_name: str, method_name: str, code: str) -> str:
    """Add a new method to an existing POU.

    :param pou_name: Name of the containing POU.
    :param method_name: Name of the new method (PascalCase, no prefix).
    :param code: Full ST source text including declaration block.
    """
    try:
        result = _cfg.writer().add_method(pou_name, method_name, code)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def update_pou_item(pou_name: str, item_name: str, code: str) -> str:
    """Update the body of an existing method, action, or property.

    :param pou_name: Name of the containing POU.
    :param item_name: Name of the method, action, or property.
    :param code: New ST source text.
    """
    try:
        result = _cfg.writer().update_pou_item(pou_name, item_name, code)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# BuildRunner tools
# ---------------------------------------------------------------------------


def build(project_path: str) -> str:
    """Build the TwinCAT project and return structured errors.

    Always fix all errors before proceeding to deploy.
    Returns JSON with success flag and error list (file/line/message/severity).

    On a successful build, if ``doc_trigger`` is ``on_build`` (the default),
    documentation is regenerated by the configured DocGenerator adapter. Doc
    failures are surfaced as a non-fatal ``docs_warning`` field on the JSON
    response (they do not fail the build).

    :param project_path: Absolute path to the .sln or .tsproj file.
    """
    try:
        result = _cfg.builder().build(project_path)
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


def deploy(target_ams_id: str, confirmed: bool = False) -> str:
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
    """
    gate = _safety_check("deploy", target_ams_id, confirmed)
    if gate is not None:
        return gate
    try:
        result = _cfg.builder().deploy(target_ams_id)
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


# ---------------------------------------------------------------------------
# TestRunner tools
# ---------------------------------------------------------------------------


def run_tests() -> str:
    """Trigger TcUnit test execution on the target runtime."""
    try:
        result = _cfg.test_runner().run_tests()
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


def get_test_results() -> str:
    """Return parsed TcUnit test results after tests have completed.

    Call run_tests() first, then wait for tests to finish, then call this.
    Returns structured JSON: suite → test → pass/fail/message.
    """
    try:
        result = _cfg.test_runner().get_results()
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
    get_pou_item,
    get_gvl,
    get_dut,
    open_project,
    create_project,
    add_pou,
    add_method,
    update_pou_item,
    build,
    deploy,
    start_runtime,
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
