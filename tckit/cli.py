"""TcKit CLI entry point.

Dispatches subcommands:

- ``tckit`` (no args)         start the MCP server on stdio.
- ``tckit --transport sse``   start the MCP server on SSE (Docker mode).
- ``tckit config show``       print resolved config and its sources.
- ``tckit config validate``   check config for missing or malformed values.
- ``tckit doctor``            run health checks (config + bridge).

The console script ``tckit`` is wired to :func:`main` via ``pyproject.toml``.
``python -m tckit.server`` keeps working for the bare-server invocation.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any

from tckit.config import (
    TcKitConfig,
    _load_project_config,
    _load_user_toml,
    _user_home,
    load_config,
)
from tckit.utils.diagnostics import bridge_health, validate_config


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="tckit",
        description="TcKit MCP server: connects Claude Code to TwinCAT 3 PLC projects.",
    )
    parser.add_argument(
        "--transport",
        choices=["stdio", "sse"],
        default=os.getenv("TCKIT_TRANSPORT", "stdio"),
        help=(
            "MCP transport for the server (used when no subcommand is given). "
            "Default: stdio (also via TCKIT_TRANSPORT env var). Use sse for the "
            "Docker / long-running server path."
        ),
    )

    sub = parser.add_subparsers(dest="command", metavar="COMMAND")

    # `tckit config ...`
    config_parser = sub.add_parser(
        "config",
        help="Inspect or validate TcKit configuration.",
    )
    config_sub = config_parser.add_subparsers(
        dest="config_command", metavar="SUBCOMMAND"
    )
    config_sub.add_parser("show", help="Print resolved config and its sources.")
    config_sub.add_parser("validate", help="Check config for malformed values.")

    # `tckit doctor`
    sub.add_parser(
        "doctor",
        help="Run health checks (config validation + bridge reachability).",
    )

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _build_parser()
    args = parser.parse_args(argv)

    if args.command == "config":
        if args.config_command == "show":
            return _config_show()
        if args.config_command == "validate":
            return _config_validate()
        # `tckit config` with no subcommand: print the config-subcommand help.
        parser.parse_args(["config", "--help"])
        return 0

    if args.command == "doctor":
        return _doctor()

    return _run_server(args.transport)


# ---------------------------------------------------------------------------
# Subcommand implementations
# ---------------------------------------------------------------------------


def _run_server(transport: str) -> int:
    """Start the MCP server with the chosen transport."""
    from tckit.server import _build_mcp, _register_tools

    mcp = _build_mcp(transport)
    _register_tools(mcp)
    mcp.run(transport=transport)
    return 0


_RESOLVED_KEYS: tuple[str, ...] = (
    "reader",
    "writer",
    "builder",
    "test_runner",
    "doc_generator",
    "docs_searcher",
    "BRIDGE_URL",
    "TARGET_AMS_ID",
    "ALLOWED_NETIDS",
    "BLOCKED_NETIDS",
    "SAFETY_CONFIRMATIONS",
    "COM_VERSION",
    "XAE_MODE",
    "PLC_PROJECT_PATH",
    "PLC_PROJECT_NAME",
)


def _build_show_payload(cfg: TcKitConfig) -> dict[str, Any]:
    """Assemble the structured payload printed by ``tckit config show``."""
    return {
        "user_home": str(_user_home()),
        "user_config_toml": _load_user_toml(),
        "project_config_json": _load_project_config(),
        "resolved": {key: cfg.get(key) for key in _RESOLVED_KEYS},
    }


def _config_show() -> int:
    cfg = load_config()
    print(json.dumps(_build_show_payload(cfg), indent=2, default=str))
    return 0


def _config_validate() -> int:
    cfg = load_config()
    issues = validate_config(cfg)
    if not issues:
        print("Configuration is valid.")
        return 0
    print("Configuration issues found:")
    for issue in issues:
        print(f"  - {issue}")
    return 1


def _doctor() -> int:
    cfg = load_config()
    sections: list[tuple[str, bool, list[str]]] = []

    issues = validate_config(cfg)
    sections.append(("Config", not issues, issues))

    ok, msg = bridge_health(cfg.get("BRIDGE_URL"))
    sections.append(("Bridge", ok, [msg]))

    print("TcKit doctor")
    print("=" * 50)
    overall = True
    for name, ok, lines in sections:
        status = "OK" if ok else "FAIL"
        print(f"\n[{status}] {name}")
        for line in lines:
            print(f"  {line}")
        if not ok:
            overall = False

    print("\n" + "=" * 50)
    print(f"Overall: {'PASS' if overall else 'FAIL'}")
    if not overall:
        print(
            "\nHint: if the bridge is down, start it with .\\bridge\\Start-Bridge.ps1 "
            "(Windows) or check BRIDGE_URL."
        )
    return 0 if overall else 1


if __name__ == "__main__":
    sys.exit(main())
