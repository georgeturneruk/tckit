"""TcKit CLI entry point.

Dispatches subcommands:

- ``tckit`` (no args)              start the MCP server on stdio.
- ``tckit --transport sse``        start the MCP server on SSE (CI / containers).
- ``tckit init``                   write ``~/.tckit/config.toml`` from the bundled template.
- ``tckit init --with-claude-md``  also drop the TwinCAT CLAUDE.md template into cwd.
- ``tckit init --print``           emit the template to stdout (no file I/O).
- ``tckit config show``            print resolved config and its sources.
- ``tckit config validate``        check config for missing or malformed values.
- ``tckit doctor``                 run health checks (config + bridge).
- ``tckit bridge install``         copy the bundled bridge to ``~/.tckit/bridge/``.
- ``tckit docgen SRC OUT``         render HTML docs from a TwinCAT solution.

The console script ``tckit`` is wired to :func:`main` via ``pyproject.toml``.
``python -m tckit.server`` keeps working for the bare-server invocation.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
from importlib import resources
from pathlib import Path
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
    tcunit_xml_status,
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
    init_parser.add_argument(
        "--with-claude-md",
        dest="with_claude_md",
        action="store_true",
        help=(
            "Also drop the TwinCAT CLAUDE.md template into the current "
            "directory (linker file + twincat/ topic files). Existing "
            "files are not overwritten unless --force is also passed."
        ),
    )

    # `tckit bridge install`
    bridge_parser = sub.add_parser(
        "bridge",
        help="Manage the Windows bridge service shipped with tckit.",
    )
    bridge_sub = bridge_parser.add_subparsers(
        dest="bridge_command", metavar="SUBCOMMAND"
    )
    bridge_install_parser = bridge_sub.add_parser(
        "install",
        help="Copy the bundled bridge to ~/.tckit/bridge/.",
    )
    bridge_install_parser.add_argument(
        "--force",
        action="store_true",
        help="Overwrite ~/.tckit/bridge/ if it already exists.",
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
        return _init(
            force=args.force,
            print_only=args.print_only,
            with_claude_md=args.with_claude_md,
        )

    if args.command == "bridge":
        if args.bridge_command == "install":
            return _bridge_install(force=args.force)
        # `tckit bridge` with no subcommand: print the bridge-subcommand help.
        parser.parse_args(["bridge", "--help"])
        return 0

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


def _init(
    force: bool = False,
    print_only: bool = False,
    with_claude_md: bool = False,
) -> int:
    """Scaffold ``~/.tckit/config.toml`` from the bundled template.

    ``--print`` returns the template content to stdout, used by the
    ``tc-config`` skill so it has one source of truth for the template.

    ``--with-claude-md`` additionally drops the TwinCAT CLAUDE.md
    template into the current directory (linker + topic files). If
    the user-global config already exists, ``--with-claude-md`` lets
    the command proceed for the CLAUDE.md side rather than failing
    on the already-present config.
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
    user_global_exists = target.exists()

    if user_global_exists and not force and not with_claude_md:
        print(
            f"{target} already exists. Re-run with --force to overwrite, or "
            "edit it directly.",
            file=sys.stderr,
        )
        return 1

    if not user_global_exists or force:
        home.mkdir(parents=True, exist_ok=True)
        target.write_text(template, encoding="utf-8")
        print(f"Wrote {target}")
        print(
            "Next: edit the file to set TARGET_AMS_ID (and any other values), "
            "then run `tckit doctor`."
        )
    else:
        print(f"{target} already exists; skipping user-global config.")

    if with_claude_md:
        from tckit.templates import install_twincat_claude_md

        cwd = Path.cwd()
        written = install_twincat_claude_md(cwd, overwrite=force)
        if written:
            print(f"Wrote TwinCAT CLAUDE.md template into {cwd}:")
            for path in written:
                print(f"  {path.relative_to(cwd)}")
        else:
            print(
                f"TwinCAT CLAUDE.md template already present in {cwd}; "
                "re-run with --force to overwrite."
            )

    return 0


def _bridge_source_root() -> Path:
    """Locate the bundled bridge tree.

    Wheel installs find it at ``tckit/_bridge/`` (force-included by hatch).
    Editable installs from a source checkout don't have ``tckit/_bridge/``;
    fall back to the repo's ``bridge/`` directory so contributors can drive
    the install command from a dev checkout without building a wheel first.
    """
    wheel_path = Path(__file__).parent / "_bridge"
    if (wheel_path / "Start-Bridge.ps1").exists():
        return wheel_path

    repo_path = Path(__file__).parent.parent / "bridge"
    if (repo_path / "Start-Bridge.ps1").exists():
        return repo_path

    raise FileNotFoundError(
        "Could not locate the bundled bridge files. This is a packaging "
        "bug; please file an issue at "
        "https://github.com/georgeturneruk/tckit/issues."
    )


