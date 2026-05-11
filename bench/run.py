#!/usr/bin/env python3
"""bench/run.py — runner for the TcKit benchmark harness.

Invokes ``claude -p`` headless with a task prompt and an MCP config,
captures the JSON response, extracts tool calls and token totals, and
writes one result file per run.

Usage::

    python bench/run.py --task bench/tasks/01-orient.md \
        --config bench/configs/tckit.json --runs 3

Each run produces::

    bench/results/<task>__<config>__<utc-timestamp>__run<n>.json

See bench/README.md for the full task list and prerequisites.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys
import time
from datetime import datetime, timezone
from typing import Any


# ---------------------------------------------------------------------------
# Claude invocation
# ---------------------------------------------------------------------------


def run_one(prompt: str, config: pathlib.Path, tcunit_path: str) -> dict[str, Any]:
    """Invoke ``claude -p`` once with the given prompt and MCP config.

    Uses ``--output-format stream-json`` so each turn (including
    ``tool_use`` and ``tool_result`` content blocks) is emitted as its
    own JSON line. The final ``result`` event carries cumulative usage
    and cost.

    Permissions are bypassed so the headless run does not stall on
    Read/Grep prompts. The TcUnit directory is added via ``--add-dir``
    to grant explicit access independent of the working directory.
    """
    cmd = [
        "claude",
        "-p", prompt,
        "--strict-mcp-config",
        "--mcp-config", str(config),
        "--output-format", "stream-json",
        "--input-format", "text",
        "--verbose",
        "--no-session-persistence",
        "--permission-mode", "bypassPermissions",
        "--add-dir", tcunit_path,
    ]
    t0 = time.monotonic()
    proc = subprocess.run(cmd, capture_output=True, text=True, check=False)
    duration = time.monotonic() - t0

    events: list[dict[str, Any]] = []
    for line in proc.stdout.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            continue

    return {
        "wall_clock_seconds": duration,
        "exit_code": proc.returncode,
        "events": events,
        "raw_stdout": proc.stdout if not events else None,
        "raw_stderr": proc.stderr if proc.returncode != 0 or not events else None,
    }


# ---------------------------------------------------------------------------
# Metric extraction
# ---------------------------------------------------------------------------


def _content_items(message: dict[str, Any]) -> list[dict[str, Any]]:
    content = message.get("content")
    if isinstance(content, list):
        return [item for item in content if isinstance(item, dict)]
    return []


def extract_metrics(result: dict[str, Any]) -> dict[str, Any]:
    """Walk the stream-json event list and aggregate metrics.

    Each event is one JSON object; ``assistant`` events carry message
    content blocks (which may be ``text`` or ``tool_use``), and the
    final ``result`` event carries cumulative usage and cost.
    """
    events: list[dict[str, Any]] = result.get("events") or []

    tool_calls: list[dict[str, Any]] = []
    final_text: str | None = None
    final_usage: dict[str, Any] = {}
    total_cost_usd: float | None = None
    num_turns: int | None = None
    claude_duration_ms: int | None = None

    for event in events:
        etype = event.get("type")

        if etype == "assistant":
            message = event.get("message") or {}
            for item in _content_items(message):
                if item.get("type") == "tool_use":
                    tool_calls.append({
                        "name": item.get("name"),
                        "input_keys": sorted((item.get("input") or {}).keys())
                            if isinstance(item.get("input"), dict) else [],
                    })

        elif etype == "result":
            final_text = event.get("result") if isinstance(event.get("result"), str) else final_text
            usage = event.get("usage")
            if isinstance(usage, dict):
                final_usage = usage
            cost = event.get("total_cost_usd")
            if isinstance(cost, (int, float)):
                total_cost_usd = float(cost)
            turns = event.get("num_turns")
            if isinstance(turns, int):
                num_turns = turns
            duration = event.get("duration_ms")
            if isinstance(duration, int):
                claude_duration_ms = duration

    input_tokens = int(final_usage.get("input_tokens", 0) or 0)
    output_tokens = int(final_usage.get("output_tokens", 0) or 0)
    cache_read = int(final_usage.get("cache_read_input_tokens", 0) or 0)
    cache_creation = int(final_usage.get("cache_creation_input_tokens", 0) or 0)

    tool_breakdown: dict[str, int] = {}
    for call in tool_calls:
        name = call.get("name") or "<unknown>"
        tool_breakdown[name] = tool_breakdown.get(name, 0) + 1

    return {
        "tool_call_count": len(tool_calls),
        "tool_breakdown": tool_breakdown,
        "tool_calls": tool_calls,
        "num_turns": num_turns,
        "input_tokens": input_tokens,
        "output_tokens": output_tokens,
        "cache_read_tokens": cache_read,
        "cache_creation_tokens": cache_creation,
        "total_tokens": input_tokens + output_tokens,
        "total_cost_usd": total_cost_usd,
        "wall_clock_seconds": result.get("wall_clock_seconds"),
        "claude_reported_duration_ms": claude_duration_ms,
        "exit_code": result.get("exit_code"),
        "final_text": final_text,
    }


# ---------------------------------------------------------------------------
# Markdown sibling
# ---------------------------------------------------------------------------


def render_markdown(
    *,
    task: str,
    config: str,
    run: int,
    timestamp: str,
    tcunit_path: str,
    metrics: dict[str, Any],
    final_text: str | None,
) -> str:
    """Render the per-run final text as a Markdown document.

    The .md sibling exists so reviewers can read the actual answer
    Claude produced without parsing the JSON. Headline metrics go in
    a short preamble; the body is the unmodified ``final_text``.
    """
    tool_breakdown = metrics.get("tool_breakdown") or {}
    if tool_breakdown:
        breakdown_line = ", ".join(
            f"{name}×{count}" for name, count in sorted(tool_breakdown.items())
        )
    else:
        breakdown_line = "(none)"

    wall = metrics.get("wall_clock_seconds")
    wall_str = f"{wall:.1f}s" if isinstance(wall, (int, float)) else "n/a"

    header = (
        f"# {task} — {config} — run {run}\n\n"
        f"- Timestamp (UTC): {timestamp}\n"
        f"- TcUnit path: {tcunit_path}\n"
        f"- Tool calls: {metrics.get('tool_call_count', 'n/a')}\n"
        f"- Total tokens: {metrics.get('total_tokens', 'n/a')}\n"
        f"- Wall clock: {wall_str}\n"
        f"- Exit code: {metrics.get('exit_code', 'n/a')}\n"
        f"- Tool breakdown: {breakdown_line}\n\n"
        "---\n\n"
    )
    body = final_text if isinstance(final_text, str) and final_text else "_(no final text)_"
    return header + body + "\n"


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main() -> int:
    parser = argparse.ArgumentParser(description="Run a TcKit benchmark task.")
    parser.add_argument("--task", required=True, type=pathlib.Path,
                        help="Path to task markdown file.")
    parser.add_argument("--config", required=True, type=pathlib.Path,
                        help="Path to MCP config JSON.")
    parser.add_argument("--runs", type=int, default=3,
                        help="Number of runs to execute (default 3).")
    parser.add_argument("--tcunit-path", default="C:/TcUnit",
                        help="Path substituted into ${TCUNIT_PATH} placeholders.")
    parser.add_argument("--results-dir", type=pathlib.Path,
                        default=pathlib.Path(__file__).parent / "results",
                        help="Directory to write per-run result JSONs.")
    args = parser.parse_args()

    if not args.task.exists():
        print(f"task not found: {args.task}", file=sys.stderr)
        return 2
    if not args.config.exists():
        print(f"config not found: {args.config}", file=sys.stderr)
        return 2

    args.results_dir.mkdir(parents=True, exist_ok=True)

    prompt_template = args.task.read_text(encoding="utf-8")
    prompt = prompt_template.replace("${TCUNIT_PATH}", args.tcunit_path)

    task_stem = args.task.stem
    config_stem = args.config.stem
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")

    print(f"Task: {task_stem}  Config: {config_stem}  Runs: {args.runs}")
    print(f"TcUnit path: {args.tcunit_path}")
    print()

    for n in range(1, args.runs + 1):
        print(f"  Run {n}/{args.runs}...", end=" ", flush=True)
        result = run_one(prompt, args.config, args.tcunit_path)
        metrics = extract_metrics(result)

        out = {
            "task": task_stem,
            "config": config_stem,
            "run": n,
            "timestamp_utc": timestamp,
            "tcunit_path": args.tcunit_path,
            "metrics": {k: v for k, v in metrics.items() if k != "final_text"},
            "final_text": metrics.get("final_text"),
            "events": result.get("events"),
            "raw_stdout": result.get("raw_stdout"),
            "raw_stderr": result.get("raw_stderr"),
        }
        out_path = args.results_dir / f"{task_stem}__{config_stem}__{timestamp}__run{n}.json"
        out_path.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")

        md_path = out_path.with_suffix(".md")
        md_path.write_text(
            render_markdown(
                task=task_stem,
                config=config_stem,
                run=n,
                timestamp=timestamp,
                tcunit_path=args.tcunit_path,
                metrics=metrics,
                final_text=metrics.get("final_text"),
            ),
            encoding="utf-8",
        )

        if metrics["exit_code"] == 0:
            print(
                f"OK  calls={metrics['tool_call_count']:<3}  "
                f"tokens={metrics['total_tokens']:<6}  "
                f"wall={metrics['wall_clock_seconds']:.1f}s"
            )
        else:
            print(f"FAILED  exit={metrics['exit_code']}  see {out_path.name}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
