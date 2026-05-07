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


class BridgeError(Exception):
    """Base class for bridge-client errors."""


class BridgeUnavailableError(BridgeError):
    """The bridge service is not reachable at the configured URL."""


class BridgeClient:
    """Thin wrapper around httpx for talking to the Windows bridge service.

    Reads ``BRIDGE_URL`` from the environment (falling back to localhost:8765).
    Reads ``TCKIT_BUILD_TIMEOUT`` for the build endpoint specifically — builds
    can take many minutes and need a longer ceiling than the default 60s.
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
        :class:`BridgeUnavailableError` is raised.
        """
        return self._request("POST", path, json=payload or {}, timeout=timeout)

    def get(self, path: str, timeout: float | None = None) -> dict[str, Any]:
        return self._request("GET", path, json=None, timeout=timeout)

    def health(self) -> bool:
        """Return True if /health responds with status=ok."""
        try:
            resp = self.get("/health", timeout=2.0)
        except BridgeUnavailableError:
            return False
        return resp.get("status") == "ok"

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

        try:
            response = client.request(method, path, json=json, timeout=timeout or self._timeout)
        except httpx.ConnectError as exc:
            raise BridgeUnavailableError(
                f"Bridge not reachable at {self._base_url} ({exc})"
            ) from exc
        except httpx.TimeoutException as exc:
            effective = timeout or self._timeout
            raise BridgeError(
                f"Bridge timed out after {effective}s on {path}"
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
    """Resolve the timeout for /build calls from env (default 600s)."""
    raw = os.getenv("TCKIT_BUILD_TIMEOUT")
    if not raw:
        return 600.0
    try:
        return float(raw)
    except ValueError:
        return 600.0
