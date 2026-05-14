"""Shared helper that maps a bridge JSON response onto a ``Result``."""

from __future__ import annotations

from typing import Any

from tckit.ports.types import Result


def to_result(resp: dict[str, Any]) -> Result:
    """Map a bridge POST response onto a ``Result`` dataclass.

    Bridge routes return JSON with ``success`` / ``error`` at the top level
    and any route-specific fields alongside them. Those extras become the
    ``details`` payload so callers can reach in without per-route helpers.
    """
    return Result(
        success=bool(resp.get("success", False)),
        error=resp.get("error"),
        details={k: v for k, v in resp.items() if k not in ("success", "error")},
    )
