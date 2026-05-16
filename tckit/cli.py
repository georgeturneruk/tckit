"""TcKit CLI entry point.

Dispatches subcommands:

- ``tckit`` (no args)         start the MCP server on stdio.
- ``tckit --transport sse``   start the MCP server on SSE (CI / containers).
- ``tckit init``              write ``~/.tckit/config.toml`` from the bundled template.
- ``tckit init --print``      emit the template to stdout (no file I/O).
- ``tckit config show``       print resolved config and its sources.
- ``tckit config validate``   check config for missing or malformed values.
- ``tckit doctor``            run health checks (config + bridge).
- ``tckit docgen SRC OUT``    render HTML docs from a TwinCAT solution.

The console script ``tckit`` is wired to :func:`main` via ``pyproject.toml``.
``python -m tckit.server`` keeps working for the bare-server invocation.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from importlib import resources
from typing import Any

from tckit.config import (
    TcKitConfig,
    _load_project_config,
    _load_user_toml,
    _user_home,
    load_config,
)
from tckit.utils.diagnostics import (
    bridge_dependencies,
    bridge_health,
    config_file_status,
    install_bridge_dependency,
    validate_config,
)


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
            "Default: stdio (also via TCKIT_TRANSPORT env var). Use sse for "
            "the CI / containerised server path."
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
    doctor_parser = sub.add_parser(
        "doctor",
        help="Run health checks (config validation + bridge reachability).",
    )
    doctor_parser.add_argument(
        "--no-install",
        action="store_true",
        help=(
            "Don't prompt to install missing bridge dependencies; just "
            "report them. Use in CI or non-interactive contexts."
        ),
    )

    # `tckit init`
    init_parser = sub.add_parser(
        "init",
        help="Write ~/.tckit/config.toml from the bundled template.",
    )
    init_parser.add_argument(
        "--force",
        action="store_true",
        help="Overwrite ~/.tckit/config.toml if it already exists.",
    )
    init_parser.add_argument(
        "--print",
        dest="print_only",
        action="store_true",
        help="Emit the template to stdout without touching the filesystem.",
    )

    # `tckit docgen <project_path> <output_path>`
    docgen_parser = sub.add_parser(
        "docgen",
        help="Render HTML docs from a TwinCAT solution.",
    )
    docgen_parser.add_argument(
        "project_path",
        help="Path to the TwinCAT solution directory (containing a .sln file).",
    )
    docgen_parser.add_argument(
        "output_path",
        help="Directory where the generated HTML site will be written.",
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
        return _doctor(no_install=args.no_install)

    if args.command == "init":
        return _init(force=args.force, print_only=args.print_only)

    if args.command == "docgen":
        return _docgen(args.project_path, args.output_path)

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


def _read_template() -> str:
    """Return the bundled ``config.toml`` template as a string."""
    return (
        resources.files("tckit.templates")
        .joinpath("config.toml.example")
        .read_text(encoding="utf-8")
    )


def _init(force: bool = False, print_only: bool = False) -> int:
    """Scaffold ``~/.tckit/config.toml`` from the bundled template.

    ``--print`` returns the template content to stdout, used by the
    ``tc-config`` skill so it has one source of truth for the template.
    """
    template = _read_template()

    if print_only:
        # Newline-terminated so callers can pipe through `tee` cleanly.
        sys.stdout.write(template)
        if not template.endswith("\n"):
            sys.stdout.write("\n")
        return 0

    home = _user_home()
    target = home / "config.toml"

    if target.exists() and not force:
        print(
            f"{target} already exists. Re-run with --force to overwrite, or "
            "edit it directly.",
            file=sys.stderr,
        )
        return 1

    home.mkdir(parents=True, exist_ok=True)
    target.write_text(template, encoding="utf-8")
    print(f"Wrote {target}")
    print(
        "Next: edit the file to set TARGET_AMS_ID (and any other values), "
        "then run `tckit doctor`."
    )
    return 0


def _docgen(project_path: str, output_path: str) -> int:
    """Run the HTML doc generator against a TwinCAT solution."""
    from tckit.adapters.doc_generators.html_generator import HtmlGenerator

    result = HtmlGenerator().generate(project_path, output_path)
    if not result.success:
        print(f"docgen failed: {result.error}", file=sys.stderr)
        return 1

    details = result.details or {}
    print(
        f"Generated docs for {details.get('plcs', '?')} PLC project(s), "
        f"{details.get('objects', '?')} object(s)."
    )
    print(f"Index: {details.get('index', output_path)}")
    return 0


def _doctor(no_install: bool = False) -> int:
    cfg = load_config()
    bridge_url = cfg.get("BRIDGE_URL")

    sections: list[tuple[str, bool, list[str]]] = []

    # Config-file presence is the first thing a new user gets wrong; surface
    # it before the shape-check so the next-step hint is unambiguous.
    file_present, target_set = config_file_status(cfg)
    file_lines: list[str] = []
    file_ok = True
    if not file_present and not target_set:
        file_ok = False
        file_lines.append(
            f"no config file at {_user_home() / 'config.toml'} and TARGET_AMS_ID unset. "
            "Run `tckit init` (pip) or ask Claude 'Set me up for TcKit' (plugin)."
        )
    elif not target_set:
        file_lines.append(
            "TARGET_AMS_ID is unset. Read-only tools work, but deploy/build/test need it set "
            f"in {_user_home() / 'config.toml'} or as an env var."
        )
    else:
        file_lines.append(f"config file present at {_user_home() / 'config.toml'}")
    sections.append(("Config file", file_ok, file_lines))

    issues = validate_config(cfg)
    sections.append(("Config", not issues, issues))

    bridge_ok, bridge_msg = bridge_health(bridge_url)
    sections.append(("Bridge", bridge_ok, [bridge_msg]))

    # Bridge dependencies only checked when the bridge is reachable.
    dep_section_ok = True
    dep_lines: list[str] = []
    missing_deps: list[str] = []
    if bridge_ok:
        deps = bridge_dependencies(bridge_url)
        if not deps:
            dep_lines.append(
                "bridge /health returned no dependencies block "
                "(older bridge? upgrade Start-Bridge.ps1)"
            )
        else:
            for name, version in sorted(deps.items()):
                if version is None:
                    dep_lines.append(f"{name}: not installed")
                    missing_deps.append(name)
                    dep_section_ok = False
                else:
                    dep_lines.append(f"{name}: {version}")
        sections.append(("Bridge dependencies", dep_section_ok, dep_lines))

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

    # Offer to install missing bridge dependencies.
    if missing_deps and not no_install:
        for dep in missing_deps:
            if _prompt_yes(
                f"\nInstall missing bridge dependency '{dep}' via PowerShell Gallery now?"
            ):
                ok, msg = install_bridge_dependency(dep, bridge_url)
                status = "OK" if ok else "FAIL"
                print(f"  [{status}] {msg}")
                if ok:
                    overall = True  # treat the install success as recovering the dep section
            else:
                print(f"  Skipped {dep}; install with:")
                print(
                    f"    Install-Module -Name {dep} -Scope CurrentUser -Force"
                )

    print("\n" + "=" * 50)
    print(f"Overall: {'PASS' if overall else 'FAIL'}")
    if not overall:
        if not bridge_ok:
            print(
                "\nHint: if the bridge is down, start it with .\\bridge\\Start-Bridge.ps1 "
                "(Windows) or check BRIDGE_URL."
            )
        elif missing_deps and no_install:
            print(
                "\nHint: bridge dependencies missing. Run without --no-install "
                "to prompt for installation, or install manually:"
            )
            for dep in missing_deps:
                print(
                    f"  Install-Module -Name {dep} -Scope CurrentUser -Force"
                )
    return 0 if overall else 1


def _prompt_yes(question: str) -> bool:
    """Prompt ``question`` with default-yes. Returns True on Y/y/Enter."""
    try:
        answer = input(f"{question} [Y/n] ").strip().lower()
    except EOFError:
        return False
    return answer in ("", "y", "yes")


if __name__ == "__main__":
    sys.exit(main())
