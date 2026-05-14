"""End-to-end multi-PLC + library reference round-trip.

Skipped by default. Runs only when:
  - the Windows bridge service is reachable at $BRIDGE_URL (default localhost:8765)
  - $TCKIT_INTEGRATION_SLN_DIR is set to a writable directory where the test
    can author a fresh .sln. The test owns the directory and will create
    files under it.

The test exercises the full ADR-0009 surface:
  create_project → add_plc_project → add_pou (in both PLC projects) →
  save_plc_as_library (Library, install=True) → add_library_reference
  (from Tests to Library) → build (Tests) → assert build succeeds.
"""

from __future__ import annotations

import os
import shutil
import uuid
from pathlib import Path

import pytest

from tckit.adapters.builders.xae_com_builder import XaeComBuilder
from tckit.adapters.writers.automation_writer import AutomationWriter
from tckit.ports.types import POUType
from tckit.utils.bridge_client import BridgeClient

TESTS_PLC_NAME = "MultiPlcTests"
LIBRARY_FB_NAME = "FB_MultiPlcLibAdder"
TESTS_FB_NAME = "FB_MultiPlcLibConsumer"


def _bridge_or_skip() -> BridgeClient:
    client = BridgeClient()
    if not client.health():
        pytest.skip(f"Bridge service not reachable at {client.base_url}")
    return client


def _sln_dir_or_skip() -> Path:
    raw = os.getenv("TCKIT_INTEGRATION_SLN_DIR")
    if not raw:
        pytest.skip(
            "Set TCKIT_INTEGRATION_SLN_DIR to a writable directory to run this test."
        )
    return Path(raw)


@pytest.fixture()
def sln_workdir(monkeypatch: pytest.MonkeyPatch) -> Path:
    """A throwaway directory under TCKIT_INTEGRATION_SLN_DIR for this run."""
    root = _sln_dir_or_skip()
    root.mkdir(parents=True, exist_ok=True)
    work = root / f"adr0009-{uuid.uuid4().hex[:8]}"
    work.mkdir()
    yield work
    # Clean up the working dir after the test. The .library installed into
    # the system repo is left in place; the integration env is expected to
    # handle repo cleanup separately.
    shutil.rmtree(work, ignore_errors=True)


def test_end_to_end_multi_plc_with_library_reference(
    sln_workdir: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    client = _bridge_or_skip()
    sln_name = "MultiPlcRoundtrip"
    sln_path = sln_workdir / f"{sln_name}.sln"
    # create_project defaults the first PLC to "${sln_name}_Plc" to avoid
    # the sln/project name collision that has crashed TcXaeShell on
    # solution load.
    library_first_plc = f"{sln_name}_Plc"
    library_path = sln_workdir / f"{library_first_plc}.library"

    writer = AutomationWriter(client=client)
    builder = XaeComBuilder(client=client)

    # 1) Create the sln + Library PLC project.
    created = writer.create_project(sln_name, str(sln_workdir))
    assert created.success, f"create_project failed: {created.error}"

    # Subsequent calls scope to a specific PLC, so set the env path.
    monkeypatch.setenv("PLC_PROJECT_PATH", str(sln_path))

    # Add a tiny FB to the auto-created PLC project (so the library has content).
    library_fb_decl = (
        f"FUNCTION_BLOCK {LIBRARY_FB_NAME}\n"
        "VAR_INPUT\na : INT; b : INT;\nEND_VAR\n"
        "VAR_OUTPUT\nresult : INT;\nEND_VAR\n"
        "result := a + b;\n"
    )
    add_lib_fb = writer.add_pou(
        LIBRARY_FB_NAME,
        POUType.FUNCTION_BLOCK,
        library_fb_decl,
        plc_name=library_first_plc,
    )
    assert add_lib_fb.success, f"add_pou (library FB) failed: {add_lib_fb.error}"

    # 2) Add a second PLC project for the consumer.
    add_plc = writer.add_plc_project(str(sln_path), TESTS_PLC_NAME)
    assert add_plc.success, f"add_plc_project failed: {add_plc.error}"

    # 3) Save the library PLC as a library, installing it.
    saved = writer.save_plc_as_library(
        library_first_plc, str(library_path), install=True
    )
    assert saved.success, f"save_plc_as_library failed: {saved.error}"
    assert library_path.exists(), f"expected .library at {library_path}"

    # 4) Add a library reference from Tests to the just-installed library.
    ref = writer.add_library_reference(
        TESTS_PLC_NAME, library_first_plc
    )
    assert ref.success, (
        f"add_library_reference failed: {ref.error}. "
        "If 'library not found', the SaveAsLibrary distributor may differ "
        "from the 'Tc3 Project' default — adjust the distributor parameter."
    )

    # 5) Add a consumer FB to Tests that uses the library FB.
    consumer_decl = (
        f"FUNCTION_BLOCK {TESTS_FB_NAME}\n"
        f"VAR\nadder : {LIBRARY_FB_NAME};\nEND_VAR\n"
        "adder(a := 2, b := 3);\n"
    )
    add_consumer = writer.add_pou(
        TESTS_FB_NAME,
        POUType.FUNCTION_BLOCK,
        consumer_decl,
        plc_name=TESTS_PLC_NAME,
    )
    assert add_consumer.success, f"add_pou (consumer FB) failed: {add_consumer.error}"

    # 6) Build the Tests PLC project. Success here proves the library reference
    #    resolved and the installed library was usable.
    built = builder.build(str(sln_path), plc_name=TESTS_PLC_NAME)
    assert built.success, (
        f"Tests PLC build failed: {[e.message for e in built.errors]}"
    )


def test_end_to_end_add_library_placeholder(
    sln_workdir: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """add_library_placeholder lands a <PlaceholderReference> entry on disk.

    Uses ``Tc2_Utilities`` — a Beckhoff-shipped library that is not in the
    Standard PLC Template's default reference set, so the test doesn't
    collide with a pre-existing placeholder. The build step proves the
    placeholder resolves against the installed library.
    """
    client = _bridge_or_skip()
    sln_name = "PlaceholderRoundtrip"
    sln_path = sln_workdir / f"{sln_name}.sln"

    writer = AutomationWriter(client=client)
    builder = XaeComBuilder(client=client)

    created = writer.create_project(sln_name, str(sln_workdir))
    assert created.success, f"create_project failed: {created.error}"
    monkeypatch.setenv("PLC_PROJECT_PATH", str(sln_path))

    first_plc = f"{sln_name}_Plc"  # see create_project naming default
    ph = writer.add_library_placeholder(
        first_plc,
        "Tc2_Utilities",
        "Tc2_Utilities",
        distributor="Beckhoff Automation GmbH",
    )
    assert ph.success, f"add_library_placeholder failed: {ph.error}"

    # The placeholder reference should land in the .plcproj as a
    # <PlaceholderReference> element. Confirm by reading the file.
    plcproj_path = sln_workdir / first_plc / f"{first_plc}.plcproj"
    assert plcproj_path.exists(), f"expected .plcproj at {plcproj_path}"
    xml = plcproj_path.read_text(encoding="utf-8")
    assert '<PlaceholderReference Include="Tc2_Utilities">' in xml, (
        "expected <PlaceholderReference> entry to land on disk; got:\n"
        + xml
    )

    # The default PLC template already provides MAIN so the build has
    # something to compile. A successful build proves the placeholder
    # resolved against the installed library at compile time.
    built = builder.build(str(sln_path), plc_name=first_plc)
    assert built.success, (
        f"Placeholder-resolving build failed: {[e.message for e in built.errors]}"
    )
