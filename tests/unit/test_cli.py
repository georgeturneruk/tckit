"""Tests for tckit.cli — argument dispatch and subcommand exit codes."""

from __future__ import annotations

import io
import json
from contextlib import redirect_stdout

import pytest

from tckit import cli

# ---------------------------------------------------------------------------
# _build_parser — argument routing
# ---------------------------------------------------------------------------


def test_no_args_means_no_subcommand_default_stdio(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.delenv("TCKIT_TRANSPORT", raising=False)
    args = cli._build_parser().parse_args([])
    assert args.command is None
    assert args.transport == "stdio"


def test_transport_flag_passes_through(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("TCKIT_TRANSPORT", raising=False)
    args = cli._build_parser().parse_args(["--transport", "sse"])
    assert args.command is None
    assert args.transport == "sse"


def test_config_show_recognised() -> None:
    args = cli._build_parser().parse_args(["config", "show"])
    assert args.command == "config"
    assert args.config_command == "show"


def test_config_validate_recognised() -> None:
    args = cli._build_parser().parse_args(["config", "validate"])
    assert args.command == "config"
    assert args.config_command == "validate"


def test_doctor_recognised() -> None:
    args = cli._build_parser().parse_args(["doctor"])
    assert args.command == "doctor"


def test_unknown_subcommand_rejected() -> None:
    parser = cli._build_parser()
    with pytest.raises(SystemExit):
        parser.parse_args(["bogus-subcommand"])


# ---------------------------------------------------------------------------
# main() dispatch — each subcommand routes correctly
# ---------------------------------------------------------------------------


def test_main_dispatches_config_show(monkeypatch: pytest.MonkeyPatch) -> None:
    called: dict[str, bool] = {}

    def fake_show() -> int:
        called["show"] = True
        return 0

    monkeypatch.setattr(cli, "_config_show", fake_show)
    rc = cli.main(["config", "show"])
    assert rc == 0
    assert called.get("show") is True


def test_main_dispatches_config_validate(monkeypatch: pytest.MonkeyPatch) -> None:
    called: dict[str, bool] = {}

    def fake_validate() -> int:
        called["validate"] = True
        return 1

    monkeypatch.setattr(cli, "_config_validate", fake_validate)
    rc = cli.main(["config", "validate"])
    assert rc == 1
    assert called.get("validate") is True


def test_main_dispatches_doctor(monkeypatch: pytest.MonkeyPatch) -> None:
    called: dict[str, bool] = {}

    def fake_doctor(no_install: bool = False) -> int:
        called["doctor"] = True
        called["no_install"] = no_install
        return 0

    monkeypatch.setattr(cli, "_doctor", fake_doctor)
    rc = cli.main(["doctor"])
    assert rc == 0
    assert called.get("doctor") is True
    assert called.get("no_install") is False


def test_main_dispatches_doctor_with_no_install_flag(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured: dict[str, bool] = {}

    def fake_doctor(no_install: bool = False) -> int:
        captured["no_install"] = no_install
        return 0

    monkeypatch.setattr(cli, "_doctor", fake_doctor)
    rc = cli.main(["doctor", "--no-install"])
    assert rc == 0
    assert captured["no_install"] is True


def test_main_dispatches_to_server_when_no_subcommand(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured: dict[str, str] = {}

    def fake_run_server(transport: str) -> int:
        captured["transport"] = transport
        return 0

    monkeypatch.setattr(cli, "_run_server", fake_run_server)
    rc = cli.main(["--transport", "sse"])
    assert rc == 0
    assert captured["transport"] == "sse"


# ---------------------------------------------------------------------------
# _config_show — payload shape
# ---------------------------------------------------------------------------


def test_config_show_emits_expected_json_keys(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._config_show()
    assert rc == 0

    payload = json.loads(buf.getvalue())
    assert "user_home" in payload
    assert "user_config_toml" in payload
    assert "project_config_json" in payload
    assert "resolved" in payload
    assert "reader" in payload["resolved"]


# ---------------------------------------------------------------------------
# _config_validate — exit codes match issue presence
# ---------------------------------------------------------------------------


def test_config_validate_exits_zero_when_clean(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)
    for var in ("TARGET_AMS_ID", "ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        monkeypatch.delenv(var, raising=False)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._config_validate()
    assert rc == 0
    assert "valid" in buf.getvalue().lower()


def test_config_validate_exits_one_when_bad_netid(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)
    monkeypatch.setenv("TARGET_AMS_ID", "not-a-netid")
    monkeypatch.delenv("ALLOWED_NETIDS", raising=False)
    monkeypatch.delenv("BLOCKED_NETIDS", raising=False)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._config_validate()
    assert rc == 1
    assert "TARGET_AMS_ID" in buf.getvalue()


# ---------------------------------------------------------------------------
# _doctor — overall pass/fail bookkeeping
# ---------------------------------------------------------------------------


def _stub_doctor_deps(monkeypatch: pytest.MonkeyPatch, deps: dict) -> None:
    """Stub the bridge dependency probe used by _doctor."""
    monkeypatch.setattr(
        "tckit.cli.bridge_dependencies", lambda url=None: deps
    )
    # tcunit_xml_status fires after dependencies whenever the bridge is up.
    # Default to an OK kernel-RT resolve so the existing dep-focused tests
    # don't have to thread this through every call. Tests targeting the
    # TcUnit section override this stub explicitly.
    monkeypatch.setattr(
        "tckit.cli.tcunit_xml_status",
        lambda url=None: (True, False, ["kernel-RT path resolves: C:\\TwinCAT\\..."]),
    )


def _stub_config_file_present(monkeypatch: pytest.MonkeyPatch) -> None:
    """Pretend the user has a config file and TARGET_AMS_ID set, so the
    Config-file section in _doctor passes and the test can focus on the
    section it actually exercises."""
    monkeypatch.setattr(
        "tckit.cli.config_file_status", lambda cfg: (True, True)
    )


def test_doctor_returns_zero_when_all_checks_pass(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": "6.2.127"})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    assert rc == 0
    assert "PASS" in buf.getvalue()
    assert "TcXaeMgmt: 6.2.127" in buf.getvalue()


def test_doctor_returns_one_when_bridge_down(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health",
        lambda url=None: (False, "not reachable at http://x"),
    )
    # Dependencies probe should not be called when bridge is down; stub anyway
    # so any accidental call returns deterministically.
    _stub_doctor_deps(monkeypatch, {})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    assert rc == 1
    assert "FAIL" in buf.getvalue()
    assert "Start-Bridge.ps1" in buf.getvalue()


def test_doctor_returns_one_when_config_invalid(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr(
        "tckit.cli.validate_config",
        lambda cfg: ["TARGET_AMS_ID is malformed"],
    )
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": "6.2.127"})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    assert rc == 1
    assert "FAIL" in buf.getvalue()
    assert "TARGET_AMS_ID" in buf.getvalue()


def test_doctor_no_install_reports_missing_dep_and_fails(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": None})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    assert rc == 1
    output = buf.getvalue()
    assert "TcXaeMgmt: not installed" in output
    assert "FAIL" in output
    assert "Install-Module" in output


def test_doctor_prompts_and_installs_missing_dep(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": None})
    monkeypatch.setattr("tckit.cli._prompt_yes", lambda question: True)
    installed: list[str] = []

    def fake_install(name: str, url: str | None = None) -> tuple[bool, str]:
        installed.append(name)
        return True, f"installed {name} 6.2.127"

    monkeypatch.setattr("tckit.cli.install_bridge_dependency", fake_install)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=False)
    assert installed == ["TcXaeMgmt"]
    assert "installed TcXaeMgmt 6.2.127" in buf.getvalue()
    assert rc == 0


def test_doctor_skips_install_when_user_declines(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": None})
    monkeypatch.setattr("tckit.cli._prompt_yes", lambda question: False)

    def reject_install(name: str, url: str | None = None) -> tuple[bool, str]:
        raise AssertionError("install_bridge_dependency must not be called")

    monkeypatch.setattr("tckit.cli.install_bridge_dependency", reject_install)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=False)
    assert "Skipped TcXaeMgmt" in buf.getvalue()
    assert rc == 1


def test_doctor_fails_loud_when_config_file_missing(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """First-touch new user case: no ~/.tckit/config.toml, no env var."""
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)

    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": "6.2.127"})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    output = buf.getvalue()
    assert rc == 1
    assert "no config file" in output
    assert "tckit init" in output


def test_doctor_warns_but_passes_when_file_present_but_target_unset(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Read-only user: file exists but TARGET_AMS_ID is empty — should warn, not fail."""
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("TCKIT_CONFIG", raising=False)
    monkeypatch.delenv("TARGET_AMS_ID", raising=False)
    (tmp_path / "config.toml").write_text('XAE_MODE = "attach"\n')

    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": "6.2.127"})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    output = buf.getvalue()
    assert rc == 0
    assert "TARGET_AMS_ID is unset" in output


def test_doctor_tcunit_section_passes_with_ambiguity_warning(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Multiple UmRT candidates: WARN line surfaces but overall stays OK."""
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    monkeypatch.setattr(
        "tckit.cli.bridge_dependencies", lambda url=None: {"TcXaeMgmt": "6.2.127"}
    )
    monkeypatch.setattr(
        "tckit.cli.tcunit_xml_status",
        lambda url=None: (
            True,
            True,
            [
                "multiple UmRT candidates (2); freshest will be used:",
                "  freshest: C:\\ProgramData\\...\\UmRT_A\\...",
                "  alt:      C:\\ProgramData\\...\\UmRT_B\\...",
            ],
        ),
    )

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    output = buf.getvalue()
    assert rc == 0
    assert "TcUnit results path" in output
    assert "[WARN]" in output
    assert "multiple UmRT candidates" in output


def test_doctor_tcunit_section_fails_when_no_xml_resolves(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Zero candidates: section is FAIL and overall doctor returns 1."""
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    monkeypatch.setattr(
        "tckit.cli.bridge_dependencies", lambda url=None: {"TcXaeMgmt": "6.2.127"}
    )
    monkeypatch.setattr(
        "tckit.cli.tcunit_xml_status",
        lambda url=None: (False, False, ["no TcUnit XML found. Searched: ..."]),
    )

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    output = buf.getvalue()
    assert rc == 1
    assert "TcUnit results path" in output
    assert "[FAIL] TcUnit results path" in output


# ---------------------------------------------------------------------------
# _init — scaffold ~/.tckit/config.toml
# ---------------------------------------------------------------------------


def test_init_writes_template_when_file_absent(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._init()
    assert rc == 0

    target = tmp_path / "config.toml"
    assert target.exists()
    assert "TARGET_AMS_ID" in target.read_text(encoding="utf-8")
    assert str(target) in buf.getvalue()


def test_init_refuses_to_overwrite_without_force(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    target = tmp_path / "config.toml"
    target.write_text("# don't touch me\n", encoding="utf-8")

    rc = cli._init()
    assert rc == 1
    assert target.read_text(encoding="utf-8") == "# don't touch me\n"
    err = capsys.readouterr().err
    assert "already exists" in err
    assert "--force" in err


def test_init_force_overwrites_existing(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))
    target = tmp_path / "config.toml"
    target.write_text("# stale\n", encoding="utf-8")

    rc = cli._init(force=True)
    assert rc == 0
    assert "TARGET_AMS_ID" in target.read_text(encoding="utf-8")


def test_init_print_emits_template_without_touching_disk(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TCKIT_HOME", str(tmp_path))

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._init(print_only=True)
    assert rc == 0
    assert "TARGET_AMS_ID" in buf.getvalue()
    assert not (tmp_path / "config.toml").exists()


def test_init_print_matches_template_on_disk() -> None:
    """`tckit init --print` and the bundled template must be byte-for-byte equal
    (single source of truth for the skill that reads the printed output)."""
    from importlib import resources

    template = (
        resources.files("tckit.templates")
        .joinpath("config.toml.example")
        .read_text(encoding="utf-8")
    )
    buf = io.StringIO()
    with redirect_stdout(buf):
        cli._init(print_only=True)
    printed = buf.getvalue()
    # _init guarantees a trailing newline; the template already ends with one,
    # so the printed output should equal the file content exactly.
    assert printed == template if template.endswith("\n") else printed == template + "\n"


def test_init_recognised_by_parser() -> None:
    args = cli._build_parser().parse_args(["init"])
    assert args.command == "init"
    assert args.force is False
    assert args.print_only is False


def test_init_parser_force_flag() -> None:
    args = cli._build_parser().parse_args(["init", "--force"])
    assert args.force is True


def test_init_parser_print_flag() -> None:
    args = cli._build_parser().parse_args(["init", "--print"])
    assert args.print_only is True


def test_init_parser_with_claude_md_flag() -> None:
    args = cli._build_parser().parse_args(["init", "--with-claude-md"])
    assert args.with_claude_md is True


def test_init_with_claude_md_writes_template_into_cwd(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    home = tmp_path / "home"
    cwd = tmp_path / "project"
    cwd.mkdir()
    monkeypatch.setenv("TCKIT_HOME", str(home))
    monkeypatch.chdir(cwd)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._init(with_claude_md=True)
    assert rc == 0
    assert (cwd / "CLAUDE.md").exists()
    assert (cwd / "twincat" / "cyclic-in-method.md").exists()
    assert (home / "config.toml").exists()  # user-global also written
    out = buf.getvalue()
    assert "CLAUDE.md" in out
    assert "twincat" in out


def test_init_with_claude_md_proceeds_when_user_global_exists(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """An existing ~/.tckit/config.toml must NOT block --with-claude-md."""
    home = tmp_path / "home"
    home.mkdir()
    (home / "config.toml").write_text("# pre-existing\n", encoding="utf-8")

    cwd = tmp_path / "project"
    cwd.mkdir()
    monkeypatch.setenv("TCKIT_HOME", str(home))
    monkeypatch.chdir(cwd)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._init(with_claude_md=True)
    assert rc == 0
    assert (home / "config.toml").read_text(encoding="utf-8") == "# pre-existing\n"
    assert (cwd / "CLAUDE.md").exists()
    out = buf.getvalue()
    assert "skipping user-global config" in out


def test_init_without_with_claude_md_still_fails_when_user_global_exists(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Existing pre-existing behaviour: bare `tckit init` fails if config exists."""
    home = tmp_path / "home"
    home.mkdir()
    (home / "config.toml").write_text("# pre-existing\n", encoding="utf-8")
    monkeypatch.setenv("TCKIT_HOME", str(home))

    rc = cli._init()
    assert rc == 1
    assert "already exists" in capsys.readouterr().err


def test_init_with_claude_md_skips_existing_linker_without_force(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    home = tmp_path / "home"
    cwd = tmp_path / "project"
    cwd.mkdir()
    (cwd / "CLAUDE.md").write_text("# my own CLAUDE.md\n", encoding="utf-8")
    monkeypatch.setenv("TCKIT_HOME", str(home))
    monkeypatch.chdir(cwd)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._init(with_claude_md=True)
    assert rc == 0
    # Linker preserved, but topic files still laid down.
    assert (cwd / "CLAUDE.md").read_text(encoding="utf-8") == "# my own CLAUDE.md\n"
    assert (cwd / "twincat" / "cyclic-in-method.md").exists()


def test_init_with_claude_md_force_overwrites_linker(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    home = tmp_path / "home"
    cwd = tmp_path / "project"
    cwd.mkdir()
    (cwd / "CLAUDE.md").write_text("# stale\n", encoding="utf-8")
    monkeypatch.setenv("TCKIT_HOME", str(home))
    monkeypatch.chdir(cwd)

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._init(with_claude_md=True, force=True)
    assert rc == 0
    body = (cwd / "CLAUDE.md").read_text(encoding="utf-8")
    assert "TwinCAT conventions" in body


# ---------------------------------------------------------------------------
# _claude_md_nudge — doctor's CLAUDE.md hint
# ---------------------------------------------------------------------------


def test_claude_md_nudge_silent_when_no_sln_present(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.chdir(tmp_path)
    assert cli._claude_md_nudge() is None


def test_claude_md_nudge_silent_when_claude_md_alongside_sln(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    (tmp_path / "Solution.sln").write_text("", encoding="utf-8")
    (tmp_path / "CLAUDE.md").write_text("", encoding="utf-8")
    monkeypatch.chdir(tmp_path)
    assert cli._claude_md_nudge() is None


def test_claude_md_nudge_fires_when_sln_present_without_claude_md(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    (tmp_path / "Solution.sln").write_text("", encoding="utf-8")
    monkeypatch.chdir(tmp_path)

    nudge = cli._claude_md_nudge()
    assert nudge is not None
    assert "Solution.sln" in nudge
    assert "tckit init --with-claude-md" in nudge


def test_claude_md_nudge_walks_up_to_find_sln(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    nested = tmp_path / "a" / "b" / "c"
    nested.mkdir(parents=True)
    (tmp_path / "Solution.sln").write_text("", encoding="utf-8")
    monkeypatch.chdir(nested)

    nudge = cli._claude_md_nudge()
    assert nudge is not None
    assert "Solution.sln" in nudge


def test_doctor_surfaces_claude_md_nudge_when_applicable(
    tmp_path,  # type: ignore[no-untyped-def]
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    (tmp_path / "Solution.sln").write_text("", encoding="utf-8")
    monkeypatch.chdir(tmp_path)
    _stub_config_file_present(monkeypatch)
    monkeypatch.setattr("tckit.cli.validate_config", lambda cfg: [])
    monkeypatch.setattr(
        "tckit.cli.bridge_health", lambda url=None: (True, "reachable at http://x")
    )
    _stub_doctor_deps(monkeypatch, {"TcXaeMgmt": "6.2.127"})

    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = cli._doctor(no_install=True)
    out = buf.getvalue()
    assert rc == 0
    assert "[INFO] CLAUDE.md" in out
    assert "tckit init --with-claude-md" in out
