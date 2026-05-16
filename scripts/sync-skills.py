#!/usr/bin/env python3
"""Mirror ``.claude/skills/`` to ``plugin/skills/``.

The repo carries skills in two places by necessity: ``.claude/skills/`` is
read by Claude Code when working *on* TcKit (developer DX), and
``plugin/skills/`` is what the Claude Code marketplace ships to end users
who install the plugin. The plugin manifest declares
``"skills": "./skills/"``, so the bundle must contain its own copy.

Some skills are internal to TcKit development and must NOT ship to users
(e.g. a skill that fires on "write an ADR" makes no sense in a TwinCAT
user's project). Those are listed in ``INTERNAL`` below; the sync skips
them and the check tolerates their absence from ``plugin/skills/``.

CI enforces parity with ``python scripts/sync-skills.py --check`` (see
``.github/workflows/ci.yml``). This script makes the fix a one-liner.

For guidance on adding a new skill (user-facing vs internal, where to
edit, what the ``INTERNAL`` set is for), see ``CONTRIBUTING.md``.

Usage::

    python scripts/sync-skills.py           # mirror .claude → plugin
    python scripts/sync-skills.py --check   # exit 1 if they have drifted

Stdlib only; runs anywhere Python 3.11+ does.
"""

from __future__ import annotations

import filecmp
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / ".claude" / "skills"
DST = ROOT / "plugin" / "skills"

# Skills that live in .claude/skills/ for TcKit development but must NOT
# ship to TcKit users via the plugin. Match on the top-level folder name.
INTERNAL: frozenset[str] = frozenset({"tc-docs-write", "tc-adr"})


def _drift(src: Path, dst: Path, *, top_level: bool = False) -> list[str]:
    """Walk both trees and return a list of differing relative paths.

    At the top level, INTERNAL skill folders are expected to be absent
    from ``dst`` (the plugin tree); their presence in ``src`` only is not
    drift. An INTERNAL name appearing in ``dst`` IS drift — that means
    someone copied it across by hand.
    """
    differences: list[str] = []
    cmp = filecmp.dircmp(src, dst)
    left_only = [n for n in cmp.left_only if not (top_level and n in INTERNAL)]
    differences.extend(f"only in {src.name}: {n}" for n in left_only)
    differences.extend(f"only in {dst.name}: {n}" for n in cmp.right_only)
    differences.extend(f"differs: {n}" for n in cmp.diff_files)
    for sub in cmp.common_dirs:
        differences.extend(_drift(src / sub, dst / sub))
    return differences


def _ignore_internal(directory: str, contents: list[str]) -> list[str]:
    """``copytree`` ignore callback: skip INTERNAL skills at the source root."""
    if Path(directory).resolve() == SRC.resolve():
        return [n for n in contents if n in INTERNAL]
    return []


def main(argv: list[str]) -> int:
    check_only = "--check" in argv

    if not SRC.exists():
        print(f"source missing: {SRC}", file=sys.stderr)
        return 2

    if check_only:
        if not DST.exists():
            print(f"target missing: {DST}", file=sys.stderr)
            return 1
        diffs = _drift(SRC, DST, top_level=True)
        if diffs:
            print("Skill trees have drifted:")
            for d in diffs:
                print(f"  {d}")
            print(f"\nRun `python {Path(__file__).relative_to(ROOT).as_posix()}` to fix.")
            return 1
        print("Skill trees are in sync.")
        return 0

    if DST.exists():
        shutil.rmtree(DST)
    shutil.copytree(SRC, DST, ignore=_ignore_internal)
    skipped = ", ".join(sorted(INTERNAL)) if INTERNAL else "(none)"
    print(f"Synced {SRC.relative_to(ROOT).as_posix()} -> {DST.relative_to(ROOT).as_posix()}")
    print(f"Skipped internal skills: {skipped}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