def _bridge_install(force: bool = False) -> int:
    """Copy the bundled bridge tree into ``~/.tckit/bridge/``.

    Mirrors ``tckit init``'s overwrite behaviour: refuses to clobber an
    existing directory unless ``--force`` is passed. The bridge tests
    directory is dev-only and intentionally excluded from the wheel, so
    end users only get ``Start-Bridge.ps1`` and ``harness/``.
    """
    try:
        src = _bridge_source_root()
    except FileNotFoundError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    dst = _user_home() / "bridge"

    if dst.exists() and not force:
        print(
            f"{dst} already exists. Re-run with --force to overwrite, or "
            "delete it first.",
            file=sys.stderr,
        )
        return 1

    if dst.exists():
        shutil.rmtree(dst)

    # Copy only the runtime files: Start-Bridge.ps1 + harness/. Skip tests/
    # even when the source is the repo (editable install), to keep the
    # installed tree identical in both layouts.
    dst.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src / "Start-Bridge.ps1", dst / "Start-Bridge.ps1")
    shutil.copytree(src / "harness", dst / "harness")

    print(f"Installed bridge to {dst}")
    print(
        "Start it in a separate PowerShell window with TcXaeShell open:\n"
        f"  {dst / 'Start-Bridge.ps1'}"
    )
    return 0


def _bridge_installed_script() -> Path:
    """Where ``tckit bridge install`` lands the launcher."""
    return _user_home() / "bridge" / "Start-Bridge.ps1"


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
                "(older bridge? run `tckit bridge install --force` to refresh)"
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

        # TcUnit XML path resolution. Catches the kernel-vs-UmRT mismatch
        # that paid 9x on the T1 bench. WARN on ambiguity is treated as
        # OK overall (the freshest candidate is still the right one) but
        # surfaced so operators can pin via TCKIT_TCUNIT_XML_PATH.
        # See ADR-0011.
        xml_ok, xml_warn, xml_lines = tcunit_xml_status(bridge_url)
        section_label = "TcUnit results path"
        if xml_warn:
            xml_lines = ["[WARN] multiple candidates"] + xml_lines
        sections.append((section_label, xml_ok, xml_lines))

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

    # Offer to install the bridge itself if it's down and not yet on disk.
    # This is the typical first-run state for users coming in via the
    # Claude Code plugin: the MCP server is running but they have no
    # ~/.tckit/bridge/ because nothing has copied it there yet.
    installed_script = _bridge_installed_script()
    if not bridge_ok and not installed_script.exists() and not no_install:
        if _prompt_yes(
            f"\nBridge isn't reachable and {installed_script} doesn't exist. "
            "Install the bundled bridge to ~/.tckit/bridge/ now?"
        ):
            rc = _bridge_install(force=False)
            if rc == 0:
                print(
                    f"\nNow start it: {installed_script}\n"
                    "(open in a PowerShell window with TcXaeShell running)"
                )

    nudge = _claude_md_nudge()
    if nudge:
        print("\n[INFO] CLAUDE.md")
        print(f"  {nudge}")

    print("\n" + "=" * 50)
    print(f"Overall: {'PASS' if overall else 'FAIL'}")
    if not overall:
        if not bridge_ok:
            installed_script = _bridge_installed_script()
            if installed_script.exists():
                print(
                    f"\nHint: start the bridge with {installed_script} in a "
                    "PowerShell window with TcXaeShell open, or check BRIDGE_URL."
                )
            else:
                print(
                    "\nHint: bridge isn't installed yet. Run `tckit bridge install` "
                    "to copy it to ~/.tckit/bridge/Start-Bridge.ps1, then start it "
                    "in a PowerShell window with TcXaeShell open. Contributors "
                    "working from a repo checkout can also run "
                    ".\\bridge\\Start-Bridge.ps1 directly."
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


def _claude_md_nudge() -> str | None:
    """Return a one-line nudge if a `.sln` is in the cwd tree without a sibling CLAUDE.md.

    Walks from the cwd upward looking for the first directory that
    contains a `.sln`. If that directory has no `CLAUDE.md`, returns
    a nudge string; otherwise returns ``None``.
    """
    cwd = Path.cwd()
    for parent in (cwd, *cwd.parents):
        slns = sorted(parent.glob("*.sln"))
        if not slns:
            continue
        if (parent / "CLAUDE.md").exists():
            return None
        return (
            f"No CLAUDE.md alongside {slns[0].name} in {parent}. "
            "To drop in tckit's TwinCAT conventions template, run "
            "`tckit init --with-claude-md`."
        )
    return None


if __name__ == "__main__":
    sys.exit(main())
