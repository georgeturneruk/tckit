"""Tests for the MCP transport selection logic in tckit.server."""

from __future__ import annotations

import pytest

from tckit.server import _TOOLS, _build_mcp, _build_parser, _register_tools


# ---------------------------------------------------------------------------
# _build_parser — argument parsing and env-var fallback
# ---------------------------------------------------------------------------


def test_default_transport_is_stdio_when_no_env_var(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TCKIT_TRANSPORT", raising=False)
    args = _build_parser().parse_args([])
    assert args.transport == "stdio"


def test_default_transport_follows_env_var(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TCKIT_TRANSPORT", "sse")
    args = _build_parser().parse_args([])
    assert args.transport == "sse"


def test_cli_flag_wins_over_env_var(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TCKIT_TRANSPORT", "sse")
    args = _build_parser().parse_args(["--transport", "stdio"])
    assert args.transport == "stdio"


def test_invalid_transport_rejected(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TCKIT_TRANSPORT", raising=False)
    parser = _build_parser()
    with pytest.raises(SystemExit):
        parser.parse_args(["--transport", "smoke-signals"])


# ---------------------------------------------------------------------------
# _build_mcp — instance construction differs per transport
# ---------------------------------------------------------------------------


def test_build_mcp_stdio_constructs_without_error() -> None:
    mcp = _build_mcp("stdio")
    assert mcp is not None


def test_build_mcp_sse_binds_default_host_and_port() -> None:
    mcp = _build_mcp("sse")
    assert mcp.settings.host == "0.0.0.0"
    assert mcp.settings.port == 8000


# ---------------------------------------------------------------------------
# _register_tools — tools attach cleanly to a fresh instance
# ---------------------------------------------------------------------------


def test_register_tools_runs_without_error() -> None:
    mcp = _build_mcp("stdio")
    _register_tools(mcp)


def test_tool_tuple_contains_all_known_tools() -> None:
    """Spot-check that the tuple of tools wires up the expected names.

    If a tool is removed or renamed, this catches the drift.
    """
    names = {fn.__name__ for fn in _TOOLS}
    expected = {
        "get_structure",
        "get_pou_interface",
        "get_pou_declaration",
        "get_pou_item",
        "get_gvl",
        "get_dut",
        "open_project",
        "create_project",
        "add_plc_project",
        "save_plc_as_library",
        "add_library_reference",
        "add_pou",
        "add_method",
        "update_pou_item",
        "update_pou_item_patch",
        "add_variable",
        "build",
        "deploy",
        "start_runtime",
        "run_tests",
        "get_test_results",
        "find_fb",
        "search_docs",
        "get_doc_page",
        "generate_docs",
        "get_doc_status",
    }
    assert names == expected
