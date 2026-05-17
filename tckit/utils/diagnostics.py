"""Diagnostic helpers used by ``tckit config validate`` and ``tckit doctor``.

Functions here are deliberately pure (no I/O beyond the bridge ping) so they
remain easy to unit-test. The CLI layer at :mod:`tckit.cli` is the one that
prints results to stdout.
"""

from __future__ import annotations

import re

from tckit.config import TcKitConfig, _user_home
from tckit.utils.bridge_client import BridgeClient

_AMS_NETID_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+\.\d+\.\d+$")


def config_file_status(cfg: TcKitConfig) -> tuple[bool, bool]:
    """Return ``(config_file_exists, target_ams_id_set)``.

    Pure helper for the doctor's first section. ``target_ams_id_set`` covers
    both the file and the env-var paths so a user who only exports
    ``TARGET_AMS_ID`` (no file) still reads as "set".
    """
    file_exists = (_user_home() / "config.toml").exists()
    target_set = bool(cfg.get("TARGET_AMS_ID"))
    return file_exists, target_set


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


def bridge_dependencies(url: str | None = None) -> dict[str, str | None]:
    """Return the bridge's reported PowerShell-module dependencies.

    Each value is the installed version string, or ``None`` if the module
    is missing on the bridge's PSModulePath. Returns an empty dict when the
    bridge is unreachable or its /health response lacks a dependencies
    block (older bridges).
    """
    client = BridgeClient(base_url=url) if url else BridgeClient()
    try:
        try:
            resp = client.get("/health", timeout=2.0)
        except Exception:  # noqa: BLE001 — bridge errors absorbed; caller handles via bridge_health
            return {}
    finally:
        client.close()
    deps = resp.get("dependencies")
    if not isinstance(deps, dict):
        return {}
    return {str(k): (str(v) if v is not None else None) for k, v in deps.items()}


def tcunit_xml_status(url: str | None = None) -> tuple[bool, bool, list[str]]:
    """Check whether the bridge can resolve a TcUnit XML output path.

    Calls the bridge's ``/tcunit-xml-resolve`` route which mirrors
    ``Get-TcUnitDefaultXmlPath``'s ladder. Returns
    ``(ok, warn, lines)``:

    - ``ok=True, warn=False``: env override resolves, or exactly one
      candidate (kernel or single UmRT) is present.
    - ``ok=True, warn=True``: multiple UmRT candidates; freshest will
      be used. Operator should pin via ``TCKIT_TCUNIT_XML_PATH`` if
      ambiguous.
    - ``ok=False``: zero candidates; tests will not be readable.

    ``lines`` is the human-readable detail for ``tckit doctor`` to
    print. Older bridges that don't have the route return
    ``(True, False, ["route not available; upgrade bridge"])`` so the
    doctor doesn't fail closed on the version skew. See ADR-0011.
    """
    client = BridgeClient(base_url=url) if url else BridgeClient()
    try:
        try:
            resp = client.post("/tcunit-xml-resolve", {}, timeout=5.0)
        except Exception as exc:  # noqa: BLE001
            return False, False, [f"error contacting bridge: {exc}"]
    finally:
        client.close()

    if not resp.get("success", True):
        err = resp.get("error") or "unknown error"
        if "not found" in str(err).lower() or "unknown" in str(err).lower():
            return True, False, ["route not available; upgrade bridge"]
        return False, False, [str(err)]

    env_override = resp.get("env_override")
    env_exists = bool(resp.get("env_exists", False))
    kernel_path = resp.get("kernel_path", "")
    kernel_exists = bool(resp.get("kernel_exists", False))
    umrt = resp.get("umrt_candidates") or []

    if env_override and env_exists:
        return True, False, [f"env override resolves: {env_override}"]
    if env_override and not env_exists:
        return False, False, [
            f"env override TCKIT_TCUNIT_XML_PATH set to {env_override} "
            "but file does not exist"
        ]
    if kernel_exists:
        return True, False, [f"kernel-RT path resolves: {kernel_path}"]
    if len(umrt) == 1:
        return True, False, [f"UmRT path resolves: {umrt[0].get('path')}"]
    if len(umrt) > 1:
        paths = [str(c.get("path", "")) for c in umrt]
        lines = [
            f"multiple UmRT candidates ({len(paths)}); freshest will be used:",
            f"  freshest: {paths[0]}",
        ]
        lines.extend(f"  alt:      {p}" for p in paths[1:])
        lines.append(
            "  pin via TCKIT_TCUNIT_XML_PATH in ~/.tckit/config.toml if ambiguous."
        )
        return True, True, lines
    return False, False, [
        f"no TcUnit XML found. Searched: {kernel_path}",
        "and %ProgramData%\\Beckhoff\\TwinCAT\\3.1\\Runtimes\\*\\3.1\\Boot\\tcunit_xunit_testresults.xml",
        "Run TcUnit tests once to populate, or set TCKIT_TCUNIT_XML_PATH.",
    ]


def install_bridge_dependency(
    name: str, url: str | None = None
) -> tuple[bool, str]:
    """POST to the bridge's ``/install-dependency`` route to install ``name``.

    Returns ``(ok, message)``. The bridge enforces an allow-list of module
    names; unknown names come back as a failure with an explanatory error.
    """
    client = BridgeClient(base_url=url) if url else BridgeClient()
    try:
        try:
            resp = client.post(
                "/install-dependency", {"name": name}, timeout=120.0
            )
        except Exception as exc:  # noqa: BLE001 — bridge errors surfaced verbatim
            return False, f"error contacting bridge: {exc}"
    finally:
        client.close()

    if resp.get("success"):
        details = resp.get("details") or {}
        version = details.get("version") or "unknown"
        return True, f"installed {name} {version}"
    return False, str(resp.get("error") or f"unknown install failure for {name}")
