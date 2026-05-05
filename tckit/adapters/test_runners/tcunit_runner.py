"""tcunit_runner — TestRunner adapter for TcUnit test execution.

Triggers test run via bridge, polls for XML output file creation,
then parses XML (suite/test/pass/fail/message) into structured TestResults.
"""

from tckit.ports.test_runner import TestRunner
from tckit.ports.types import Result, TestResults, TestStatus


class TcUnitRunner(TestRunner):
    """Runs TcUnit tests and parses XML results into structured JSON."""

    def run_tests(self) -> Result:
        raise NotImplementedError("tcunit_runner.run_tests() not yet implemented")

    def wait_complete(self, timeout_seconds: int = 60) -> Result:
        raise NotImplementedError("tcunit_runner.wait_complete() not yet implemented")

    def get_results(self) -> TestResults:
        raise NotImplementedError("tcunit_runner.get_results() not yet implemented")

    def get_status(self) -> TestStatus:
        raise NotImplementedError("tcunit_runner.get_status() not yet implemented")
