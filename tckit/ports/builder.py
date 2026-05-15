"""BuildRunner port — build, deploy, runtime control, and symbol reads."""

from abc import ABC, abstractmethod

from tckit.ports.types import BuildResult, BuildStatus, Result


class BuildRunner(ABC):
    """Build and deploy TwinCAT projects via the automation interface.

    Always ensure a successful build before deploying.
    Never deploy to a target without a successful build first.

    Multi-project solutions: ``build`` and ``deploy`` accept an optional
    ``plc_name`` to scope the operation to a single PLC project; ``None``
    follows the standard resolution order. ``start_runtime`` and
    ``read_symbols`` are target-wide and take no ``plc_name``. See ADR-0005.
    """

    @abstractmethod
    def build(
        self, project_path: str, *, plc_name: str | None = None
    ) -> BuildResult:
        """Build the TwinCAT project and return structured errors.

        :param project_path: Absolute path to the .sln or .tsproj file.
        :param plc_name: PLC project to build; ``None`` follows the standard
            resolution order.
        :returns: BuildResult with success flag and structured error list.
        """
        ...

    @abstractmethod
    def deploy(
        self,
        target_ams_id: str,
        *,
        plc_name: str | None = None,
        boot_autostart: bool = True,
    ) -> Result:
        """Deploy the built configuration to a target runtime.

        :param target_ams_id: AMS Net ID of the target (e.g. ``192.168.1.100.1.1``).
        :param plc_name: PLC project to deploy; ``None`` follows the standard
            resolution order.
        :param boot_autostart: When ``True`` (default), the bridge enables
            ``BootProjectAutostart`` and regenerates the boot project so
            the PLC application runs as soon as the runtime reaches Run
            mode. Without this the PLC sits loaded-but-stopped and serves
            no ADS symbols, so subsequent ``run_tests`` polls will time
            out. Set ``False`` only if the consumer wants to control
            autostart explicitly.
        """
        ...

    @abstractmethod
    def start_runtime(self, target_ams_id: str) -> Result:
        """Start or restart the TwinCAT runtime on a target.

        :param target_ams_id: AMS Net ID of the target.
        """
        ...

    @abstractmethod
    def read_symbols(
        self, target_ams_id: str, paths: list[str]
    ) -> dict[str, str | None]:
        """Read PLC symbols by instance path on a running runtime.

        Universally useful when a fixture or skill wants to inspect the
        live state of a few specific symbols without spinning up TcUnit.
        Reads happen via ADS on the standard PLC runtime port; the target
        must already be in Run mode (use :meth:`deploy` or
        :meth:`start_runtime` first).

        Lives on ``BuildRunner`` rather than ``TestRunner`` because the
        operational dependency is the same as ``deploy`` and
        ``start_runtime``: a running runtime on the named target. The
        ``ReadSymbols`` parameter on ``/tcunit-run`` stays as a
        convenience for the suites-finished-then-probe pattern.

        :param target_ams_id: AMS Net ID of the target.
        :param paths: Symbol instance paths (e.g.
            ``["MAIN.suite.Tests[1].TestIsFailed"]``). Empty list returns
            an empty dict.
        :returns: Mapping of path -> string value, with ``None`` for any
            path that couldn't be resolved on the runtime. The full read
            attempt is best-effort; an unreadable symbol does not fail
            the call.
        """
        ...

    @abstractmethod
    def get_status(self) -> BuildStatus:
        """Return the current build/deploy status."""
        ...
