"""TestRunner port — TcUnit test execution and result parsing."""

from abc import ABC, abstractmethod

from tckit.ports.types import Result, TestResults


class TestRunner(ABC):
    """Run TcUnit tests and parse results.

    Two methods. ``run_tests`` blocks until the suites finish or the bridge's
    timeout fires; ``get_results`` reads the parsed XML the run wrote.

    Multi-project solutions (ADR-0005): both methods take a required
    ``target_ams_id`` (the AMS Net ID of the runtime to talk to) plus an
    optional ``plc_name`` (the PLC project hosting the suites). Both arguments
    are deliberately explicit because the IDE-equivalent workflow requires
    picking both — implicit "last deployed target" state would be brittle in
    an MCP session where calls may interleave.
    """

    @abstractmethod
    def run_tests(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> Result:
        """Trigger TcUnit test execution and block until completion.

        :param target_ams_id: AMS Net ID of the target runtime hosting the
            tests (e.g. ``192.168.1.100.1.1``).
        :param plc_name: PLC project hosting the TcUnit suites; ``None``
            follows the standard resolution order.
        """
        ...

    @abstractmethod
    def get_results(
        self, target_ams_id: str, *, plc_name: str | None = None
    ) -> TestResults:
        """Return parsed test results after run_tests() succeeds.

        :param target_ams_id: AMS Net ID of the target runtime the tests
            were executed on.
        :param plc_name: PLC project hosting the TcUnit suites; ``None``
            follows the standard resolution order.
        :returns: TestResults with suite/test/pass/fail hierarchy plus
            assertion-failure detail.
        """
        ...
