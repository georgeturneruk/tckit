"""BuildRunner port — build, deploy, and runtime control."""

from abc import ABC, abstractmethod

from tckit.ports.types import BuildResult, BuildStatus, Result


class BuildRunner(ABC):
    """Build and deploy TwinCAT projects via the automation interface.

    Always ensure a successful build before deploying.
    Never deploy to a target without a successful build first.
    """

    @abstractmethod
    def build(self, project_path: str) -> BuildResult:
        """Build the TwinCAT project and return structured errors.

        :param project_path: Absolute path to the .sln or .tsproj file.
        :returns: BuildResult with success flag and structured error list.
        """
        ...

    @abstractmethod
    def deploy(self, target_ams_id: str) -> Result:
        """Deploy the built configuration to a target runtime.

        :param target_ams_id: AMS Net ID of the target (e.g. ``192.168.1.100.1.1``).
        """
        ...

    @abstractmethod
    def start_runtime(self, target_ams_id: str) -> Result:
        """Start or restart the TwinCAT runtime on a target.

        :param target_ams_id: AMS Net ID of the target.
        """
        ...

    @abstractmethod
    def get_status(self) -> BuildStatus:
        """Return the current build/deploy status."""
        ...
