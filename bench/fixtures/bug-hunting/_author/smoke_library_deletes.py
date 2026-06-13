"""Round-trip smoke for ADR-0013 library deletes (Wave 4) against the bridge.

Builds a two-PLC sln (one library source, one consumer), installs the
source as a library, then exercises:

- ``add_library_reference`` + ``delete_library_reference`` (3-arg
  RemoveReference path).
- ``add_library_placeholder`` with parameters + ``delete_placeholder``
  (1-arg RemoveReference path).

After each delete, the consumer ``.plcproj`` is parsed to confirm the
``<LibraryReference>`` / ``<PlaceholderReference>`` element has been
stripped. The placeholder case also probes the open ADR-0013 question:
does ``RemoveReference`` strip the orphan ``<Parameters>`` block, or
does it survive?

Prereqs: bridge reachable; TcXaeShell available. Note: this smoke
installs ``SmokeLibSrc_Plc`` into the local TwinCAT system library
repository (install=True is required for ``add_library_reference`` to
resolve). Clean up via XAE's library repository view if you want it
gone afterwards.

Exits 0 on success, non-zero on the first failing assertion.
"""

from __future__ import annotations

import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tckit.adapters.writers.automation_writer import AutomationWriter  # noqa: E402
from tckit.ports.types import POUType, Result  # noqa: E402
from tckit.utils.bridge_client import BridgeClient  # noqa: E402

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "_smoke-library-deletes"
SLN_NAME = "SmokeLibDeletes"
CONSUMER_PLC = f"{SLN_NAME}_Plc"
SOURCE_PLC = "SmokeLibSrc_Plc"
PLACEHOLDER_NAME = "SmokeLibSrcPH"

FB_LIB_DECL = """\
FUNCTION_BLOCK FB_Lib
VAR_INPUT
    value : INT;
END_VAR
VAR_OUTPUT
    result : INT;
END_VAR
result := value * 2;
"""


def _fail(msg: str) -> None:
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def _ok(label: str) -> None:
    print(f"OK   {label}")


def _check(label: str, result: Result) -> None:
    if not result.success:
        _fail(f"[{label}] {result.error}")
    _ok(label)


def _wipe_fixture(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path, ignore_errors=True)


def _find_consumer_plcproj() -> Path:
    """Locate the consumer .plcproj for direct XML inspection."""
    candidates = list(FIXTURE_DIR.rglob(f"{CONSUMER_PLC}.plcproj"))
    if not candidates:
        _fail(f"Consumer .plcproj not found under {FIXTURE_DIR}.")
    if len(candidates) > 1:
        _fail(
            f"Ambiguous consumer .plcproj: {[str(p) for p in candidates]}"
        )
    return candidates[0]


def _parse_plcproj_refs(path: Path) -> dict:
    """Return library/placeholder reference names + any parameter elements
    present in the consumer .plcproj.
    """
    tree = ET.parse(path)
    root = tree.getroot()
    # Strip the MSBuild namespace from every tag so XPath queries don't
    # need to carry the prefix.
    for el in root.iter():
        if "}" in el.tag:
            el.tag = el.tag.split("}", 1)[1]

    libraries = [el.get("Include", "") for el in root.iter("LibraryReference")]
    placeholders = [el.get("Include", "") for el in root.iter("PlaceholderReference")]
    placeholder_params: dict[str, int] = {}
    for ph in root.iter("PlaceholderReference"):
        name = ph.get("Include", "")
        params = ph.find("Parameters")
        if params is not None:
            placeholder_params[name] = len(list(params))
    return {
        "libraries": libraries,
        "placeholders": placeholders,
        "placeholder_params": placeholder_params,
    }


