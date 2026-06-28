"""HTTP client for the Windows bridge service.

Used by both the automation_writer and xae_com_builder adapters. Lives
under tckit/utils/ so adapters can share it without importing each other.
"""

from __future__ import annotations

import os
from typing import Any

import httpx

DEFAULT_BRIDGE_URL = "http://localhost:8765"
DEFAULT_TIMEOUT = 60.0

# Per-route HTTP-timeout defaults for bridge calls. Each value is the
# ceiling we expect a route to need against a cold target on a typical
# bench machine; routes not listed here fall back to ``DEFAULT_TIMEOUT``.
# Some routes also honour an env var override (see ``ROUTE_TIMEOUT_ENV``)
# so operators can extend the ceiling without a code change.
ROUTE_TIMEOUT_DEFAULTS: dict[str, float] = {
    "/build": 600.0,
    "/deploy": 300.0,
    "/runtime": 180.0,
    "/tcunit-run": 600.0,
    "/results": 60.0,
    "/save-as-library": 180.0,
    "/install-dependency": 120.0,
    # Attaching to XAE and enumerating PLC projects; quick on a warm
    # instance, but allow headroom for a cold attach.
    "/active-solution": 30.0,
    # ADS read to EtherCAT master — fast on a reachable target.
    "/ethercat-status": 30.0,
    # MDP module enumeration + per-module reads — allow for many modules.
    "/ipc-hardware": 30.0,
    # NC axis enumeration + per-axis state reads.
    "/nc-axes": 30.0,
    # XAE COM tree navigation for I/O topology — allow for cold XAE attach.
    "/hardware-scan": 120.0,
}

ROUTE_TIMEOUT_ENV: dict[str, str] = {
    "/build": "TCKIT_BUILD_TIMEOUT",
    "/tcunit-run": "TCKIT_TEST_RUN_TIMEOUT",
}


def route_timeout(path: str) -> float:
    """Resolve the HTTP timeout for a bridge ``path``.

    Env var overrides win over the static defaults. Routes that aren't
    in either map fall back to ``DEFAULT_TIMEOUT``.
    """
    if not path.startswith("/"):
        path = "/" + path
    env_var = ROUTE_TIMEOUT_ENV.get(path)
    if env_var:
        raw = os.getenv(env_var)
        if raw:
            try:
                return float(raw)
            except ValueError:
                pass
    return float(ROUTE_TIMEOUT_DEFAULTS.get(path, DEFAULT_TIMEOUT))


class BridgeError(Exception):
    """Base class for bridge-client errors."""


class BridgeUnavailableError(BridgeError):
    """The bridge service is not reachable at the configured URL."""


class BridgeClient:
    """Thin wrapper around httpx for talking to the Windows bridge service.

    Reads ``BRIDGE_URL`` from the environment (falling back to
    ``localhost:8765``). HTTP timeouts are resolved per-route via
    :func:`route_timeout`; callers don't need to pass a ``timeout=``
    unless they're talking to an unmapped route or want to override
    the per-route default.
    """

    def __init__(
        self,
        base_url: str | None = None,
        timeout: float = DEFAULT_TIMEOUT,
    ) -> None:
        self._base_url = (base_url or os.getenv("BRIDGE_URL") or DEFAULT_BRIDGE_URL).rstrip("/")
        self._timeout = timeout
        self._client: httpx.Client | None = None

    @property
    def base_url(self) -> str:
        return self._base_url

    def _get_client(self) -> httpx.Client:
        if self._client is None:
            self._client = httpx.Client(base_url=self._base_url, timeout=self._timeout)
        return self._client

    def close(self) -> None:
        if self._client is not None:
            self._client.close()
            self._client = None

    # ------------------------------------------------------------------
    # Request helpers
    # ------------------------------------------------------------------

    def post(
        self,
        path: str,
        payload: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        """POST a JSON payload to ``path`` and return the parsed JSON response.

        On HTTP 5xx, the response body (if JSON) is still returned so the
        caller can read its ``error`` field. On connection errors,
        :class:`BridgeUnavailableError` is raised. ``timeout=None``
        defaults to the per-route value from :func:`route_timeout`.
        """
        return self._request("POST", path, json=payload or {}, timeout=timeout)

    def get(self, path: str, timeout: float | None = None) -> dict[str, Any]:
        return self._request("GET", path, json=None, timeout=timeout)

    def health(self) -> bool:
        """Return True if /health responds with status=ok.

        Any bridge-level failure counts as "not healthy" — not just an
        unreachable bridge (``BridgeUnavailableError``) but also a
        connect/read timeout (``BridgeError``). A connect to a dead local
        port can *time out* rather than be refused (e.g. an IPv6 ``::1``
        attempt that hangs when nothing is listening), so callers like the
        auto-spawn must read that as "bridge down" instead of crashing.
        """
        try:
            resp = self.get("/health", timeout=2.0)
        except BridgeError:
            return False
        return resp.get("status") == "ok"

    def active_solution(self) -> str | None:
        """Return the .sln path open in the attached XAE, or None.

        Best-effort like :meth:`health`: an unreachable bridge or no open
        solution yields None rather than raising, so callers can fall back
        to their own context-specific error.
        """
        try:
            resp = self.get("/active-solution")
        except BridgeError:
            return None
        if resp.get("success") and resp.get("solution_path"):
            return str(resp["solution_path"])
        return None

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _request(
        self,
        method: str,
        path: str,
        json: dict[str, Any] | None,
        timeout: float | None,
    ) -> dict[str, Any]:
        client = self._get_client()
        if not path.startswith("/"):
            path = "/" + path

        effective_timeout = timeout if timeout is not None else route_timeout(path)

        try:
            response = client.request(method, path, json=json, timeout=effective_timeout)
        except httpx.ConnectError as exc:
            raise BridgeUnavailableError(
                f"Bridge not reachable at {self._base_url} ({exc})"
            ) from exc
        except httpx.TimeoutException as exc:
            raise BridgeError(
                f"Bridge timed out after {effective_timeout}s on {path}"
            ) from exc

        # Always try to parse JSON. PowerShell harness returns JSON even on errors.
        try:
            return response.json()  # type: ignore[no-any-return]
        except ValueError:
            snippet = response.text[:200]
            return {
                "success": False,
                "error": f"Non-JSON response from bridge ({response.status_code}): {snippet}",
            }


def build_timeout() -> float:
    """Resolve the timeout for /build calls. Thin wrapper over
    :func:`route_timeout` for backwards compatibility — new callers
    should not need to invoke this directly because ``BridgeClient.post``
    already routes ``/build`` through the per-route map.
    """
    return route_timeout("/build")
