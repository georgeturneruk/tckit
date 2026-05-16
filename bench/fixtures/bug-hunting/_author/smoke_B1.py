"""End-to-end runtime smoke for the B1 off-by-one fixture.

Drives the closed-loop story ADR-0007 promises, against a live 4026
install + reachable TARGET_AMS_ID:

    save_plc_as_library (Library)
        -> build (Tests, resolves freshly installed .library)
        -> deploy + start_runtime
        -> run_tests + get_results
        -> assert RED (the seeded off-by-one fails AverageOfConstantStream)
        -> update_method_body_patch on FB_RollingAverage.Step
           (FOR i := 1 -> FOR i := 0)
        -> save_plc_as_library again
        -> build + deploy + start_runtime + run_tests + get_results
        -> assert GREEN (failures == 0)

This is a manual smoke runner, not part of `bench/run.py`. The bench
harness changes from ADR-0007 section "Bench harness changes" are deferred.

Prerequisites:
- Bridge service reachable at $BRIDGE_URL (default localhost:8765).
- $TARGET_AMS_ID set to a reachable target runtime.
- TcUnit installed in the System library repo (distributor `www.tcunit.org`).
- B1 fixture in its committed (seeded-bug) state. Re-running this script
  mutates FB_RollingAverage.Step via the writer; the closing reset hint
  printed at the end shows the operator how to revert.

Exits 0 on red->patch->green, non-zero on any unexpected step.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tckit.adapters.builders.xae_com_builder import XaeComBuilder  # noqa: E402
from tckit.adapters.test_runners.tcunit_runner import TcUnitRunner  # noqa: E402
from tckit.adapters.writers.automation_writer import AutomationWriter  # noqa: E402
from tckit.ports.types import Result  # noqa: E402
from tckit.utils.bridge_client import BridgeClient  # noqa: E402

FIXTURE_DIR = REPO_ROOT / "bench" / "fixtures" / "bug-hunting" / "B1-off-by-one"
SLN_PATH = FIXTURE_DIR / "B1RollingAverage.sln"
LIBRARY_PLC = "B1RollingAverage_Plc"
TESTS_PLC = "RollingAverageTests"
LIBRARY_FB = "FB_RollingAverage"
SUITE_TEST = "AverageOfConstantStream"
BUGGY_LINE = "FOR i := 1 TO sampleCount DO"
FIXED_LINE = "FOR i := 0 TO sampleCount - 1 DO"

# PLC symbol paths probed via /tcunit-run's `Probes` parameter to gauge
# pass/fail directly from the runtime. The xUnit XML publisher is OFF in
# TcUnit by default (`GVL_Param_TcUnit.xUnitEnablePublish := FALSE`), so
# parsing the XML via /results would require a library-parameter override
# we don't currently set. Reading the live symbols sidesteps that.
SUITE_PROBE_NUM_TESTS = "MAIN.suite.NumberOfTests"
SUITE_PROBE_TEST_FAILED = "MAIN.suite.Tests[1].TestIsFailed"


def _fail(msg: str) -> "None":
    print(f"FAIL: {msg}", file=sys.stderr)
    sys.exit(1)


def _probe_failed(label: str, result: Result) -> bool:
    """Read the suite's TestIsFailed probe from a run_tests Result.

    Returns True if the test failed (red), False if it passed (green).
    Aborts the smoke if the probe didn't land or the response shape is
    unexpected.
    """
    details = result.details or {}
    probes = details.get("probes") or {}
    errors = details.get("probes_errors") or {}
    num_tests = probes.get(SUITE_PROBE_NUM_TESTS)
    failed_raw = probes.get(SUITE_PROBE_TEST_FAILED)
    if num_tests is None or failed_raw is None:
        _fail(
            f"[{label}] expected probes {SUITE_PROBE_NUM_TESTS!r} and "
            f"{SUITE_PROBE_TEST_FAILED!r} on /tcunit-run response; got "
            f"probes={probes!r} errors={errors!r}"
        )
    if str(num_tests).strip() != "1":
        _fail(
            f"[{label}] suite reported NumberOfTests={num_tests!r}; "
            "expected exactly 1 registered test."
        )
    failed = str(failed_raw).strip().lower() == "true"
    print(
        f"  {label}: NumberOfTests={num_tests}  TestIsFailed={failed_raw}"
    )
    return failed


def _cycle(
    *,
    label: str,
    writer: AutomationWriter,
    builder: XaeComBuilder,
    runner: TcUnitRunner,
    target_ams_id: str,
    library_artefact: Path,
) -> bool:
    """One full save-as-library -> build -> deploy -> start_runtime -> run_tests
    pass. Returns whether the suite's test FAILED (True = red, False = green)
    as read from PLC symbols via /tcunit-run probes.
    """
    # Save-As-Library refuses to overwrite an existing .library, so drop the
    # stale artefact before each run. The .library is gitignored and gets
    # regenerated from current source by save_plc_as_library.
    if library_artefact.exists():
        library_artefact.unlink()
    print(f"[{label}] save_plc_as_library({LIBRARY_PLC})...", flush=True)
    save = writer.save_plc_as_library(
        LIBRARY_PLC, str(library_artefact), install=True
    )
    if not save.success:
        _fail(f"[{label}] save_plc_as_library failed: {save.error}")

    print(f"[{label}] build({TESTS_PLC})...", flush=True)
    build = builder.build(str(SLN_PATH), plc_name=TESTS_PLC)
    if not build.success:
        for err in build.errors:
            print(f"  - {err.file}:{err.line}: {err.message}", file=sys.stderr)
        _fail(f"[{label}] build({TESTS_PLC}) failed")

    print(f"[{label}] deploy({TESTS_PLC} -> {target_ams_id})...", flush=True)
    deploy = builder.deploy(target_ams_id, plc_name=TESTS_PLC)
    if not deploy.success:
        _fail(f"[{label}] deploy failed: {deploy.error}")

    print(f"[{label}] start_runtime({target_ams_id})...", flush=True)
    start = builder.start_runtime(target_ams_id)
    if not start.success:
        _fail(f"[{label}] start_runtime failed: {start.error}")

    print(f"[{label}] run_tests({TESTS_PLC})...", flush=True)
    run = runner.run_tests(
        target_ams_id,
        plc_name=TESTS_PLC,
        probes=[SUITE_PROBE_NUM_TESTS, SUITE_PROBE_TEST_FAILED],
    )
    if not run.success:
        _fail(f"[{label}] run_tests failed: {run.error}")
    return _probe_failed(label, run)


def main() -> int:
    target_ams_id = os.getenv("TARGET_AMS_ID")
    if not target_ams_id:
        _fail("TARGET_AMS_ID env var is required.")

    if not SLN_PATH.exists():
        _fail(f"sln not found at {SLN_PATH}. Run author_B1.py first.")

    client = BridgeClient()
    if not client.health():
        _fail(f"Bridge not reachable at {client.base_url}. Start the bridge first.")

    writer = AutomationWriter(client=client)
    builder = XaeComBuilder(client=client)
    runner = TcUnitRunner(client=client)

    # The writer + builder + runner all attach the active project via
    # PLC_PROJECT_PATH; setting it here lets every call reach the right sln.
    os.environ["PLC_PROJECT_PATH"] = str(SLN_PATH)
    library_artefact = FIXTURE_DIR / f"{LIBRARY_PLC}.library"

    # -------- red pass: seeded bug should make TestIsFailed = TRUE
    red_failed = _cycle(
        label="red",
        writer=writer,
        builder=builder,
        runner=runner,
        target_ams_id=target_ams_id,
        library_artefact=library_artefact,
    )
    if not red_failed:
        _fail(
            f"red pass: {SUITE_TEST} reported passing on the seeded bug. "
            "Check the committed FB_RollingAverage.Step."
        )
    print(f"[red] OK — {SUITE_TEST} failed as expected.\n")

    # -------- patch via the writer, then re-run
    print(f"[patch] update_method_body_patch({LIBRARY_FB}.Step)...", flush=True)
    patch = writer.update_method_body_patch(
        LIBRARY_FB,
        "Step",
        BUGGY_LINE,
        FIXED_LINE,
        plc_name=LIBRARY_PLC,
    )
    if not patch.success:
        _fail(f"update_method_body_patch failed: {patch.error}")
    print("[patch] OK\n")

    # -------- green pass: patched code should make TestIsFailed = FALSE
    green_failed = _cycle(
        label="green",
        writer=writer,
        builder=builder,
        runner=runner,
        target_ams_id=target_ams_id,
        library_artefact=library_artefact,
    )
    if green_failed:
        _fail(
            f"green pass: {SUITE_TEST} still failed after the patch. "
            "Investigate the writer patch (update_method_body_patch) or the "
            "seeded fix."
        )
    print(f"[green] OK — {SUITE_TEST} passed after patch.\n")

    print("Smoke complete: red -> patch -> green.")
    print("Reset the committed seeded-bug state with:")
    print(f"  git -C {REPO_ROOT} checkout HEAD -- bench/fixtures/bug-hunting/B1-off-by-one")
    return 0


if __name__ == "__main__":
    sys.exit(main())
