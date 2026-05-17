#!/usr/bin/env python3
"""bench/run.py — runner for the TcKit benchmark harness.

Invokes ``claude -p`` headless with a task prompt and an MCP config,
captures the JSON response, extracts tool calls and token totals, and
writes one result file per run.

Usage::

    python bench/run.py --task bench/tasks/01-orient.md \
        --config bench/configs/tckit.json --runs 3

Each run produces (under bench/results/)::

    <task>__<config>__<utc-timestamp>__run<n>.json   — events + metrics
    <task>__<config>__<utc-timestamp>__run<n>.md     — Markdown preview
    <task>__<config>__<utc-timestamp>__run<n>.diff   — git diff of the project
    <task>__<config>__<utc-timestamp>__run<n>.build.json — bridge build result

The ``.diff`` sibling is written whenever the project path is a git
working tree (no-op for non-git targets). The ``.build.json`` sibling
is only written when ``--build-after-each`` is set; that flag also
implies a one-off pre-bench POST to ``/open`` so XAE has the target
solution loaded before runs begin.

See bench/README.md for the full task list, writer-bench setup, and
prerequisites.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from typing import Any

# Imported lazily inside the build-helper functions so that reader-only
# benches don't require the tckit package to be importable from this venv.
# (Most reader bench runs hit a separate uv/pip environment.)


# ---------------------------------------------------------------------------
# Claude invocation
# ---------------------------------------------------------------------------


def run_one(prompt: str, config: pathlib.Path, tcunit_path: str) -> dict[str, Any]:
    """Invoke ``claude -p`` once with the given prompt and MCP config.

    Uses ``--output-format stream-json`` so each turn (including
    ``tool_use`` and ``tool_result`` content blocks) is emitted as its
    own JSON line. The final ``result`` event carries cumulative usage
    and cost.

    Permissions are bypassed so the headless run does not stall on
    Read/Grep prompts. The TcUnit directory is added via ``--add-dir``
    to grant explicit access independent of the working directory.

    The child process is launched with cwd set to ``tcunit_path``. Two
    side-effects of that choice matter for benchmark isolation:

    1. Claude Code's skill-discovery and ``CLAUDE.md`` ancestor walk
       run from cwd up to filesystem root. If ``tcunit_path`` is
       inside this repo, the walk picks up our project skills under
       ``C:/tckit/.claude/skills/`` and our top-level ``CLAUDE.md``.
       That biases the bench: the vanilla arm sees ``tc-write-st``
       and ``tc-build-test-loop`` telling it to use TcKit's MCP tools
       (which it doesn't have), wastes calls on ``ToolSearch``, and
       generally inherits TcKit context that an installed-TcKit user
       wouldn't pay for. The ``--isolate-cwd`` flag (see main()) pins
       cwd to a temp directory outside the repo and copies the fixture
       in, which makes the walk hit the filesystem root with nothing
       to find.
    2. cwd pinning also stops the model from browsing the TcKit repo
       itself (the original W1-era rationale: vanilla can otherwise
       ``Read`` harness scripts and discover the bridge URL).
    """
    # Resolve the config path before launch: cwd is pinned to the target
    # project below, so a relative --mcp-config would otherwise miss.
    config_abs = str(config.resolve())
    cmd = [
        "claude",
        "-p", prompt,
        "--strict-mcp-config",
        "--mcp-config", config_abs,
        "--output-format", "stream-json",
        "--input-format", "text",
        "--verbose",
        "--no-session-persistence",
        "--permission-mode", "bypassPermissions",
        "--add-dir", tcunit_path,
    ]
    t0 = time.monotonic()
    # Force UTF-8 decoding of the claude CLI's stdout. On Windows the
    # default text= decoding is the system locale (cp1252), which mojibakes
    # any non-ASCII characters Claude writes (em-dashes, smart quotes, etc).
    # cwd is pinned to the target project so the spawned session sees
    # only the codebase under test, not the TcKit source repo.
    proc = subprocess.run(
        cmd, capture_output=True, text=True, check=False,
        encoding="utf-8", errors="replace",
        cwd=tcunit_path,
    )
    duration = time.monotonic() - t0

    events: list[dict[str, Any]] = []
    for line in proc.stdout.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            continue

    return {
        "wall_clock_seconds": duration,
        "exit_code": proc.returncode,
        "events": events,
        "raw_stdout": proc.stdout if not events else None,
        "raw_stderr": proc.stderr if proc.returncode != 0 or not events else None,
    }


# ---------------------------------------------------------------------------
# Metric extraction
# ---------------------------------------------------------------------------


def _content_items(message: dict[str, Any]) -> list[dict[str, Any]]:
    content = message.get("content")
    if isinstance(content, list):
        return [item for item in content if isinstance(item, dict)]
    return []


def extract_metrics(result: dict[str, Any]) -> dict[str, Any]:
    """Walk the stream-json event list and aggregate metrics.

    Each event is one JSON object; ``assistant`` events carry message
    content blocks (which may be ``text`` or ``tool_use``), and the
    final ``result`` event carries cumulative usage and cost.
    """
    events: list[dict[str, Any]] = result.get("events") or []

    tool_calls: list[dict[str, Any]] = []
    final_text: str | None = None
    final_usage: dict[str, Any] = {}
    total_cost_usd: float | None = None
    num_turns: int | None = None
    claude_duration_ms: int | None = None

    for event in events:
        etype = event.get("type")

        if etype == "assistant":
            message = event.get("message") or {}
            for item in _content_items(message):
                if item.get("type") == "tool_use":
                    tool_calls.append({
                        "name": item.get("name"),
                        "input_keys": sorted((item.get("input") or {}).keys())
                            if isinstance(item.get("input"), dict) else [],
                    })

        elif etype == "result":
            final_text = event.get("result") if isinstance(event.get("result"), str) else final_text
            usage = event.get("usage")
            if isinstance(usage, dict):
                final_usage = usage
            cost = event.get("total_cost_usd")
            if isinstance(cost, (int, float)):
                total_cost_usd = float(cost)
            turns = event.get("num_turns")
            if isinstance(turns, int):
                num_turns = turns
            duration = event.get("duration_ms")
            if isinstance(duration, int):
                claude_duration_ms = duration

    input_tokens = int(final_usage.get("input_tokens", 0) or 0)
    output_tokens = int(final_usage.get("output_tokens", 0) or 0)
    cache_read = int(final_usage.get("cache_read_input_tokens", 0) or 0)
    cache_creation = int(final_usage.get("cache_creation_input_tokens", 0) or 0)

    tool_breakdown: dict[str, int] = {}
    for call in tool_calls:
        name = call.get("name") or "<unknown>"
        tool_breakdown[name] = tool_breakdown.get(name, 0) + 1

    return {
        "tool_call_count": len(tool_calls),
        "tool_breakdown": tool_breakdown,
        "tool_calls": tool_calls,
        "num_turns": num_turns,
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "cache_read_tokens": cache_read,
        "cache_creation_tokens": cache_creation,
        "total_tokens": input_tokens + output_tokens,
        "total_cost_usd": total_cost_usd,
        "wall_clock_seconds": result.get("wall_clock_seconds"),
        "claude_reported_duration_ms": claude_duration_ms,
        "exit_code": result.get("exit_code"),
        "final_text": final_text,
    }


# ---------------------------------------------------------------------------
# Reset hook and bridge-driven verification (writer bench)
# ---------------------------------------------------------------------------


def run_reset_cmd(reset_cmd: str) -> None:
    """Run a shell command before each bench run; abort on non-zero exit.

    The contract for writer bench: a clean project state is a precondition
    for every run. If the reset fails (typo, missing path, dirty index that
    refuses to clean), surface it loudly rather than benching against a
    partially-mutated tree.
    """
    proc = subprocess.run(
        reset_cmd, shell=True, capture_output=True, text=True, check=False,
    )
    if proc.returncode != 0:
        print(f"  reset-cmd failed (exit {proc.returncode}):", file=sys.stderr)
        if proc.stdout:
            print(proc.stdout, file=sys.stderr)
        if proc.stderr:
            print(proc.stderr, file=sys.stderr)
        raise SystemExit(3)


def capture_git_diff(project_path: str) -> str | None:
    """Return ``git diff`` of the project tree, or None if not a git repo.

    Returns an empty string for a clean tree. We do this unconditionally on
    every run because it's cheap and the post-write diff is the human-facing
    artefact that says what each config actually did to the project.
    """
    proc = subprocess.run(
        ["git", "-C", project_path, "diff", "--no-color"],
        capture_output=True, text=True, check=False,
        encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        # Not a git repo, or git itself failed. Either way, no diff to write.
        return None
    return proc.stdout


def open_solution(bridge_url: str, sln_path: str) -> dict[str, Any]:
    """POST /open once before the bench run loop. Idempotent on XAE side.

    Cold XAE spawn (XAE_MODE=headless) can take well over the default 60s
    bridge timeout — between spinning up TcXaeShell, registering COM, and
    opening the solution. Reuse the build timeout (default 600s, override
    via TCKIT_BUILD_TIMEOUT) since opening is in the same latency class.
    """
    from tckit.utils.bridge_client import BridgeClient, build_timeout

    client = BridgeClient(base_url=bridge_url, timeout=build_timeout())
    try:
        if not client.health():
            raise SystemExit(
                f"Bridge not reachable at {bridge_url}; start the bridge first."
            )
        return client.post("/open", {"SolutionPath": sln_path})
    finally:
        client.close()


def isolate_fixture_to_tempdir(fixture_path: str) -> pathlib.Path:
    """Copy a fixture tree into a fresh temp directory outside any repo,
    stripping Claude Code metadata files so the vanilla arm doesn't
    inherit them.

    Returns the temp path. The caller passes this path as cwd for
    ``claude -p``, which moves Claude Code's skill-discovery /
    CLAUDE.md ancestor walk out of the project tree and into a
    location with no ``.claude/`` or ``CLAUDE.md`` anywhere above it.

    Excluded from the copy:

    - ``CLAUDE.md`` / ``CLAUDE.local.md`` — would auto-load at cwd
      and bias the vanilla arm. The B1 fixture's CLAUDE.md mentions
      TcKit by name, which is precisely the contamination we're
      isolating against.
    - ``.claude/`` — any project skills, settings, agents the
      fixture might ship.
    - ``.mcp.json`` — would auto-add MCP servers that ``empty.json``
      is supposed to prevent.
    - XAE / Visual Studio build artefacts that the operator's
      currently-attached XAE has open file handles on (which makes
      shutil.copytree raise PermissionError), and which are
      gitignored anyway because they're rebuilt per run:
      ``.vs/``, ``_Boot/``, ``_CompileInfo*/``, ``_Deployment/``,
      ``_Libraries/``, ``_Output/``, ``*.tmc``, ``*.suo``,
      ``*.~u``, ``*.bak``, ``*.tpzip``, ``*.tszip``, ``*.tpy``,
      ``*.library``.

    The temp dir is created under ``$TEMP``/``/tmp`` (per-user) so a
    credentials-grade isolation isn't needed; the cwd just has to be
    outside the TcKit repo.
    """
    src = pathlib.Path(fixture_path).resolve()
    tmp_root = pathlib.Path(tempfile.mkdtemp(prefix="tckit-bench-"))
    dest = tmp_root / src.name
    ignore = shutil.ignore_patterns(*_TEMP_FIXTURE_EXCLUDES)
    shutil.copytree(src, dest, ignore=ignore)
    return dest


# Patterns excluded from both isolate_fixture_to_tempdir (outbound copy)
# AND sync_fixture_edits (inbound mirror). The outbound case avoids
# locked-file PermissionError when XAE has handles open; the inbound
# case avoids the same problem in reverse when the bench's own
# per-run MCP opens the temp fixture in DTE and XAE creates
# .vs/.suo/_Boot etc inside the temp tree — those must not be mirrored
# back over the operator's live versions in the real fixture.
_TEMP_FIXTURE_EXCLUDES = (
    # Claude Code metadata — the contamination --isolate-cwd exists for.
    "CLAUDE.md",
    "CLAUDE.local.md",
    ".claude",
    ".mcp.json",
    # XAE / VS build artefacts: locked by an open XAE and gitignored.
    ".vs",
    "_Boot",
    "_CompileInfo",
    "_CompileInfo_Upload",
    "_Deployment",
    "_Libraries",
    "_Output",
    "*.tmc",
    "*.suo",
    "*.~u",
    "*.bak",
    "*.tpzip",
    "*.tszip",
    "*.tpy",
    "*.library",
)


def _is_excluded_path(rel: pathlib.PurePath) -> bool:
    """Match a relative path against _TEMP_FIXTURE_EXCLUDES.

    Pattern semantics mirror shutil.ignore_patterns: any path segment
    that matches a pattern excludes the path. Used by
    sync_fixture_edits to skip files that should not be mirrored back
    to the real fixture (locked artefacts, dev metadata).
    """
    import fnmatch
    for part in rel.parts:
        for pat in _TEMP_FIXTURE_EXCLUDES:
            if fnmatch.fnmatch(part, pat):
                return True
    return False


def inject_skills_into_isolated(
    temp_fixture: pathlib.Path, skills_dir: str
) -> int:
    """Copy a skills directory into a temp fixture's ``.claude/skills/``.

    Used on the tckit arm to inject the shippable ``plugin/skills/``
    surface into the isolated temp cwd. Without it, the tckit arm
    has no skills at all when paired with --isolate-cwd (which
    strips ``.claude/`` from the fixture copy); with it, the model
    sees exactly the 6 user-facing TcKit skills, not the dev-only
    `tc-adr` / `tc-docs-write` that live in this repo's
    ``.claude/skills/``.

    Returns the number of skills copied. Each skill must be a
    directory under ``skills_dir`` with at least a ``SKILL.md``.
    """
    src = pathlib.Path(skills_dir).resolve()
    if not src.is_dir():
        raise FileNotFoundError(
            f"--inject-skills path is not a directory: {src}"
        )
    dest = temp_fixture / ".claude" / "skills"
    dest.mkdir(parents=True, exist_ok=True)
    count = 0
    for skill in sorted(src.iterdir()):
        if not skill.is_dir():
            continue
        shutil.copytree(skill, dest / skill.name)
        count += 1
    return count


def sync_fixture_edits(tmp_fixture: pathlib.Path, real_fixture: str) -> None:
    """Copy files modified inside ``tmp_fixture`` back to the real
    fixture, preserving relative paths.

    Conservative on two axes:

    1. **Only mirrors files whose mtime is newer than the matching
       path in the real fixture** (i.e. the model touched them).
    2. **Only mirrors files that already exist in the real fixture.**
       Injected metadata (e.g. ``.claude/skills/`` from
       ``--inject-skills``) lives in the temp copy but never in the
       real source tree; skipping non-existent destinations stops
       the inject step from polluting the real fixture on sync-back.

    Files deleted in the temp copy are NOT deleted from the real
    tree; the bench harness's reset-cmd is authoritative for that.
    Bug-hunting tasks are edits to existing code by construction, so
    "new files don't sync" is not a real loss; if a future task
    needs adds, this policy can be revisited.
    """
    real = pathlib.Path(real_fixture).resolve()
    for src_path in tmp_fixture.rglob("*"):
        if src_path.is_dir():
            continue
        rel = src_path.relative_to(tmp_fixture)
        # Skip XAE build artefacts and dev metadata: those may exist in
        # the temp tree because the bench's per-run MCP opened the temp
        # sln in DTE (XAE creates .vs/.suo etc inside the loaded
        # solution dir), but the operator's live XAE has handles open
        # on the real-fixture versions and shutil.copy2 would fail with
        # PermissionError. Same pattern list isolate_fixture_to_tempdir
        # excludes on the outbound copy.
        if _is_excluded_path(rel):
            continue
        dest_path = real / rel
        if not dest_path.exists():
            continue
        if dest_path.stat().st_mtime >= src_path.stat().st_mtime:
            continue
        shutil.copy2(src_path, dest_path)


def _port_in_use(host: str, port: int) -> bool:
    """Best-effort socket check: return True if something is listening.

    Used to refuse-and-explain rather than silently double-spawn when an
    operator already has the MCP server running on the bench's port.
    """
    import socket
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(0.5)
    try:
        s.connect((host, port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def _wait_mcp_ready(mcp_url: str, timeout_s: int) -> bool:
    """Poll the MCP SSE endpoint until it responds, or timeout.

    The /sse handshake returns 200 with an event stream; we abandon the
    connection immediately, we just need to know the server is up. Any
    transient error (URLError, timeout) means not ready yet.
    """
    import urllib.error
    import urllib.request

    sse = mcp_url.rstrip("/") + "/sse"
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            req = urllib.request.Request(sse, headers={"Accept": "text/event-stream"})
            with urllib.request.urlopen(req, timeout=2) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, OSError, TimeoutError):
            pass
        time.sleep(0.5)
    return False


def start_mcp_subprocess(
    mcp_cmd: str,
    plc_project_path: str,
    extra_env: dict[str, str] | None = None,
) -> subprocess.Popen:
    """Spawn an MCP server pointed at ``plc_project_path``.

    The bench runs MCP per-run so PLC_PROJECT_PATH can switch between
    the isolated temp fixture (during the model session) and the real
    fixture (during post-run-tests). Inherits the parent process env
    plus the overrides; ``extra_env`` is for safety knobs like
    ``ALLOWED_NETIDS`` / ``SAFETY_CONFIRMATIONS`` that the operator
    set on the bench's own env. Stdout/stderr go to DEVNULL — the
    server is noisy and we are not debugging it from here.

    On Windows the spawn goes through a CREATE_NEW_PROCESS_GROUP so
    stop_mcp_subprocess can terminate the whole tree (otherwise the
    python child of a shell wrapper survives ``terminate()`` and
    keeps the port held). The command is shlex-split rather than
    shell=True so we keep a direct handle on the python process.
    """
    import shlex

    env = dict(os.environ)
    env["PLC_PROJECT_PATH"] = plc_project_path
    if extra_env:
        env.update(extra_env)

    creation_flags = 0
    if os.name == "nt":
        creation_flags = subprocess.CREATE_NEW_PROCESS_GROUP

    return subprocess.Popen(
        shlex.split(mcp_cmd),
        shell=False,
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=creation_flags,
    )


def stop_mcp_subprocess(proc: subprocess.Popen) -> None:
    """Terminate the per-run MCP server tree. Force-kill if needed.

    On Windows ``terminate`` only kills the immediate process and any
    children spawned via ``uv run`` survive (the actual python -m
    tckit.server). We try the polite signal first, then fall back to
    ``taskkill /T`` which walks the process tree.
    """
    if proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=3)
        return
    except subprocess.TimeoutExpired:
        pass
    # Walk the tree. On Windows taskkill /T /F is the cleanest way to
    # nuke `uv run python ...` children. On POSIX kill -- -pgid works
    # via the new-process-group flag.
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    else:
        try:
            os.killpg(os.getpgid(proc.pid), 9)
        except (OSError, ProcessLookupError):
            pass
    try:
        proc.wait(timeout=2)
    except subprocess.TimeoutExpired:
        pass


def _temp_sln_path(
    tmp_fixture: pathlib.Path | None, tcunit_path: str, sln_path: str
) -> str:
    """Resolve the sln path the MCP server should point at for this run.

    With ``--isolate-cwd`` the model session sees the temp copy, so the
    MCP server must too — otherwise the writer's MCP calls write to the
    real fixture while the model's Read tools see the temp copy and
    cannot observe their own writes. Without isolate-cwd both paths
    coincide.
    """
    if tmp_fixture is None:
        return sln_path
    rel = pathlib.Path(sln_path).resolve().relative_to(
        pathlib.Path(tcunit_path).resolve()
    )
    return str(tmp_fixture / rel).replace("\\", "/")


def close_solution(bridge_url: str) -> dict[str, Any]:
    """POST /close to release the bridge's in-memory project model.

    Used to bracket a ``claude -p`` invocation that may edit .plcproj /
    .TcPOU XML directly (the vanilla arm, no MCP tools). With the
    solution closed, XAE won't flag the disk edits as "modified
    externally"; a subsequent ``/open`` re-reads from the new disk
    state. Mirrors the close/edit/reopen pattern in
    ``bridge/harness/Add-TcLibraryPlaceholder.ps1``.
    """
    from tckit.utils.bridge_client import BridgeClient, BridgeError, build_timeout

    client = BridgeClient(base_url=bridge_url, timeout=build_timeout())
    try:
        return client.post("/close", {})
    except BridgeError as exc:
        return {"success": False, "error": str(exc)}
    finally:
        client.close()


def build_project(bridge_url: str, sln_path: str) -> dict[str, Any]:
    """POST /build via the bridge and return the parsed response.

    Uses the longer build timeout from tckit.utils.bridge_client so XAE has
    room to rebuild larger projects. Any bridge-level error is captured into
    the returned dict so the caller can persist it in the .build.json sibling.
    """
    from tckit.utils.bridge_client import (
        BridgeClient,
        BridgeError,
        build_timeout,
    )

    client = BridgeClient(base_url=bridge_url, timeout=build_timeout())
    try:
        return client.post("/build", {"ProjectPath": sln_path})
    except BridgeError as exc:
        return {"success": False, "error": str(exc)}
    finally:
        client.close()


# ---------------------------------------------------------------------------
# Closed-loop test cycle (bug-hunting bench)
# ---------------------------------------------------------------------------
#
# The bug-hunting fixtures (ADR-0007) have two PLC projects in one sln:
# a library under test and a tests project that references the library
# as a compiled .library. The consumer build resolves against the
# *installed* library, not source, so any edit to the library project
# must be flushed through save_plc_as_library before rebuilding tests.
# These helpers wrap that orchestration so a fresh seeded-bug state
# (pre-run) and the model's edited state (post-run) both reach the
# runtime via a freshly-installed .library.


def _library_artefact_path(sln_path: str, library_plc: str) -> pathlib.Path:
    """Conventional .library path next to the sln: ``<sln_dir>/<plc>.library``.

    Mirrors ``smoke_B1.py``: the .library is gitignored and regenerated
    from current source on each save_plc_as_library call.
    """
    return pathlib.Path(sln_path).resolve().parent / f"{library_plc}.library"


def save_plc_library(
    bridge_url: str, sln_path: str, library_plc: str
) -> dict[str, Any]:
    """Save the named PLC as a compiled .library and install it.

    Returns ``{success, error, output_path}``. Deletes any stale .library
    before saving; the underlying bridge route historically refused to
    overwrite, and the artefact is gitignored anyway.
    """
    from tckit.adapters.writers.automation_writer import AutomationWriter
    from tckit.utils.bridge_client import BridgeClient, build_timeout

    output_path = _library_artefact_path(sln_path, library_plc)
    try:
        if output_path.exists():
            output_path.unlink()
    except OSError as exc:
        return {
            "success": False,
            "error": f"could not remove stale .library at {output_path}: {exc}",
            "output_path": str(output_path),
        }

    # PLC_PROJECT_PATH steers the bridge to the right sln; set it here so
    # the writer adapter forwards it on the POST body. Mirrors smoke_B1.py.
    os.environ["PLC_PROJECT_PATH"] = sln_path

    client = BridgeClient(base_url=bridge_url, timeout=build_timeout())
    writer = AutomationWriter(client=client)
    try:
        result = writer.save_plc_as_library(
            library_plc, str(output_path), install=True
        )
        return {
            "success": bool(result.success),
            "error": result.error,
            "output_path": str(output_path),
        }
    finally:
        client.close()


def run_test_cycle(
    *,
    bridge_url: str,
    sln_path: str,
    library_plc: str,
    tests_plc: str,
    target_ams_id: str,
    probes: list[str],
) -> dict[str, Any]:
    """Drive the full post-session validation cycle for a bug-hunting fixture.

    Order mirrors ``bench/fixtures/bug-hunting/_author/smoke_B1.py``'s
    ``_cycle()``: save_plc_as_library (library) -> build (tests) -> deploy
    (tests) -> start_runtime -> run_tests (tests, with probes). Each step
    short-circuits on failure and the returned dict records what was
    reached. The probe values land under ``probes``; the caller derives
    pass/fail from ``*.TestIsFailed`` entries.
    """
    from tckit.adapters.builders.xae_com_builder import XaeComBuilder
    from tckit.adapters.test_runners.tcunit_runner import TcUnitRunner
    from tckit.adapters.writers.automation_writer import AutomationWriter
    from tckit.utils.bridge_client import BridgeClient, build_timeout

    os.environ["PLC_PROJECT_PATH"] = sln_path
    out: dict[str, Any] = {
        "library_saved": None,
        "library_save_error": None,
        "built": None,
        "build_errors": [],
        "deployed": None,
        "deploy_error": None,
        "runtime_started": None,
        "start_error": None,
        "tests_ran": None,
        "tests_run_error": None,
        "probes": {},
        "probes_errors": {},
    }

    client = BridgeClient(base_url=bridge_url, timeout=build_timeout())
    writer = AutomationWriter(client=client)
    builder = XaeComBuilder(client=client)
    runner = TcUnitRunner(client=client)
    try:
        artefact = _library_artefact_path(sln_path, library_plc)
        if artefact.exists():
            try:
                artefact.unlink()
            except OSError as exc:
                out["library_saved"] = False
                out["library_save_error"] = (
                    f"could not remove stale .library at {artefact}: {exc}"
                )
                return out
        save = writer.save_plc_as_library(
            library_plc, str(artefact), install=True
        )
        out["library_saved"] = bool(save.success)
        out["library_save_error"] = save.error
        if not save.success:
            return out

        build = builder.build(sln_path, plc_name=tests_plc)
        out["built"] = bool(build.success)
        out["build_errors"] = [
            {"file": e.file, "line": e.line, "message": e.message}
            for e in build.errors
        ]
        if not build.success:
            return out

        deploy = builder.deploy(target_ams_id, plc_name=tests_plc)
        out["deployed"] = bool(deploy.success)
        out["deploy_error"] = deploy.error
        if not deploy.success:
            return out

        start = builder.start_runtime(target_ams_id)
        out["runtime_started"] = bool(start.success)
        out["start_error"] = start.error
        if not start.success:
            return out

        run_result = runner.run_tests(
            target_ams_id, plc_name=tests_plc, probes=probes or None,
        )
        out["tests_ran"] = bool(run_result.success)
        out["tests_run_error"] = run_result.error
        details = run_result.details or {}
        probe_values = details.get("probes")
        if isinstance(probe_values, dict):
            out["probes"] = {str(k): str(v) for k, v in probe_values.items()}
        probe_errors = details.get("probes_errors")
        if isinstance(probe_errors, dict):
            out["probes_errors"] = {str(k): str(v) for k, v in probe_errors.items()}
        return out
    finally:
        client.close()


def derive_pass_fail(probes: dict[str, str]) -> bool | None:
    """Infer test pass/fail from ``*.TestIsFailed`` probe values.

    Any probe whose key ends with ``.TestIsFailed`` and whose value is
    "True" (case-insensitive) flips the result to failed. Returns
    ``None`` if no such probes were collected, so the caller can
    distinguish "tests didn't run" from "tests ran and passed".
    """
    failed_flags = [v for k, v in probes.items() if k.endswith(".TestIsFailed")]
    if not failed_flags:
        return None
    return not any(str(v).strip().lower() == "true" for v in failed_flags)


def check_tests_modified(repo_root: str, guard_path: str) -> list[str]:
    """Return the list of paths under ``guard_path`` modified vs HEAD.

    Tamper guard for the bug-hunting bench: a non-empty result means the
    model edited test code to make the suite pass, which grades the run
    as failed regardless of the test outcome (ADR-0007).
    """
    proc = subprocess.run(
        ["git", "-C", repo_root, "diff", "--name-only", "--", guard_path],
        capture_output=True, text=True, check=False,
        encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        return []
    return [line for line in proc.stdout.splitlines() if line.strip()]


# ---------------------------------------------------------------------------
# Markdown sibling
# ---------------------------------------------------------------------------


def render_markdown(
    *,
    task: str,
    config: str,
    run: int,
    timestamp: str,
    tcunit_path: str,
    metrics: dict[str, Any],
    final_text: str | None,
) -> str:
    """Render the per-run final text as a Markdown document.

    The .md sibling exists so reviewers can read the actual answer
    Claude produced without parsing the JSON. Headline metrics go in
    a short preamble; the body is the unmodified ``final_text``.
    """
    tool_breakdown = metrics.get("tool_breakdown") or {}
    if tool_breakdown:
        breakdown_line = ", ".join(
            f"{name}×{count}" for name, count in sorted(tool_breakdown.items())
        )
    else:
        breakdown_line = "(none)"

    wall = metrics.get("wall_clock_seconds")
    wall_str = f"{wall:.1f}s" if isinstance(wall, (int, float)) else "n/a"

    header = (
        f"# {task} — {config} — run {run}\n\n"
        f"- Timestamp (UTC): {timestamp}\n"
        f"- TcUnit path: {tcunit_path}\n"
        f"- Tool calls: {metrics.get('tool_call_count', 'n/a')}\n"
        f"- Total tokens: {metrics.get('total_tokens', 'n/a')}\n"
        f"- Wall clock: {wall_str}\n"
        f"- Exit code: {metrics.get('exit_code', 'n/a')}\n"
        f"- Tool breakdown: {breakdown_line}\n\n"
        "---\n\n"
    )
    body = final_text if isinstance(final_text, str) and final_text else "_(no final text)_"
    return header + body + "\n"


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description="Run a TcKit benchmark task.")
    parser.add_argument("--task", required=True, type=pathlib.Path,
                        help="Path to task markdown file.")
    parser.add_argument("--config", required=True, type=pathlib.Path,
                        help="Path to MCP config JSON.")
    parser.add_argument("--runs", type=int, default=3,
                        help="Number of runs to execute (default 3).")
    parser.add_argument("--tcunit-path", default="C:/TcUnit",
                        help="Path substituted into ${TCUNIT_PATH} placeholders.")
    parser.add_argument("--results-dir", type=pathlib.Path,
                        default=pathlib.Path(__file__).parent / "results",
                        help="Directory to write per-run result JSONs.")
    parser.add_argument("--reset-cmd", default="",
                        help=(
                            "Shell command to run before every bench run. "
                            "Required for writer benches; pair with a writable "
                            "clone of the target project. Non-zero exit aborts."
                        ))
    parser.add_argument("--sln-path", default="",
                        help=(
                            "Absolute path to the .sln. When set, the harness "
                            "POSTs /open once before the run loop and uses this "
                            "path for the build call. Required with --build-after-each."
                        ))
    parser.add_argument("--build-after-each", action="store_true",
                        help=(
                            "After each run, POST /build via the bridge and "
                            "write the parsed result as a <stem>.build.json "
                            "sibling. Requires --sln-path."
                        ))
    parser.add_argument("--bridge-url", default="http://localhost:8765",
                        help="Bridge service URL. Default: http://localhost:8765")
    parser.add_argument("--pre-save-as-library", default="",
                        help=(
                            "Name of the library PLC project to save+install "
                            "before each run, so the consumer build resolves "
                            "against the freshly-seeded source. Required for "
                            "bug-hunting fixtures where one PLC project is "
                            "consumed by another as a compiled .library. The "
                            "output path is derived as <sln-dir>/<name>.library."
                        ))
    parser.add_argument("--post-run-tests", default="",
                        help=(
                            "Name of the tests PLC project to drive through "
                            "build -> deploy -> start_runtime -> run_tests "
                            "after each run. Re-saves the library first (so the "
                            "model's edits land in the installed .library) and "
                            "writes a .test-result.json sibling. Requires "
                            "--sln-path, --pre-save-as-library, and TARGET_AMS_ID."
                        ))
    parser.add_argument("--tests-guard-path", default="",
                        help=(
                            "Repo-relative path to a tests directory. After "
                            "each run, the harness diffs this path vs HEAD; "
                            "a non-empty diff marks the run as tampered "
                            "(passed=False) and records the modified files."
                        ))
    parser.add_argument("--test-probe", action="append", default=[],
                        help=(
                            "PLC symbol path read after run_tests; repeatable. "
                            "Used to gauge pass/fail when the xUnit XML "
                            "publisher is off. Defaults to the B1 probe set "
                            "(MAIN.suite.NumberOfTests + MAIN.suite.Tests[1]"
                            ".TestIsFailed) when --post-run-tests is set."
                        ))
    parser.add_argument("--repo-root", type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parent.parent,
                        help=(
                            "Repository root used by --tests-guard-path for "
                            "the git diff call. Defaults to the parent of bench/."
                        ))
    parser.add_argument("--close-during-run", action="store_true",
                        help=(
                            "Close the bridge's loaded solution before each "
                            "claude -p invocation and re-open it after. Required "
                            "for the vanilla arm of bug-hunting benches: the "
                            "model edits .plcproj / .TcPOU XML directly, which "
                            "trips XAE's 'modified externally' guard. The pre-"
                            "save-as-library still runs while the solution is "
                            "open; the close fires only around the model session."
                        ))
    parser.add_argument("--isolate-cwd", action="store_true",
                        help=(
                            "Copy the fixture to a fresh temp directory "
                            "outside this repo before each claude -p "
                            "invocation, pin cwd there, then sync the "
                            "model's edits back to the real fixture for "
                            "post-run validation. CLAUDE.md / .claude/ / "
                            ".mcp.json and XAE build artefacts are "
                            "excluded from the copy. Use this on both arms "
                            "to ensure neither inherits this repo's "
                            "dev-side .claude/skills/ + CLAUDE.md via "
                            "Claude Code's cwd-ancestor walk. Pair with "
                            "--inject-skills on the tckit arm to give it "
                            "the user-facing plugin/skills/ surface "
                            "afterwards. Cheaper than --bare and keeps "
                            "OAuth working (--bare requires API key)."
                        ))
    parser.add_argument("--inject-skills", default="",
                        help=(
                            "Path to a skills directory to copy into the "
                            "isolated temp fixture's .claude/skills/ "
                            "after --isolate-cwd has prepared it. Use "
                            "this on the tckit arm with `plugin/skills` "
                            "to inject the 6 user-facing TcKit skills, "
                            "so the model sees the shippable plugin "
                            "surface rather than this repo's dev-side "
                            ".claude/skills/ (which adds tc-adr and "
                            "tc-docs-write — dev-only). Requires "
                            "--isolate-cwd."
                        ))
    parser.add_argument("--mcp-cmd", default="",
                        help=(
                            "If set, the bench manages a per-run MCP "
                            "server with PLC_PROJECT_PATH pointing at "
                            "the active fixture sln (the temp copy "
                            "under --isolate-cwd, else the real "
                            "fixture). Without this, the model's MCP "
                            "writer calls write to the operator's "
                            "long-lived MCP env path while Read sees "
                            "the temp copy — the model cannot observe "
                            "its own writes. Recommended for the tckit "
                            "arm. Example: "
                            "'uv run python -m tckit.server --transport sse'."
                        ))
    parser.add_argument("--mcp-url", default="http://localhost:8000",
                        help="MCP SSE base URL the spawned server listens on.")
    parser.add_argument("--mcp-startup-timeout", type=int, default=30,
                        help="Seconds to wait for /sse to respond after MCP spawn.")
    args = parser.parse_args()

    if args.build_after_each and not args.sln_path:
        print("--build-after-each requires --sln-path", file=sys.stderr)
        return 2

    if args.inject_skills and not args.isolate_cwd:
        print("--inject-skills requires --isolate-cwd", file=sys.stderr)
        return 2

    if args.post_run_tests:
        if not args.sln_path:
            print("--post-run-tests requires --sln-path", file=sys.stderr)
            return 2
        if not args.pre_save_as_library:
            print(
                "--post-run-tests requires --pre-save-as-library "
                "(the library PLC name)",
                file=sys.stderr,
            )
            return 2
        if not os.getenv("TARGET_AMS_ID"):
            print(
                "--post-run-tests requires TARGET_AMS_ID env var "
                "(the target runtime)",
                file=sys.stderr,
            )
            return 2

    test_probes: list[str] = list(args.test_probe)
    if args.post_run_tests and not test_probes:
        # B1's probe set as the default (matches smoke_B1.py). Operators
        # of multi-test fixtures pass --test-probe explicitly.
        test_probes = [
            "MAIN.suite.NumberOfTests",
            "MAIN.suite.Tests[1].TestIsFailed",
        ]

    if not args.task.exists():
        print(f"task not found: {args.task}", file=sys.stderr)
        return 2
    if not args.config.exists():
        print(f"config not found: {args.config}", file=sys.stderr)
        return 2

    args.results_dir.mkdir(parents=True, exist_ok=True)

    prompt_template = args.task.read_text(encoding="utf-8")
    prompt = prompt_template.replace("${TCUNIT_PATH}", args.tcunit_path)

    task_stem = args.task.stem
    config_stem = args.config.stem
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")

    print(f"Task: {task_stem}  Config: {config_stem}  Runs: {args.runs}")
    print(f"TcUnit path: {args.tcunit_path}")
    if args.reset_cmd:
        print(f"Reset cmd: {args.reset_cmd}")
    if args.sln_path:
        print(f"Sln path: {args.sln_path}")
    print()

    # Pre-flight: refuse-and-explain if --mcp-cmd is set but the port is
    # already taken. Otherwise the spawn would silently fail and the
    # claude -p arm would hit whatever MCP the operator had running with
    # its own (probably stale) PLC_PROJECT_PATH.
    if args.mcp_cmd:
        from urllib.parse import urlparse

        parsed = urlparse(args.mcp_url)
        host = parsed.hostname or "localhost"
        port = parsed.port or 8000
        if _port_in_use(host, port):
            print(
                f"  --mcp-cmd given but {host}:{port} already in use. Stop the "
                "existing MCP server (or omit --mcp-cmd to use it as-is, at "
                "the cost of the isolate-cwd staleness bug).",
                file=sys.stderr,
            )
            return 7

    # One-off pre-bench /open so XAE is on the right solution before any run.
    # Cheap and idempotent on the bridge side; the operator can rely on a
    # single command for setup rather than alt-tabbing into XAE.
    if args.sln_path:
        print(f"  Pre-bench: opening {args.sln_path} via {args.bridge_url}...", flush=True)
        open_resp = open_solution(args.bridge_url, args.sln_path)
        if not open_resp.get("success", False):
            print(f"  /open failed: {open_resp.get('error', open_resp)}", file=sys.stderr)
            return 3
        print("  OK")

    for n in range(1, args.runs + 1):
        print(f"  Run {n}/{args.runs}...", end=" ", flush=True)
        if args.reset_cmd:
            # Close the solution around the reset so XAE doesn't catch
            # the disk revert as an external modification and drop its
            # in-memory project (which would wedge subsequent calls).
            # /open is idempotent and re-reads from the now-reverted
            # disk state. Only bother if we have a sln_path to reopen.
            if args.sln_path:
                close_solution(args.bridge_url)
            run_reset_cmd(args.reset_cmd)
            if args.sln_path:
                reopen_resp = open_solution(args.bridge_url, args.sln_path)
                if not reopen_resp.get("success", False):
                    print(
                        f"\n  /open (post-reset reopen) failed: "
                        f"{reopen_resp.get('error', reopen_resp)}",
                        file=sys.stderr,
                    )
                    return 3
        pre_save_result: dict[str, Any] | None = None
        if args.pre_save_as_library:
            print(" pre-save...", end="", flush=True)
            pre_save_result = save_plc_library(
                args.bridge_url, args.sln_path, args.pre_save_as_library
            )
            if not pre_save_result.get("success"):
                # Abort the run: vanilla and tckit both rely on the consumer
                # build resolving against the seeded-bug .library, and without
                # this step the .library reflects whatever was last installed.
                print(
                    f"\n  pre-save-as-library failed: "
                    f"{pre_save_result.get('error')}",
                    file=sys.stderr,
                )
                return 4
        if args.close_during_run:
            print(" close-sln...", end="", flush=True)
            close_resp = close_solution(args.bridge_url)
            if not close_resp.get("success", False):
                print(
                    f"\n  /close failed: {close_resp.get('error', close_resp)}",
                    file=sys.stderr,
                )
                return 5
        run_cwd = args.tcunit_path
        tmp_fixture: pathlib.Path | None = None
        if args.isolate_cwd:
            print(" isolate-cwd...", end="", flush=True)
            tmp_fixture = isolate_fixture_to_tempdir(args.tcunit_path)
            if args.inject_skills:
                n_skills = inject_skills_into_isolated(
                    tmp_fixture, args.inject_skills
                )
                print(f" inject-{n_skills}-skills...", end="", flush=True)
            run_cwd = str(tmp_fixture)
        # MCP server lifecycle: per-run spawn with PLC_PROJECT_PATH pointing
        # at the active sln (temp under --isolate-cwd, else the real
        # fixture). Without this, the model's MCP writer calls would land
        # in the operator's long-lived MCP env path while Read sees the
        # temp copy — the model cannot observe its own writes.
        mcp_proc: subprocess.Popen | None = None
        if args.mcp_cmd:
            mcp_plc_path = _temp_sln_path(tmp_fixture, args.tcunit_path, args.sln_path)
            print(" mcp-start...", end="", flush=True)
            mcp_proc = start_mcp_subprocess(args.mcp_cmd, mcp_plc_path)
            if not _wait_mcp_ready(args.mcp_url, args.mcp_startup_timeout):
                stop_mcp_subprocess(mcp_proc)
                print(
                    f"\n  MCP did not become ready at {args.mcp_url} within "
                    f"{args.mcp_startup_timeout}s",
                    file=sys.stderr,
                )
                return 6
        try:
            result = run_one(prompt, args.config, run_cwd)
        finally:
            if mcp_proc is not None:
                print(" mcp-stop...", end="", flush=True)
                stop_mcp_subprocess(mcp_proc)
            if tmp_fixture is not None:
                sync_fixture_edits(tmp_fixture, args.tcunit_path)
                try:
                    shutil.rmtree(tmp_fixture.parent, ignore_errors=True)
                except OSError:
                    pass
        if args.close_during_run:
            print(" reopen-sln...", end="", flush=True)
            reopen_resp = open_solution(args.bridge_url, args.sln_path)
            if not reopen_resp.get("success", False):
                print(
                    f"\n  /open (post-run reopen) failed: "
                    f"{reopen_resp.get('error', reopen_resp)}",
                    file=sys.stderr,
                )
                return 5
        metrics = extract_metrics(result)

        out = {
            "task": task_stem,
            "config": config_stem,
            "run": n,
            "timestamp_utc": timestamp,
            "tcunit_path": args.tcunit_path,
            "metrics": {k: v for k, v in metrics.items() if k != "final_text"},
            "final_text": metrics.get("final_text"),
            "events": result.get("events"),
            "raw_stdout": result.get("raw_stdout"),
            "raw_stderr": result.get("raw_stderr"),
        }
        out_path = args.results_dir / f"{task_stem}__{config_stem}__{timestamp}__run{n}.json"
        out_path.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")

        md_path = out_path.with_suffix(".md")
        md_path.write_text(
            render_markdown(
                task=task_stem,
                config=config_stem,
                run=n,
                timestamp=timestamp,
                tcunit_path=args.tcunit_path,
                metrics=metrics,
                final_text=metrics.get("final_text"),
            ),
            encoding="utf-8",
        )

        # Project diff: always attempted, no-op outside git. Cheap, always
        # useful when a write actually happened.
        diff = capture_git_diff(args.tcunit_path)
        if diff is not None:
            diff_path = out_path.with_suffix(".diff")
            diff_path.write_text(diff, encoding="utf-8")

        # Bridge-driven build verification: explicit opt-in, gated on a
        # solution path. Result lands in <stem>.build.json so the reviewer
        # can see whether each config's change actually compiles.
        if args.build_after_each:
            print(" build...", end="", flush=True)
            build_resp = build_project(args.bridge_url, args.sln_path)
            build_path = out_path.with_suffix(".build.json")
            build_path.write_text(
                json.dumps(build_resp, indent=2, ensure_ascii=False),
                encoding="utf-8",
            )

        # Closed-loop test validation for bug-hunting fixtures: re-save the
        # library (the model's edits need to reach the installed .library
        # before the tests PLC is rebuilt), then build -> deploy -> start
        # -> run_tests -> probe. The harness's reading is authoritative;
        # ADR-0007 calls out the "model said pass, harness saw fail"
        # discrepancy as a finding worth surfacing.
        test_result: dict[str, Any] | None = None
        if args.post_run_tests:
            print(" tests...", end="", flush=True)
            cycle = run_test_cycle(
                bridge_url=args.bridge_url,
                sln_path=args.sln_path,
                library_plc=args.pre_save_as_library,
                tests_plc=args.post_run_tests,
                target_ams_id=os.environ["TARGET_AMS_ID"],
                probes=test_probes,
            )
            passed = derive_pass_fail(cycle.get("probes") or {})
            test_result = {
                "pre_save_result": pre_save_result,
                **cycle,
                "passed": passed,
                "tests_modified": False,
                "tests_modified_files": [],
            }

        if args.tests_guard_path:
            modified = check_tests_modified(
                str(args.repo_root), args.tests_guard_path
            )
            if test_result is None:
                test_result = {
                    "tests_modified": bool(modified),
                    "tests_modified_files": modified,
                    "passed": None,
                }
            else:
                test_result["tests_modified"] = bool(modified)
                test_result["tests_modified_files"] = modified
                if modified:
                    # Tamper invalidates the run regardless of probe outcome
                    # (per ADR-0007's tamper-guard rule).
                    test_result["passed"] = False

        if test_result is not None:
            test_path = out_path.with_suffix(".test-result.json")
            test_path.write_text(
                json.dumps(test_result, indent=2, ensure_ascii=False),
                encoding="utf-8",
            )

        if metrics["exit_code"] == 0:
            print(
                f"OK  calls={metrics['tool_call_count']:<3}  "
                f"tokens={metrics['total_tokens']:<6}  "
                f"wall={metrics['wall_clock_seconds']:.1f}s"
            )
        else:
            print(f"FAILED  exit={metrics['exit_code']}  see {out_path.name}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
