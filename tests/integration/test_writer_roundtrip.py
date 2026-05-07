"""End-to-end writer round-trip tests.

Skipped by default. Runs only when:
  - the Windows bridge service is reachable at $BRIDGE_URL (default localhost:8765)
  - $TCKIT_INTEGRATION_PROJECT points at a TwinCAT .sln on the Windows machine

These tests modify the live project. Use a throwaway project, not the sample.
"""

from __future__ import annotations

import os

import pytest

from tckit.adapters.writers.automation_writer import AutomationWriter
from tckit.ports.types import POUType
from tckit.utils.bridge_client import BridgeClient

TEST_FB_NAME = "FB_TcKitWriterRoundtrip"
TEST_METHOD_NAME = "DoNothing"


def _bridge_or_skip() -> BridgeClient:
    client = BridgeClient()
    if not client.health():
        pytest.skip(f"Bridge service not reachable at {client.base_url}")
    return client


def _project_or_skip() -> str:
    project = os.getenv("TCKIT_INTEGRATION_PROJECT")
    if not project:
        pytest.skip("Set TCKIT_INTEGRATION_PROJECT to a .sln path to run this test.")
    return project


@pytest.fixture()
def writer(monkeypatch: pytest.MonkeyPatch) -> AutomationWriter:
    client = _bridge_or_skip()
    project = _project_or_skip()
    monkeypatch.setenv("PLC_PROJECT_PATH", project)
    return AutomationWriter(client=client)


def test_open_project(writer: AutomationWriter) -> None:
    project = os.environ["PLC_PROJECT_PATH"]
    result = writer.open_project(project)
    assert result.success, f"open_project failed: {result.error}"


def test_add_pou_then_method_then_update(writer: AutomationWriter) -> None:
    """Full round-trip: create FB, add a method, update its body."""
    decl = (
        "FUNCTION_BLOCK FB_TcKitWriterRoundtrip\n"
        "VAR_INPUT\nbEnable : BOOL;\nEND_VAR\n"
        "VAR_OUTPUT\nbDone : BOOL;\nEND_VAR\n"
    )
    add_fb = writer.add_pou(TEST_FB_NAME, POUType.FUNCTION_BLOCK, decl)
    assert add_fb.success, f"add_pou failed: {add_fb.error}"

    method_code = (
        "METHOD DoNothing : BOOL\nVAR_INPUT\nEND_VAR\n"
        "DoNothing := TRUE;\n"
    )
    add_method = writer.add_method(TEST_FB_NAME, TEST_METHOD_NAME, method_code)
    assert add_method.success, f"add_method failed: {add_method.error}"

    updated = (
        "METHOD DoNothing : BOOL\nVAR_INPUT\nEND_VAR\n"
        "DoNothing := FALSE;\n"
    )
    update = writer.update_pou_item(TEST_FB_NAME, TEST_METHOD_NAME, updated)
    assert update.success, f"update_pou_item failed: {update.error}"
