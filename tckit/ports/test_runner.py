"""TestRunner port — TcUnit test execution and result parsing."""

from abc import ABC, abstractmethod

from tckit.ports.types import Result, TestResults, TestStatus


class TestRunner(ABC):
    """Run TcUnit tests and parse results.

    Typical call sequence:
      run_tests(target, plc_name=...) → wait_complete(timeout) → get_results()

    Multi-project solutions (ADR-0005): every test-execution method takes a
    required ``target_ams_id`` (the AMS Net ID of the runtime to talk to)
    plus an optional ``plc_name`` (the PLC project hosting the suites). Both
    arguments are deliberately explicit because the IDE-equivalent workflow
    requires picking both — implicit "last deployed target" state would be
    brittle in an MCP session where calls may interleave.
    """

    @abstractmethod
    def run_tests(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> Result:
        """Trigger TcUnit test execution on the target runtime.

        :param target_ams_id: AMS Net ID of the target runtime hosting the
            tests (e.g. ``192.168.1.100.1.1``).
        :param plc_name: PLC project hosting the TcUnit suites; ``None``
            follows the standard resolution order.
        """
        ...

    @abstractmethod
    def wait_complete(
        self,
        target_ams_id: str,
        timeout_seconds: int = 60,
        *,
        plc_name: str | None = None,
    ) -> Result:
        """Block until tests complete or timeout is reached.

        :param target_ams_id: AMS Net ID of the target runtime.
        :param timeout_seconds: Maximum seconds to wait for completion.
        :param plc_name: PLC project hosting the TcUnit suites; ``None``
            follows the standard resolution order.
        """
        ...

    @abstractmethod
    def get_results(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> TestResults:
        """Return parsed test results after wait_complete() succeeds.

        :param target_ams_id: AMS Net ID of the target runtime the tests
            were executed on.
        :param plc_name: PLC project hosting the TcUnit suites; ``None``
            follows the standard resolution order.
        :returns: TestResults with suite/test/pass/fail/message hierarchy.
        """
        ...

    @abstractmethod
    def get_status(self) -> TestStatus:
        """Return the current test execution status."""
        ...
