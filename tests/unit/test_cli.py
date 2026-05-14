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


def test_doctor_returns_zero_when_all_checks_pass(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
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
