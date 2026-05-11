#!/usr/bin/env python3
"""Repair cp1252→UTF-8 mojibake in bench result files.

The bench runner used to capture the ``claude`` CLI's stdout with the
default text-mode decoding, which on Windows is cp1252. The CLI emits
UTF-8, so any non-ASCII character (em-dash, smart quote, accented
letter) was rewritten as the multi-byte sequence's cp1252 spelling
(e.g. ``—`` → ``â€"``). Once written to JSON the corruption is
deterministic and reversible.

This script walks every ``bench/results/*.json``, attempts a strict
``cp1252 → utf-8`` round-trip on every string value, rewrites the JSON
with the recovered text, and re-renders the matching ``.md`` sibling
from the ``final_text`` field.

The runner has been fixed (``bench/run.py`` now passes
``encoding="utf-8"`` to ``subprocess.run``) so new runs are clean. This
utility exists for retroactively cleaning prior runs.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

# Reuse the markdown renderer from run.py so the .md preamble stays
# consistent with how new runs are emitted.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from run import render_markdown  # noqa: E402

RESULTS_DIR = Path(__file__).resolve().parent / "results"


def _looks_corrupt(s: str) -> bool:
    """Cheap heuristic: only attempt repair when the string contains the
    leading bytes of a UTF-8 multi-byte sequence interpreted as cp1252.
    Pure ASCII strings are left alone — round-tripping them is a no-op
    but also wastes work.
    """
    return any(0xC0 <= ord(c) <= 0xFD for c in s)


def repair_string(s: str) -> str:
    """Attempt the cp1252 → utf-8 round-trip; return the original on failure."""
    if not _looks_corrupt(s):
        return s
    try:
        return s.encode("cp1252").decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return s


def repair_value(value: Any) -> Any:
    """Walk a JSON value, repairing every string in place."""
    if isinstance(value, str):
        return repair_string(value)
    if isinstance(value, list):
        return [repair_value(item) for item in value]
    if isinstance(value, dict):
        return {k: repair_value(v) for k, v in value.items()}
    return value


def repair_file(json_path: Path) -> bool:
    """Repair one ``.json`` result and re-render its ``.md`` sibling.

    Returns True if anything changed.
    """
    original = json_path.read_text(encoding="utf-8")
    data = json.loads(original)
    repaired = repair_value(data)

    new_text = json.dumps(repaired, indent=2, ensure_ascii=False)
    changed = new_text != original
    if changed:
        json_path.write_text(new_text, encoding="utf-8")

    metrics = dict(repaired.get("metrics") or {})
    metrics["final_text"] = repaired.get("final_text")
    md = render_markdown(
        task=repaired.get("task", json_path.stem),
        config=repaired.get("config", ""),
        run=repaired.get("run", 1),
        timestamp=repaired.get("timestamp_utc", ""),
        tcunit_path=repaired.get("tcunit_path", ""),
        metrics=metrics,
        final_text=repaired.get("final_text"),
    )
    md_path = json_path.with_suffix(".md")
    md_existing = md_path.read_text(encoding="utf-8") if md_path.exists() else None
    if md_existing != md:
        md_path.write_text(md, encoding="utf-8")
        changed = True

    return changed


def main() -> int:
    if not RESULTS_DIR.exists():
        print(f"results directory missing: {RESULTS_DIR}", file=sys.stderr)
        return 2
    repaired_count = 0
    for json_path in sorted(RESULTS_DIR.glob("*.json")):
        if repair_file(json_path):
            repaired_count += 1
            print(f"repaired {json_path.name}")
    print(f"\n{repaired_count} file(s) updated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
