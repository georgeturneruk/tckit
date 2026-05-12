"""tcunit_runner — TestRunner adapter for TcUnit test execution.

Triggers test run via bridge, polls for XML output file creation,
then parses XML (suite/test/pass/fail/message) into structured TestResults.

ADR-0005 adds ``plc_name`` and ``target_ams_id`` to the signatures so
ADR-0006 (the implementation of these methods against the TcUnit harness)
lands without further interface churn. The bodies remain stubbed here.
"""

from tckit.ports.test_runner import TestRunner
from tckit.ports.types import Result, TestResults, TestStatus


class TcUnitRunner(TestRunner):
    """Runs TcUnit tests and parses XML results into structured JSON."""

    def run_tests(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> Result:
        raise NotImplementedError("tcunit_runner.run_tests() not yet implemented")

    def wait_complete(
        self,
        target_ams_id: str,
        timeout_seconds: int = 60,
        *,
        plc_name: str | None = None,
    ) -> Result:
        raise NotImplementedError("tcunit_runner.wait_complete() not yet implemented")

    def get_results(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> TestResults:
        raise NotImplementedError("tcunit_runner.get_results() not yet implemented")

    def get_status(self) -> TestStatus:
        raise NotImplementedError("tcunit_runner.get_status() not yet implemented")
