"""Best-effort auto-spawn of the local Windows bridge service.

When the MCP server starts and the bridge on localhost:8765 is down, start
``Start-Bridge.ps1`` so the operator doesn't have to launch it manually
(issue #121). Binding to localhost needs no elevation.

It's a no-op (returning the current reachability) when:
  - the bridge is already up,
  - not on Windows (the bridge needs PowerShell + COM),
  - ``BRIDGE_URL`` points at a non-local host (Docker / remote bridge),
  - auto-spawn is disabled via ``TCKIT_BRIDGE_AUTOSPAWN=0``,
  - the launcher script can't be found.

Lives under ``tckit/utils`` so the server can call it without importing the
CLI; the launcher-path resolution mirrors ``tckit bridge install``.
"""

from __future__ import annotations

import os
import subprocess
import sys
import time
from pathlib import Path
from urllib.parse import urlparse

from tckit.utils.bridge_client import DEFAULT_BRIDGE_URL, BridgeClient

_LOCAL_HOSTS = {"localhost", "127.0.0.1", "::1", ""}


def _user_home() -> Path:
    override = os.getenv("TCKIT_HOME")
    return Path(override) if override else Path.home() / ".tckit"


def _find_launcher() -> Path | None:
    """Locate ``Start-Bridge.ps1``: installed copy first, then bundled tree."""
    candidates = [
        _user_home() / "bridge" / "Start-Bridge.ps1",
        Path(__file__).resolve().parent.parent / "_bridge" / "Start-Bridge.ps1",
        Path(__file__).resolve().parent.parent.parent / "bridge" / "Start-Bridge.ps1",
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def _is_local(url: str) -> bool:
    return (urlparse(url).hostname or "").lower() in _LOCAL_HOSTS


def ensure_bridge_running(bridge_url: str | None = None, *, timeout: float = 20.0) -> bool:
    """Start the local bridge if it's down. Returns True when reachable.

    Best-effort and non-fatal: any failure to spawn just returns False, and
    the usual ``BridgeUnavailableError`` surfaces later if a tool actually
    needs the bridge.
    """
    url = bridge_url or os.getenv("BRIDGE_URL") or DEFAULT_BRIDGE_URL
    client = BridgeClient(base_url=url)
    try:
        if client.health():
            return True
        if os.getenv("TCKIT_BRIDGE_AUTOSPAWN", "1") == "0":
            return False
        if sys.platform != "win32" or not _is_local(url):
            return False
        launcher = _find_launcher()
        if launcher is None:
            return False

        port = urlparse(url).port or 8765
        home = _user_home()
        try:
            home.mkdir(parents=True, exist_ok=True)
            log = open(home / "bridge.log", "ab")  # noqa: SIM115 — handed to the detached child
        except OSError:
            return False

        creationflags = 0
        detached = getattr(subprocess, "DETACHED_PROCESS", 0)
        new_group = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        creationflags = detached | new_group

        try:
            subprocess.Popen(
                [
                    "powershell.exe",
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(launcher),
                    "-Port",
                    str(port),
                ],
                stdout=log,
                stderr=log,
                stdin=subprocess.DEVNULL,
                creationflags=creationflags,
                close_fds=True,
            )
        except OSError:
            log.close()
            return False

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if client.health():
                return True
            time.sleep(0.5)
        return False
    finally:
        client.close()
