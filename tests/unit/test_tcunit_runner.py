"""Tests for the tcunit_runner adapter (with a fake BridgeClient)."""

from __future__ import annotations

from typing import Any

import pytest

from tckit.adapters.test_runners.tcunit_runner import TcUnitRunner
from tckit.utils.bridge_client import BridgeUnavailableError


class FakeBridgeClient:
    """Records calls and returns a configured response."""

    def __init__(
        self,
        response: dict[str, Any] | None = None,
        raise_exc: Exception | None = None,
    ):
        self.calls: list[tuple[str, dict[str, Any], float | None]] = []
        self.response = response or {"success": True}
        self.raise_exc = raise_exc

    def post(
        self,
        path: str,
        payload: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        self.calls.append((path, payload or {}, timeout))
        if self.raise_exc is not None:
            raise self.raise_exc
        return self.response


def test_run_tests_posts_to_tcunit_run_with_target_and_plc(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PLC_PROJECT_PATH", "C:/proj/foo.sln")
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient(
        {
            "success": True,
            "duration_seconds": 12.5,
            "summary": {"suites": 2, "tests": 6, "asserts": 18, "failures": 1, "errors": 0},
            "xml_path": "C:/TwinCAT/3.1/Boot/Plc/TcUnitResults.xml",
        }
    )
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    result = runner.run_tests("1.2.3.4.1.1", plc_name="TestPlc")

    assert result.success is True
    path, payload, timeout = client.calls[0]
    assert path == "/tcunit-run"
    assert payload == {
        "ProjectPath": "C:/proj/foo.sln",
        "TargetAmsId": "1.2.3.4.1.1",
        "PlcName": "TestPlc",
    }
    # Timeouts now resolve in BridgeClient.post via the central
    # route_timeout map; the adapter no longer overrides per-call.
    assert timeout is None
    # Bridge extras flow through as Result.details.
    assert result.details["duration_seconds"] == 12.5
    assert result.details["summary"]["failures"] == 1


def test_run_tests_explicit_plc_name_wins_over_env(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PLC_PROJECT_NAME", "EnvDefault")
    client = FakeBridgeClient({"success": True})
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    runner.run_tests("1.2.3.4.1.1", plc_name="Override")

    _, payload, _ = client.calls[0]
    assert payload["PlcName"] == "Override"


def test_run_tests_no_plc_when_unset(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PLC_PROJECT_NAME", raising=False)
    client = FakeBridgeClient({"success": True})
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    runner.run_tests("1.2.3.4.1.1")

    _, payload, _ = client.calls[0]
    assert "PlcName" not in payload


def test_run_tests_bridge_unreachable_yields_failure_result() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("bridge down"))
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    result = runner.run_tests("1.2.3.4.1.1")

    assert result.success is False
    assert result.error is not None and "bridge down" in result.error


def test_run_tests_timeout_respects_env_override(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # The adapter no longer threads the timeout itself; the central
    # route_timeout map in bridge_client honours TCKIT_TEST_RUN_TIMEOUT
    # and is consulted at the bottom of BridgeClient.post.
    from tckit.utils.bridge_client import route_timeout

    monkeypatch.setenv("TCKIT_TEST_RUN_TIMEOUT", "300")
    assert route_timeout("/tcunit-run") == 300.0


def test_get_results_parses_full_structured_shape() -> None:
    client = FakeBridgeClient(
        {
            "success": True,
            "summary": {
                "suites": 2,
                "tests": 3,
                "asserts": 3,
                "failures": 1,
                "errors": 0,
                "duration_seconds": 0.42,
            },
            "suites": [
                {
                    "name": "FB_Adder_Suite",
                    "tests": [
                        {
                            "name": "Adds_TwoPositives",
                            "passed": True,
                            "asserts": 1,
                            "failures": [],
                            "duration_seconds": 0.05,
                        }
                    ],
                },
                {
                    "name": "FB_Subtracter_Suite",
                    "tests": [
                        {
                            "name": "Subtracts_TwoPositives",
                            "passed": False,
                            "asserts": 1,
                            "failures": [
                                {
                                    "message": "AssertEquals_INT failed",
                                    "expected": "1",
                                    "actual": "2",
                                    "line": 42,
                                }
                            ],
                            "duration_seconds": 0.32,
                        }
                    ],
                },
            ],
            "xml_path": "C:/TwinCAT/3.1/Boot/Plc/TcUnitResults.xml",
        }
    )
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    results = runner.get_results("1.2.3.4.1.1")

    assert results.summary.tests == 3
    assert results.summary.failures == 1
    assert results.total_passed == 1
    assert results.total_failed == 1
    assert results.success is False

    failing_suite = next(s for s in results.suites if s.name == "FB_Subtracter_Suite")
    failure = failing_suite.tests[0].failures[0]
    assert failure.message == "AssertEquals_INT failed"
    assert failure.expected == "1"
    assert failure.actual == "2"
    assert failure.line == 42


def test_get_results_empty_response_yields_empty_test_results() -> None:
    client = FakeBridgeClient({"success": False, "error": "no XML"})
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    results = runner.get_results("1.2.3.4.1.1")

    assert results.suites == []
    assert results.summary.tests == 0
    assert results.success is True  # vacuously: no failures recorded


def test_get_results_bridge_unreachable_yields_empty_test_results() -> None:
    client = FakeBridgeClient(raise_exc=BridgeUnavailableError("bridge down"))
    runner = TcUnitRunner(client=client)  # type: ignore[arg-type]

    results = runner.get_results("1.2.3.4.1.1")

    assert results.suites == []
    assert results.summary.tests == 0
