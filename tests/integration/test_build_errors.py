"""End-to-end build + structured-error tests.

Skipped unless bridge is reachable and TCKIT_INTEGRATION_PROJECT is set.
Mirrors the writer round-trip test setup. Uses the same throwaway project.
"""

from __future__ import annotations

import os

import pytest

from tckit.adapters.builders.xae_com_builder import XaeComBuilder
from tckit.adapters.writers.automation_writer import AutomationWriter
from tckit.ports.types import POUType
from tckit.utils.bridge_client import BridgeClient

BAD_FB_NAME = "FB_TcKitBuildErrorProbe"


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


def test_clean_build_succeeds() -> None:
    client = _bridge_or_skip()
    project = _project_or_skip()

    builder = XaeComBuilder(client=client)
    result = builder.build(project)
    assert result.success, f"clean build failed: {[e.message for e in result.errors]}"


def test_broken_fb_produces_structured_error() -> None:
    client = _bridge_or_skip()
    project = _project_or_skip()

    writer = AutomationWriter(client=client, project_path=project)
    builder = XaeComBuilder(client=client)

    # Add a deliberately broken FB referencing an undeclared variable.
    bad_decl = (
        "FUNCTION_BLOCK FB_TcKitBuildErrorProbe\n"
        "VAR_OUTPUT\nbResult : BOOL;\nEND_VAR\n"
        "bResult := nUndeclaredVar > 0;\n"
    )
    add = writer.add_pou(BAD_FB_NAME, POUType.FUNCTION_BLOCK, bad_decl)
    assert add.success, f"setup add_pou failed: {add.error}"

    result = builder.build(project)
    assert not result.success, "expected build to fail"
    assert any("nUndeclaredVar" in (e.message or "") for e in result.errors), (
        f"expected an error mentioning nUndeclaredVar; got {[e.message for e in result.errors]}"
    )
