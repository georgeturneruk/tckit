"""plc_resolver — pick the PLC-project name to operate on within a sln.

Mirrors the bridge's ``Resolve-TcPlcName`` (bridge/harness/_TcDte.psm1):
explicit name wins, then ``PLC_PROJECT_NAME`` env default, then auto-resolve
if exactly one PLC project is present, otherwise raise listing the
candidates. Used by reader and writer adapters to keep multi-project sln
behaviour symmetric between Python and PowerShell. See ADR-0005.
"""

from __future__ import annotations

import os
from collections.abc import Iterable


class AmbiguousPLCProjectError(ValueError):
    """Raised when no plc_name was given and the sln has multiple PLC projects."""


def resolve_plc_name(explicit: str | None, available: Iterable[str]) -> str:
    """Pick the PLC-project name for this call.

    Resolution order:
      1. ``explicit`` if non-empty
      2. ``PLC_PROJECT_NAME`` env var if set and present in ``available``
      3. the only entry in ``available`` if there is exactly one
      4. ``AmbiguousPLCProjectError`` listing all candidates

    Raises:
        FileNotFoundError: if ``available`` is empty (no .plcproj in the sln).
        AmbiguousPLCProjectError: if no name can be picked deterministically.
        ValueError: if an explicit or env name does not match any available
            PLC project.
    """
    names = list(available)
    if not names:
        raise FileNotFoundError(
            "No PLC projects (.plcproj) found in the solution."
        )

    if explicit:
        if explicit not in names:
            raise ValueError(
                f"plc_name {explicit!r} does not match any PLC project in the "
                f"solution. Available: {', '.join(sorted(names))}."
            )
        return explicit

    env_default = os.getenv("PLC_PROJECT_NAME", "").strip()
    if env_default:
        if env_default not in names:
            raise ValueError(
                f"PLC_PROJECT_NAME env var is {env_default!r} but no matching "
                f"PLC project exists. Available: {', '.join(sorted(names))}."
            )
        return env_default

    if len(names) == 1:
        return names[0]

    raise AmbiguousPLCProjectError(
        f"Multiple PLC projects in the solution ({', '.join(sorted(names))}). "
        "Pass plc_name to disambiguate, or set PLC_PROJECT_NAME in the "
        "environment."
    )
