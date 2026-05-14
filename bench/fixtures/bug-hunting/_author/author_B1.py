"""Author the B1 off-by-one bug-hunting fixture against a live bridge.

Drives the ADR-0009 multi-PLC + library chain end-to-end:

    create_project (Library PLC)
        -> add_plc_project (Tests PLC, sibling)
        -> add_pou (FB_RollingAverage in Library, with the seeded bug)
        -> add_pou (FB_RollingAverageConsumer in Tests, calls FB_RollingAverage)
        -> save_plc_as_library (Library, install=True)
        -> add_library_reference (Tests -> Library, defaults)
        -> build (Tests)

If the build succeeds, the multi-PLC + library plumbing introduced in
ADR-0009 round-trips against this 4026 install. Phase C0's stated
purpose is exactly that smoke-validation.

Bench runs reset the fixture from git (the committed .plcproj/.TcPOU
files are what each session starts from); this script exists to
*regenerate* the committed shape, not to run between sessions. Run it
once, commit the produced files, then leave the script in place for
future re-authoring rounds (e.g. when adjusting the seeded bug).

Usage:

    python bench/fixtures/bug-hunting/_author/author_B1.py [--force]

`--force` removes any existing fixture content under the target dir
before re-authoring; without it, the script refuses to overwrite a
non-empty fixture dir.

Requires the bridge service at $BRIDGE_URL (default localhost:8765)
and a TwinCAT 4026 install. Saves the active XAE solution first as a
defensive measure (Solution.SaveAll equivalent via /open round-trip).
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B1-off-by-one"

SLN_NAME = "B1RollingAverage"
# `create_project` names the first PLC after the sln, so the library PLC
# inherits SLN_NAME — both as its tree name and its library namespace.
LIBRARY_PLC = SLN_NAME
TESTS_PLC = "RollingAverageTests"
LIBRARY_FB = "FB_RollingAverage"
CONSUMER_FB = "FB_RollingAverageConsumer"

if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tckit.adapters.builders.xae_com_builder import XaeComBuilder  # noqa: E402
from tckit.adapters.writers.automation_writer import AutomationWriter  # noqa: E402
from tckit.ports.types import POUType, Result  # noqa: E402
from tckit.utils.bridge_client import BridgeClient  # noqa: E402


LIBRARY_FB_CODE = """\
FUNCTION_BLOCK FB_RollingAverage
VAR
    samples : ARRAY[0..15] OF INT;
    sampleCount : INT := 8;
    nextIndex : INT;
END_VAR
"""

# Off-by-one bug seeded in Step: `FOR i := 1 TO sampleCount` reads
# samples[1..8] when it should read samples[0..7]. samples[8..15] are
# zero-initialised (never written), so a constant-input stream of 10s
# yields sum = 70 instead of 80 and average = 8 instead of 10.
STEP_METHOD_CODE = """\
METHOD Step : INT
VAR_INPUT
    sample : INT;
END_VAR
VAR
    sum : DINT;
    i : INT;
END_VAR
samples[nextIndex] := sample;
nextIndex := (nextIndex + 1) MOD sampleCount;
sum := 0;
FOR i := 1 TO sampleCount DO
    sum := sum + samples[i];
END_FOR
Step := DINT_TO_INT(sum / sampleCount);
"""

# Consumer FB lives in Tests and exists so the build resolves the
# library reference end-to-end. Once TcUnit wiring lands (follow-up),
# this FB graduates into a proper FB_TestSuite descendant.
CONSUMER_FB_CODE = f"""\
FUNCTION_BLOCK FB_RollingAverageConsumer
VAR
    adder : {LIBRARY_PLC}.FB_RollingAverage;
    lastResult : INT;
