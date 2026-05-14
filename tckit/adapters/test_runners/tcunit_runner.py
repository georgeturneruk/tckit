"""tcunit_runner — TestRunner adapter for TcUnit test execution.

Triggers a test run via the bridge (which blocks until the suites finish or
the bridge's timeout fires), then parses the TcUnit XML output into a
``TestResults``. ADR-0006 covers the full design; the bodies are filled in by
the Phase 2 PR, this file is the stub the port shrink (Phase 0) leaves behind.
"""

from tckit.ports.test_runner import TestRunner
from tckit.ports.types import Result, TestResults


class TcUnitRunner(TestRunner):
    """Runs TcUnit tests and parses XML results into structured JSON."""

    def run_tests(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> Result:
        raise NotImplementedError("tcunit_runner.run_tests() not yet implemented")

    def get_results(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> TestResults:
        raise NotImplementedError("tcunit_runner.get_results() not yet implemented")
