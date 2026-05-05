#!/usr/bin/env python3
"""Phase 1 spike: validate blark can parse a .TcPOU or .TcGVL file.

Usage:
    python scripts/validate-blark.py path/to/FB_Example.TcPOU
"""

import sys
from pathlib import Path


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: validate-blark.py <path-to-.TcPOU>")
        sys.exit(1)

    path = Path(sys.argv[1])
    if not path.exists():
        print(f"File not found: {path}")
        sys.exit(1)

    try:
        import blark  # noqa: F401
    except ImportError:
        print("blark not installed. Run: pip install blark")
        sys.exit(1)

    print(f"Parsing: {path}")
    print(f"File size: {path.stat().st_size} bytes")
    print()

    try:
        from blark import parse

        result = parse(path.read_text(encoding="utf-8"))
        print("Parse SUCCESS")
        print(f"Result type: {type(result).__name__}")
        print()
        print(repr(result))
    except Exception as exc:
        print(f"Parse FAILED: {type(exc).__name__}: {exc}")
        sys.exit(1)


if __name__ == "__main__":
    main()
