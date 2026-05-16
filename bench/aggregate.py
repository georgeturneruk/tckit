#!/usr/bin/env python3
"""bench/aggregate.py — summarise TcKit benchmark results.

Reads ``bench/results/*.json``, groups by ``(task, config)``, prints a
table of per-pair means with standard deviation, and shows the
vanilla:tckit ratio per task.

Usage::

    python bench/aggregate.py
    python bench/aggregate.py --filter 01-orient
"""

from __future__ import annotations

import argparse
import json
import pathlib
import statistics
from collections import defaultdict
from typing import Any


def _fmt(values: list[float], *, decimals: int = 0) -> str:
    if not values:
        return "n/a"
    if len(values) == 1:
        return f"{values[0]:.{decimals}f}"
    mean = statistics.mean(values)
    stdev = statistics.stdev(values)
    return f"{mean:.{decimals}f} (±{stdev:.{decimals}f})"


def _ratio(numerator: float, denominator: float) -> str:
    if denominator == 0:
        return "n/a"
    return f"{numerator / denominator:.2f}×"


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarise TcKit benchmark runs.")
    parser.add_argument("--results-dir", type=pathlib.Path,
                        default=pathlib.Path(__file__).parent / "results",
                        help="Directory containing per-run result JSONs.")
    parser.add_argument("--filter", default=None,
                        help="Substring filter on result filenames.")
    args = parser.parse_args()

    if not args.results_dir.exists():
        print(f"results directory not found: {args.results_dir}")
        return 1

    by_pair: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    builds_by_pair: dict[tuple[str, str], list[bool]] = defaultdict(list)

    for path in sorted(args.results_dir.glob("*.json")):
        # Skip the build- and test-result siblings — they're sibling
        # artefacts of a main run JSON, not standalone runs. Pass-rate /
        # iteration-count aggregation lives in a separate PR.
        if path.name.endswith(".build.json"):
            continue
        if path.name.endswith(".test-result.json"):
            continue
        if args.filter and args.filter not in path.name:
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            print(f"skipping {path.name}: {exc}")
            continue
        key = (data.get("task", "?"), data.get("config", "?"))
        by_pair[key].append(data.get("metrics", {}))

        # If this run wrote a sibling build result, fold it into the
        # per-pair success rate. Missing sibling = build wasn't requested
        # for this run, not a failure.
        build_sibling = path.with_suffix(".build.json")
        if build_sibling.exists():
            try:
                build_data = json.loads(build_sibling.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                continue
            builds_by_pair[key].append(bool(build_data.get("success", False)))

    if not by_pair:
        print("No results found.")
        return 0

    columns = [
        ("TASK", 28),
        ("CONFIG", 12),
        ("N", 4),
        ("TOOL CALLS", 18),
        ("IN TOK", 16),
        ("OUT TOK", 14),
        ("TOTAL TOK", 16),
        ("WALL (s)", 14),
        ("BUILDS", 12),
    ]
    header = "".join(f"{label:<{width}}" for label, width in columns)
    print(header)
    print("-" * len(header))

    for (task, config), runs in sorted(by_pair.items()):
        n = len(runs)
        tool_calls = [r.get("tool_call_count", 0) for r in runs]
        in_toks = [r.get("input_tokens", 0) for r in runs]
        out_toks = [r.get("output_tokens", 0) for r in runs]
        total_toks = [r.get("total_tokens", 0) for r in runs]
        walls = [r.get("wall_clock_seconds") or 0.0 for r in runs]

        builds = builds_by_pair.get((task, config), [])
        if builds:
            build_cell = f"{sum(builds)}/{len(builds)}"
        else:
            build_cell = "n/a"

        row = (
            f"{task:<28}"
            f"{config:<12}"
            f"{n:<4}"
            f"{_fmt(tool_calls):<18}"
            f"{_fmt(in_toks):<16}"
            f"{_fmt(out_toks):<14}"
            f"{_fmt(total_toks):<16}"
            f"{_fmt(walls, decimals=1):<14}"
            f"{build_cell:<12}"
        )
        print(row)

    print()
    print("Vanilla / TcKit ratios (higher = TcKit more efficient):")

    by_task: dict[str, dict[str, list[dict[str, Any]]]] = defaultdict(dict)
    for (task, config), runs in by_pair.items():
        by_task[task][config] = runs

    for task, configs in sorted(by_task.items()):
        vanilla = configs.get("empty")
        tckit = configs.get("tckit")
        if not (vanilla and tckit):
            continue
        v_total = statistics.mean([r.get("total_tokens", 0) for r in vanilla])
        t_total = statistics.mean([r.get("total_tokens", 0) for r in tckit])
        v_calls = statistics.mean([r.get("tool_call_count", 0) for r in vanilla])
        t_calls = statistics.mean([r.get("tool_call_count", 0) for r in tckit])
        v_wall = statistics.mean([r.get("wall_clock_seconds") or 0.0 for r in vanilla])
        t_wall = statistics.mean([r.get("wall_clock_seconds") or 0.0 for r in tckit])
        print(
            f"  {task}: tokens {_ratio(v_total, t_total)}, "
            f"tool calls {_ratio(v_calls, t_calls)}, "
            f"wall-clock {_ratio(v_wall, t_wall)}"
        )

    print()
    print("Per-tool breakdown by (task, config):")
    for (task, config), runs in sorted(by_pair.items()):
        merged: dict[str, list[int]] = defaultdict(list)
        for r in runs:
            breakdown = r.get("tool_breakdown") or {}
            for name, count in breakdown.items():
                merged[name].append(count)
        if not merged:
            continue
        print(f"  {task} / {config}:")
        for name, counts in sorted(merged.items()):
            mean = statistics.mean(counts)
            print(f"    {name:<28} mean {mean:.1f}  (per-run: {counts})")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
