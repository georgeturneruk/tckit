#!/usr/bin/env python3
"""Enforce the one architectural rule: adapters may only import from
``tckit.ports``, ``tckit.utils``, or stdlib — never from each other.

Within-adapter imports (``tckit.adapters.<self>._helper``) are allowed —
each adapter folder may have its own private helpers.

Run from the repo root:
    python scripts/check-adapter-isolation.py

Exits 0 on success, 1 on any violation (also prints the violations).
"""

from __future__ import annotations

import ast
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ADAPTERS_ROOT = REPO_ROOT / "tckit" / "adapters"


def _adapter_of(path: Path) -> str | None:
    """Return the adapter folder name (e.g. 'readers/xml_reader') for a file
    under ``tckit/adapters``, or None if the file isn't in an adapter folder.

    The 'self' identifier we compare against is the immediate parent folder
    name — adapters live one level down from ``tckit/adapters``.
    """
    try:
        rel = path.relative_to(ADAPTERS_ROOT)
    except ValueError:
        return None
    parts = rel.parts
    if len(parts) < 2:
        # e.g. tckit/adapters/__init__.py — not inside any adapter folder
        return None
    return parts[0]


def _imported_module(node: ast.AST) -> str | None:
    """Return the dotted module name an Import / ImportFrom node references."""
    if isinstance(node, ast.ImportFrom):
        return node.module  # may be None for `from . import x`
    if isinstance(node, ast.Import):
        return node.names[0].name if node.names else None
    return None


def _check_file(path: Path) -> list[str]:
    """Return a list of human-readable violations for one file."""
    self_kind = _adapter_of(path)
    if self_kind is None:
        return []

    src = path.read_text(encoding="utf-8")
    try:
        tree = ast.parse(src, filename=str(path))
    except SyntaxError as exc:
        return [f"{path}: syntax error — {exc}"]

    violations: list[str] = []
    for node in ast.walk(tree):
        if not isinstance(node, ast.Import | ast.ImportFrom):
            continue
        module = _imported_module(node)
        if not module or not module.startswith("tckit.adapters."):
            continue
        # tckit.adapters.<kind>[.<rest>]
        parts = module.split(".")
        other_kind = parts[2] if len(parts) >= 3 else None
        if other_kind == self_kind:
            continue  # within-adapter helper
        line = getattr(node, "lineno", "?")
        violations.append(
            f"{path.relative_to(REPO_ROOT)}:{line}: "
            f"adapter '{self_kind}' imports from adapter '{other_kind}' "
            f"({module})"
        )
    return violations


def main() -> int:
    if not ADAPTERS_ROOT.is_dir():
        print(f"error: {ADAPTERS_ROOT} not found", file=sys.stderr)
        return 2

    all_violations: list[str] = []
    for path in sorted(ADAPTERS_ROOT.rglob("*.py")):
        all_violations.extend(_check_file(path))

    if all_violations:
        print("Adapter-isolation violations:")
        for v in all_violations:
            print(f"  {v}")
        print(
            "\nAdapters may only import from tckit.ports, tckit.utils, "
            "or stdlib. Move shared logic into tckit/utils/."
        )
        return 1

    print("Adapter isolation: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
