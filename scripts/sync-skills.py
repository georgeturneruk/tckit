#!/usr/bin/env python3
"""Mirror ``.claude/skills/`` to ``plugin/skills/``.

The repo carries skills in two places by necessity: ``.claude/skills/`` is
read by Claude Code when working *on* TcKit (developer DX), and
``plugin/skills/`` is what the Claude Code marketplace ships to end users
who install the plugin. The plugin manifest declares
``"skills": "./skills/"``, so the bundle must contain its own copy.

CI enforces parity with ``diff -r .claude/skills/ plugin/skills/`` (see
``.github/workflows/ci.yml``). This script makes the fix a one-liner.

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


def _drift(src: Path, dst: Path) -> list[str]:
    """Walk both trees and return a list of differing relative paths."""
    differences: list[str] = []
    cmp = filecmp.dircmp(src, dst)
    differences.extend(f"only in {src.name}: {n}" for n in cmp.left_only)
    differences.extend(f"only in {dst.name}: {n}" for n in cmp.right_only)
    differences.extend(f"differs: {n}" for n in cmp.diff_files)
    for sub in cmp.common_dirs:
        differences.extend(_drift(src / sub, dst / sub))
    return differences


def main(argv: list[str]) -> int:
    check_only = "--check" in argv

    if not SRC.exists():
        print(f"source missing: {SRC}", file=sys.stderr)
        return 2

    if check_only:
        if not DST.exists():
            print(f"target missing: {DST}", file=sys.stderr)
            return 1
        diffs = _drift(SRC, DST)
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
    shutil.copytree(SRC, DST)
    print(f"Synced {SRC.relative_to(ROOT).as_posix()} -> {DST.relative_to(ROOT).as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
