"""Diagnostic helpers used by ``tckit config validate`` and ``tckit doctor``.

Functions here are deliberately pure (no I/O beyond the bridge ping) so they
remain easy to unit-test. The CLI layer at :mod:`tckit.cli` is the one that
prints results to stdout.
"""

from __future__ import annotations

import re

from tckit.config import TcKitConfig
from tckit.utils.bridge_client import BridgeClient

_AMS_NETID_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+\.\d+\.\d+$")


def is_valid_ams_netid(value: str) -> bool:
    """Return True if ``value`` looks like a six-octet AMS Net ID.

    Example: ``192.168.1.5.1.1``.

    Each octet is required; we don't enforce 0-255 because exotic local
    routes can have larger numeric components in some Beckhoff setups.
    """
    if not value:
        return False
    return _AMS_NETID_RE.match(value) is not None


def validate_config(cfg: TcKitConfig) -> list[str]:
    """Return a list of human-readable issues with ``cfg``, empty if all good.

    Catches the usual setup typos: malformed AMS Net IDs in TARGET_AMS_ID,
    ALLOWED_NETIDS, or BLOCKED_NETIDS. Does not check that referenced
    paths exist; that's :func:`bridge_health`'s job to surface indirectly.
    """
    issues: list[str] = []

    target = cfg.get("TARGET_AMS_ID")
    if target and not is_valid_ams_netid(target):
        issues.append(
            f"TARGET_AMS_ID={target!r} is not a valid AMS Net ID "
            "(expected six dot-separated octets, e.g. 192.168.1.5.1.1)"
        )

    for var in ("ALLOWED_NETIDS", "BLOCKED_NETIDS"):
        raw = cfg.get(var, "")
        if not raw:
            continue
        for netid in [n.strip() for n in str(raw).split(",") if n.strip()]:
            if not is_valid_ams_netid(netid):
                issues.append(
                    f"{var} contains invalid NetId {netid!r} "
                    "(expected six dot-separated octets)"
                )

    return issues


def bridge_health(url: str | None = None) -> tuple[bool, str]:
    """Ping the bridge ``/health`` endpoint. Returns ``(ok, message)``.

    ``url`` overrides the default. If ``url`` is None, ``BridgeClient`` picks
    it from the ``BRIDGE_URL`` env var or its default of ``http://localhost:8765``.
    """
    client = BridgeClient(base_url=url) if url else BridgeClient()
    base = client.base_url
    try:
        ok = client.health()
    except Exception as exc:  # noqa: BLE001 — surface any bridge error verbatim
        return False, f"error contacting {base}: {exc}"
    finally:
        client.close()

    if ok:
        return True, f"reachable at {base}"
    return False, f"not reachable at {base}"