def main() -> int:
    client = BridgeClient()
    if not client.health():
        _fail(f"Bridge not reachable at {client.base_url}. Start the bridge first.")

    writer = AutomationWriter(client=client)

    _wipe_fixture(FIXTURE_DIR)
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)

    _check("create_project", writer.create_project(SLN_NAME, str(FIXTURE_DIR)))
    sln_path = FIXTURE_DIR / f"{SLN_NAME}.sln"
    _check("open_project", writer.open_project(str(sln_path)))

    _check(
        f"add_plc_project({SOURCE_PLC})",
        writer.add_plc_project(str(sln_path), SOURCE_PLC),
    )
    _check(
        f"add_pou(FB_Lib in {SOURCE_PLC})",
        writer.add_pou(
            "FB_Lib", POUType.FUNCTION_BLOCK, FB_LIB_DECL, plc_name=SOURCE_PLC
        ),
    )

    library_artefact = FIXTURE_DIR / f"{SOURCE_PLC}.library"
    _check(
        f"save_plc_as_library({SOURCE_PLC}, install=True)",
        writer.save_plc_as_library(
            SOURCE_PLC, str(library_artefact), install=True, overwrite=True
        ),
    )

    # --- Library reference round-trip --------------------------------------

    _check(
        "add_library_reference",
        writer.add_library_reference(
            CONSUMER_PLC, SOURCE_PLC, distributor="Tc3 Project"
        ),
    )

    # XAE stores the library reference as a combined identity in the
    # Include= attribute, e.g. "SmokeLibSrc_Plc,newest,Tc3 Project". Match
    # by substring so the smoke is tolerant of the exact composite form.
    plcproj = _find_consumer_plcproj()
    refs = _parse_plcproj_refs(plcproj)
    if not any(SOURCE_PLC in lib for lib in refs["libraries"]):
        _fail(
            f"AddLibrary didn't land: consumer .plcproj libraries = "
            f"{refs['libraries']}"
        )
    _ok(f"AddLibrary wrote <LibraryReference> mentioning {SOURCE_PLC!r}")

    _check(
        "delete_library_reference",
        writer.delete_library_reference(
            CONSUMER_PLC, SOURCE_PLC, distributor="Tc3 Project"
        ),
    )
    refs = _parse_plcproj_refs(plcproj)
    if any(SOURCE_PLC in lib for lib in refs["libraries"]):
        _fail(
            f"delete_library_reference left a ghost: libraries = "
            f"{refs['libraries']}"
        )
    _ok("round-trip: <LibraryReference> stripped from consumer .plcproj")

    # --- Placeholder round-trip + parameters survival probe ----------------

    placeholder_params = {
        "GVL_SmokeParams": {"MyKey": "TRUE"}
    }
    _check(
        "add_library_placeholder with parameters",
        writer.add_library_placeholder(
            CONSUMER_PLC,
            PLACEHOLDER_NAME,
            SOURCE_PLC,
            distributor="Tc3 Project",
            parameters=placeholder_params,
        ),
    )

    refs = _parse_plcproj_refs(plcproj)
    if PLACEHOLDER_NAME not in refs["placeholders"]:
        _fail(
            f"AddPlaceholder didn't land: placeholders = {refs['placeholders']}"
        )
    if refs["placeholder_params"].get(PLACEHOLDER_NAME, 0) < 1:
        _fail(
            f"AddPlaceholder didn't materialise <Parameters>: "
            f"{refs['placeholder_params']}"
        )
    _ok(
        f"AddPlaceholder wrote <PlaceholderReference Include='{PLACEHOLDER_NAME}'>"
        f" with {refs['placeholder_params'][PLACEHOLDER_NAME]} parameter(s)"
    )

    _check(
        "delete_placeholder",
        writer.delete_placeholder(CONSUMER_PLC, PLACEHOLDER_NAME),
    )

    refs = _parse_plcproj_refs(plcproj)
    if PLACEHOLDER_NAME in refs["placeholders"]:
        _fail(
            f"delete_placeholder left a ghost: placeholders = "
            f"{refs['placeholders']}"
        )
    _ok("round-trip: <PlaceholderReference> stripped from consumer .plcproj")

    # Open question from ADR-0013: does RemoveReference also clean up the
    # <Parameters> block? Anything left here is the orphan we'd need the
    # ConsumeXml('<RemoveReferences>...') fallback for.
    leaked = refs["placeholder_params"].get(PLACEHOLDER_NAME, 0)
    if leaked > 0:
        print(
            f"NOTE: RemoveReference left {leaked} orphan <Parameter> "
            f"element(s) behind. Wire the ConsumeXml fallback (see ADR-0013).",
            file=sys.stderr,
        )
    else:
        _ok("RemoveReference also stripped the orphan <Parameters> block")

    print()
    print("Library-side delete smoke complete: wave 4 round-trip clean.")
    print(
        f"NOTE: {SOURCE_PLC} was installed in the system library repository; "
        "remove it from XAE's Library Repository view if you want it gone."
    )
    print(f"Clean up the throwaway sln with: rm -rf {FIXTURE_DIR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
