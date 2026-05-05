"""xae_com_builder — BuildRunner adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell automation interface.
Returns structured build errors as JSON with file/line/message/severity.
"""

from tckit.ports.builder import BuildRunner
from tckit.ports.types import BuildResult, BuildStatus, Result


class XaeComBuilder(BuildRunner):
    """Builds and deploys TwinCAT projects via the XAE COM automation interface."""

    def build(self, project_path: str) -> BuildResult:
        raise NotImplementedError("xae_com_builder.build() not yet implemented")

    def deploy(self, target_ams_id: str) -> Result:
        raise NotImplementedError("xae_com_builder.deploy() not yet implemented")

    def start_runtime(self, target_ams_id: str) -> Result:
        raise NotImplementedError("xae_com_builder.start_runtime() not yet implemented")

    def get_status(self) -> BuildStatus:
        raise NotImplementedError("xae_com_builder.get_status() not yet implemented")