END_VAR
lastResult := adder.Step(sample := 10);
"""


def _check(label: str, result: Result) -> None:
    if not result.success:
        print(f"FAIL [{label}]: {result.error}", file=sys.stderr)
        sys.exit(1)
    print(f"OK   [{label}]")


def _wipe_fixture(force: bool) -> None:
    """Clear generated content (sln + subdirs), keep static support files."""
    if not FIXTURE_DIR.exists():
        return
    keepers = {"CLAUDE.md", "TASK.md", "README.md"}
    generated = [p for p in FIXTURE_DIR.iterdir() if p.name not in keepers]
    if not generated:
        return
    if not force:
        names = ", ".join(sorted(p.name for p in generated))
        print(
            f"Fixture dir {FIXTURE_DIR} already contains generated content "
            f"({names}). Pass --force to overwrite.",
            file=sys.stderr,
        )
        sys.exit(2)
    for entry in generated:
        if entry.is_dir():
            shutil.rmtree(entry, ignore_errors=True)
        else:
            entry.unlink()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--force",
        action="store_true",
        help="Remove generated content in the fixture dir before re-authoring.",
    )
    args = parser.parse_args()

    client = BridgeClient()
    if not client.health():
        print(f"Bridge not reachable at {client.base_url}", file=sys.stderr)
        return 1

    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)
    _wipe_fixture(force=args.force)

    writer = AutomationWriter(client=client)
    builder = XaeComBuilder(client=client)

    sln_path = FIXTURE_DIR / f"{SLN_NAME}.sln"
    library_artefact = FIXTURE_DIR / f"{LIBRARY_PLC}.library"

    # 1) create_project — sln + first PLC (Library).
    _check(
        "create_project",
        writer.create_project(SLN_NAME, str(FIXTURE_DIR)),
    )
    # Per ADR-0005, downstream calls scope to a PLC name explicitly via
    # plc_name=, so PLC_PROJECT_PATH is the only solution-level env var
    # we rely on for subsequent calls.
    import os

    os.environ["PLC_PROJECT_PATH"] = str(sln_path)

    # The first PLC project auto-created by create_project takes the
    # sln name, not the LIBRARY_PLC name. Tests becomes a sibling under
    # the same TIPC node via add_plc_project.
    first_plc = SLN_NAME

    # 2) add_plc_project — Tests sibling.
    _check(
        "add_plc_project(Tests)",
        writer.add_plc_project(str(sln_path), TESTS_PLC),
    )

    # 3) add_pou — FB_RollingAverage (with bug) under the first PLC.
    _check(
        "add_pou(FB_RollingAverage)",
        writer.add_pou(
            LIBRARY_FB,
            POUType.FUNCTION_BLOCK,
            LIBRARY_FB_CODE,
            plc_name=first_plc,
        ),
    )
    _check(
        "add_method(Step)",
        writer.add_method(
            LIBRARY_FB,
            "Step",
            STEP_METHOD_CODE,
            plc_name=first_plc,
        ),
    )

    # 4) save_plc_as_library — produce + install the .library artefact.
    _check(
        "save_plc_as_library(Library)",
        writer.save_plc_as_library(
            first_plc, str(library_artefact), install=True
        ),
    )
    if not library_artefact.exists():
        print(
            f"FAIL: expected .library at {library_artefact} but it is missing.",
            file=sys.stderr,
        )
        return 1
    print(f"OK   .library produced at {library_artefact}")

    # 5) add_library_reference — Tests -> Library (defaults: version '*',
    # distributor 'Tc3 Project'). This is the call that validates the
    # spike-by-implementation defaults in ADR-0009.
    _check(
        "add_library_reference(Tests -> Library)",
        writer.add_library_reference(TESTS_PLC, first_plc),
    )

    # 6) add_pou — consumer FB in Tests that calls into the library.
    _check(
        "add_pou(FB_RollingAverageConsumer)",
        writer.add_pou(
            CONSUMER_FB,
            POUType.FUNCTION_BLOCK,
            CONSUMER_FB_CODE,
            plc_name=TESTS_PLC,
        ),
    )

    # 7) build — Tests resolves library reference and builds against the
    # installed library. Success here is the smoke gate.
    build_result = builder.build(str(sln_path), plc_name=TESTS_PLC)
    if not build_result.success:
        print("FAIL [build(Tests)]:", file=sys.stderr)
        for err in build_result.errors:
            print(f"  - {err.file}:{err.line}: {err.message}", file=sys.stderr)
        return 1
    print("OK   build(Tests) — library reference resolved + built clean")
    print()
    print("Authoring complete. Next:")
    print(f"  - inspect generated tree under {FIXTURE_DIR}")
    print("  - commit produced .sln/.plcproj/.TcPOU files (the .library")
    print("    artefact is gitignored)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
