"""MCP server entry point — exposes TcKit ports as MCP tools."""

from __future__ import annotations

import json
from dataclasses import asdict
from typing import Any

from mcp.server.fastmcp import FastMCP

from tckit.config import load_config

mcp = FastMCP("tckit")
_cfg = load_config()


def _ok(data: Any) -> str:
    if hasattr(data, "__dataclass_fields__"):
        return json.dumps(asdict(data), indent=2)  # type: ignore[call-overload]
    return json.dumps(data, indent=2)


def _err(message: str) -> str:
    return json.dumps({"error": message})


# ---------------------------------------------------------------------------
# ProjectReader tools
# ---------------------------------------------------------------------------


@mcp.tool()
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


@mcp.tool()
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


@mcp.tool()
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


@mcp.tool()
def get_gvl(gvl_name: str) -> str:
    """Return the declaration block of a Global Variable List.

    :param gvl_name: Name of the GVL (e.g. GVL_Parameters).
    """
    try:
        result = _cfg.reader().get_gvl(gvl_name)
        return _ok(result)
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# ProjectWriter tools
# ---------------------------------------------------------------------------


@mcp.tool()
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


@mcp.tool()
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


@mcp.tool()
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


@mcp.tool()
def build(project_path: str) -> str:
    """Build the TwinCAT project and return structured errors.

    Always fix all errors before proceeding to deploy.
    Returns JSON with success flag and error list (file/line/message/severity).

    :param project_path: Absolute path to the .sln or .tsproj file.
    """
    try:
        result = _cfg.builder().build(project_path)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


@mcp.tool()
def deploy(target_ams_id: str) -> str:
    """Deploy the built configuration to a target runtime.

    Never call this without a preceding successful build().

    :param target_ams_id: AMS Net ID of the target (e.g. 192.168.1.100.1.1).
    """
    try:
        result = _cfg.builder().deploy(target_ams_id)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


@mcp.tool()
def start_runtime(target_ams_id: str) -> str:
    """Start or restart the TwinCAT runtime on a target.

    :param target_ams_id: AMS Net ID of the target.
    """
    try:
        result = _cfg.builder().start_runtime(target_ams_id)
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


# ---------------------------------------------------------------------------
# TestRunner tools
# ---------------------------------------------------------------------------


@mcp.tool()
def run_tests() -> str:
    """Trigger TcUnit test execution on the target runtime."""
    try:
        result = _cfg.test_runner().run_tests()
        return _ok(asdict(result))
    except Exception as exc:
        return _err(str(exc))


@mcp.tool()
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


@mcp.tool()
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


@mcp.tool()
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


@mcp.tool()
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
# Entry point
# ---------------------------------------------------------------------------


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
