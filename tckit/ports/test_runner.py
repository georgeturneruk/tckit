"""TestRunner port — TcUnit test execution and result parsing."""

from abc import ABC, abstractmethod

from tckit.ports.types import Result, TestResults, TestStatus


class TestRunner(ABC):
    """Run TcUnit tests and parse results.

    Typical call sequence:
      run_tests() → wait_complete(timeout) → get_results()
    """

    @abstractmethod
    def run_tests(self) -> Result:
        """Trigger TcUnit test execution on the target runtime."""
        ...

    @abstractmethod
    def wait_complete(self, timeout_seconds: int = 60) -> Result:
        """Block until tests complete or timeout is reached.

        :param timeout_seconds: Maximum seconds to wait for completion.
        """
        ...

    @abstractmethod
    def get_results(self) -> TestResults:
        """Return parsed test results after wait_complete() succeeds.

        :returns: TestResults with suite/test/pass/fail/message hierarchy.
        """
        ...

    @abstractmethod
    def get_status(self) -> TestStatus:
        """Return the current test execution status."""
        ...
