"""tcunit_runner — TestRunner adapter for TcUnit test execution.

Thin route-caller against the Windows bridge, mirroring the shape of
``automation_writer``. ``run_tests`` blocks until the bridge's
Invoke-TcUnitRun.ps1 returns (suites finished or timeout); ``get_results``
reads the parsed XML from ``POST /results``.

Multi-project sln support (ADR-0005): ``target_ams_id`` is the required
first positional argument; ``plc_name`` flows through to the bridge as
``PlcName`` (auto-resolved on single-project slns).
"""

from __future__ import annotations

import os
from typing import Any

from tckit.ports.test_runner import TestRunner
from tckit.ports.types import (
    AssertFailure,
    Result,
    TestCase,
    TestResults,
    TestResultsSummary,
    TestSuite,
)
from tckit.utils.bridge_client import BridgeClient, BridgeError
from tckit.utils.results import to_result


class TcUnitRunner(TestRunner):
    """Runs TcUnit tests and parses XML results into structured TestResults."""

    def __init__(self, client: BridgeClient | None = None) -> None:
        self._client = client or BridgeClient()

    # ------------------------------------------------------------------
    # TestRunner interface
    # ------------------------------------------------------------------

    def run_tests(
        self,
        target_ams_id: str,
        *,
        plc_name: str | None = None,
        probes: list[str] | None = None,
    ) -> Result:
        extra: dict[str, Any] = {}
        if probes:
            # The bridge's Invoke-TcUnitRun reads each instance path via
            # ADS after AllTestSuitesFinished flips, returning their
            # values under `details.probes`. Useful when the xUnit XML
            # publisher is off (its default) and the caller wants
            # pass/fail straight from the runtime. Joined with newlines
            # because the bridge's request decoder collapses nested
            # string arrays unhelpfully on PowerShell 5.1; named
            # ReadSymbols rather than Probes because PowerShell's
            # parameter binding garbles a key literally named "Probes".
            extra["ReadSymbols"] = "\n".join(probes)
        payload = self._with_target_and_plc(extra, target_ams_id, plc_name)
        try:
            resp = self._client.post("/tcunit-run", payload)
        except BridgeError as exc:
            return Result(success=False, error=str(exc))
        return to_result(resp)

    def get_results(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> TestResults:
        payload = self._with_target_and_plc({}, target_ams_id, plc_name)
        try:
            resp = self._client.post("/results", payload)
        except BridgeError:
            return TestResults()
        return _parse_test_results(resp)

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _with_target_and_plc(
        self,
        payload: dict[str, Any],
        target_ams_id: str,
        plc_name: str | None,
    ) -> dict[str, Any]:
        """Attach project path, target, and resolved PLC name to the payload.

        Mirrors ``AutomationWriter._with_project`` but with an additional
        ``TargetAmsId`` (ADR-0005: every test-execution call carries an
        explicit target). Per-call ``plc_name`` wins over the env var.
        """
        merged: dict[str, Any] = {
            "ProjectPath": os.getenv("PLC_PROJECT_PATH", ""),
            "TargetAmsId": target_ams_id,
        }
        resolved_plc = plc_name or os.getenv("PLC_PROJECT_NAME")
        if resolved_plc:
            merged["PlcName"] = resolved_plc
        merged.update(payload)
        return merged


# ---------------------------------------------------------------------------
# Response → dataclass mappers
# ---------------------------------------------------------------------------


def _parse_test_results(resp: dict[str, Any]) -> TestResults:
    """Map the bridge's JSON response from ``POST /results`` onto a TestResults.

    A missing or unsuccessful response yields an empty TestResults rather
    than raising — callers can inspect ``success`` via ``run_tests``'s
    Result; ``get_results`` is for the structured data.
    """
    suites_raw = resp.get("suites") or []
    suites = [_parse_suite(s) for s in suites_raw if isinstance(s, dict)]
    summary = _parse_summary(resp.get("summary"))
    return TestResults(suites=suites, summary=summary)


def _parse_suite(raw: dict[str, Any]) -> TestSuite:
    return TestSuite(
        name=str(raw.get("name", "")),
        tests=[_parse_case(t) for t in raw.get("tests") or [] if isinstance(t, dict)],
    )


def _parse_case(raw: dict[str, Any]) -> TestCase:
    duration = raw.get("duration_seconds")
    return TestCase(
        name=str(raw.get("name", "")),
        passed=bool(raw.get("passed", False)),
        asserts=int(raw.get("asserts", 0) or 0),
        failures=[_parse_failure(f) for f in raw.get("failures") or [] if isinstance(f, dict)],
        duration_seconds=float(duration) if duration is not None else None,
    )


def _parse_failure(raw: dict[str, Any]) -> AssertFailure:
    return AssertFailure(
        message=str(raw.get("message", "")),
        expected=str(raw.get("expected", "")),
        actual=str(raw.get("actual", "")),
        line=int(raw.get("line", 0) or 0),
    )


def _parse_summary(raw: Any) -> TestResultsSummary:
    if not isinstance(raw, dict):
        return TestResultsSummary()
    return TestResultsSummary(
        suites=int(raw.get("suites", 0) or 0),
        tests=int(raw.get("tests", 0) or 0),
        asserts=int(raw.get("asserts", 0) or 0),
        failures=int(raw.get("failures", 0) or 0),
        errors=int(raw.get("errors", 0) or 0),
        duration_seconds=float(raw.get("duration_seconds", 0.0) or 0.0),
    )
