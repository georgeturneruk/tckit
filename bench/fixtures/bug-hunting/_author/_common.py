"""Shared scaffolding for bug-hunting fixture authoring scripts.

Each `author_<id>.py` drives the bridge through the standard
multi-PLC + library + TcUnit chain. The boilerplate (sln + Tests
sibling + TcUnit placeholder with xUnitEnablePublish=TRUE
+ save/install + reference + build) is identical across fixtures;
only the bugged FB content and the consumer FB differ. This module
owns the boilerplate; each author script owns the FB authoring
calls.

See ADR-0007 section "Fixture layout" and `author_B1.py` for the reference
shape.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tckit.adapters.builders.xae_com_builder import XaeComBuilder  # noqa: E402
from tckit.adapters.writers.automation_writer import AutomationWriter  # noqa: E402
from tckit.ports.types import POUType, Result  # noqa: E402
from tckit.templates import install_twincat_claude_md  # noqa: E402
from tckit.utils.bridge_client import BridgeClient  # noqa: E402


TCUNIT_DISTRIBUTOR = "www.tcunit.org"


@dataclass
class FixtureScaffold:
    writer: AutomationWriter
    builder: XaeComBuilder
    fixture_dir: Path
    sln_name: str
    sln_path: Path
    library_plc: str  # auto-named ${sln_name}_Plc by create_project
    tests_plc: str


def check(label: str, result: Result) -> None:
    """Print OK/FAIL line for a Result, exit non-zero on failure."""
    if not result.success:
        print(f"FAIL [{label}]: {result.error}", file=sys.stderr)
        sys.exit(1)
    print(f"OK   [{label}]")


# Internal alias for the rest of this module — keeps callers using
# the public name.
_check = check


def _wipe_fixture(fixture_dir: Path, force: bool) -> None:
    """Clear generated content, keep static support files (CLAUDE.md, etc.)."""
    if not fixture_dir.exists():
        return
    keepers = {"CLAUDE.md", "TASK.md", "README.md", "bench-config.json"}
    generated = [p for p in fixture_dir.iterdir() if p.name not in keepers]
    if not generated:
        return
    if not force:
        names = ", ".join(sorted(p.name for p in generated))
        print(
            f"Fixture dir {fixture_dir} already contains generated content "
            f"({names}). Pass --force to overwrite.",
            file=sys.stderr,
        )
        sys.exit(2)
    for entry in generated:
        if entry.is_dir():
            shutil.rmtree(entry, ignore_errors=True)
        else:
            entry.unlink()


def parse_args(description: str) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=description)
    parser.add_argument(
        "--force",
        action="store_true",
        help="Remove generated content in the fixture dir before re-authoring.",
    )
    return parser.parse_args()


def scaffold_fixture(
    *,
    fixture_dir: Path,
    sln_name: str,
    tests_plc: str,
    force: bool,
) -> FixtureScaffold:
    """Wipe the fixture dir, create the sln + Tests sibling, return handles.

    After this, the caller adds whatever library/tests FBs the fixture
    needs via the returned scaffold, then calls ``finalise_fixture``.
    """
    client = BridgeClient()
    if not client.health():
        print(f"Bridge not reachable at {client.base_url}", file=sys.stderr)
        sys.exit(1)

    fixture_dir.mkdir(parents=True, exist_ok=True)
    _wipe_fixture(fixture_dir, force=force)

    sln_path = fixture_dir / f"{sln_name}.sln"
    library_plc = f"{sln_name}_Plc"

    # Target this sln explicitly on every adapter call (headless authoring).
    writer = AutomationWriter(client=client, project_path=str(sln_path))
    builder = XaeComBuilder(client=client, project_path=str(sln_path))

    _check("create_project", writer.create_project(sln_name, str(fixture_dir)))

    _check(
        f"add_plc_project({tests_plc})",
        writer.add_plc_project(str(sln_path), tests_plc),
    )

    # TcUnit placeholder lands here, before any user-added POUs, so that a
    # suite FB declaring `EXTENDS TcUnit.FB_TestSuite` resolves the namespace
    # at add time. add_library_reference for the library-under-test stays in
    # finalise_fixture because it depends on the .library produced by
    # save_plc_as_library, which can only happen after the library's POUs
    # have been added.
    #
    # xUnitEnablePublish flips on the publisher that writes the per-test
    # JUnit-style XML at %TC_BOOTPRJPATH%tcunit_xunit_testresults.xml; it
    # defaults FALSE on the library, so without this override the suite
    # finishes correctly but no XML lands and /tcunit-run can only return
    # live counters or whatever symbol Probes the caller asked for. The
    # GVL_Param_TcUnit grouping is the host parameter-list name the IDE
    # writes (uppercased on disk by the writer).
    _check(
        f"add_library_placeholder({tests_plc} -> TcUnit)",
        writer.add_library_placeholder(
            tests_plc,
            "TcUnit",
            "TcUnit",
            distributor=TCUNIT_DISTRIBUTOR,
            parameters={"GVL_Param_TcUnit": {"xUnitEnablePublish": "TRUE"}},
        ),
    )

    # Drop the TwinCAT CLAUDE.md template tree into the fixture root.
    # Existing CLAUDE.md (e.g. fixture-specific notes from older fixtures)
    # is preserved; topic files are written underneath unconditionally.
    template_written = install_twincat_claude_md(fixture_dir, overwrite=force)
    if template_written:
        print(f"OK   install_twincat_claude_md ({len(template_written)} files)")

    return FixtureScaffold(
        writer=writer,
        builder=builder,
        fixture_dir=fixture_dir,
        sln_name=sln_name,
        sln_path=sln_path,
        library_plc=library_plc,
        tests_plc=tests_plc,
    )


def finalise_fixture(scaffold: FixtureScaffold) -> None:
    """Save the library, add the library reference, then build the Tests
    PLC. Fails the script on any non-success step.

    Run this after the script has added every library/tests FB it needs.
    """
    writer = scaffold.writer
    library_artefact = scaffold.fixture_dir / f"{scaffold.library_plc}.library"

    _check(
        f"save_plc_as_library({scaffold.library_plc})",
        writer.save_plc_as_library(
            scaffold.library_plc, str(library_artefact), install=True
        ),
    )
    if not library_artefact.exists():
        print(
            f"FAIL: expected .library at {library_artefact} but it is missing.",
            file=sys.stderr,
        )
        sys.exit(1)
    print(f"OK   .library produced at {library_artefact}")

    _check(
        f"add_library_reference({scaffold.tests_plc} -> {scaffold.library_plc})",
        writer.add_library_reference(scaffold.tests_plc, scaffold.library_plc),
    )

    build_result = scaffold.builder.build(
        str(scaffold.sln_path), plc_name=scaffold.tests_plc
    )
    if not build_result.success:
        print(f"FAIL [build({scaffold.tests_plc})]:", file=sys.stderr)
        for err in build_result.errors:
            print(f"  - {err.file}:{err.line}: {err.message}", file=sys.stderr)
        sys.exit(1)
    print(f"OK   build({scaffold.tests_plc}) — references resolved + built clean")
    print()
    print("Authoring complete. Next:")
    print(f"  - inspect generated tree under {scaffold.fixture_dir}")
    print("  - commit produced .sln/.plcproj/.TcPOU files (the .library")
    print("    artefact is gitignored)")
