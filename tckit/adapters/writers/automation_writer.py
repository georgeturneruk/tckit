"""automation_writer — ProjectWriter adapter via Windows bridge → COM.

Calls bridge REST API → PowerShell harness → TcXaeShell.DTE.17.0 COM interface.
Requires the bridge service to be running on the Windows machine with XAE installed.
"""

from tckit.ports.types import POUType, Result
from tckit.ports.writer import ProjectWriter


class AutomationWriter(ProjectWriter):
    """Writes to TwinCAT project via the automation interface (bridge → COM)."""

    def open_project(self, solution_path: str) -> Result:
        raise NotImplementedError("automation_writer.open_project() not yet implemented")

    def create_project(self, name: str, path: str) -> Result:
        raise NotImplementedError("automation_writer.create_project() not yet implemented")

    def add_pou(self, name: str, pou_type: POUType, code: str) -> Result:
        raise NotImplementedError("automation_writer.add_pou() not yet implemented")

    def add_method(self, pou_name: str, method_name: str, code: str) -> Result:
        raise NotImplementedError("automation_writer.add_method() not yet implemented")

    def update_pou_item(self, pou_name: str, item_name: str, code: str) -> Result:
        raise NotImplementedError("automation_writer.update_pou_item() not yet implemented")
