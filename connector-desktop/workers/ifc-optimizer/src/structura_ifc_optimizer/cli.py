from __future__ import annotations

import argparse
import json
import sys
from dataclasses import replace

from .analysis import analyze_ifc
from .events import EngineEvent
from .pipeline import optimize_ifc
from .protocol import serve


def _event_printer(event: EngineEvent) -> None:
    print(
        json.dumps(replace(event, request_id="cli").to_dict(), ensure_ascii=False),
        flush=True,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Structura IFC Optimizer engine")
    subparsers = parser.add_subparsers(dest="command", required=True)
    analyze = subparsers.add_parser("analyze", help="Analyze an IFC file")
    analyze.add_argument("input_path")
    analyze.add_argument("--job-dir")
    optimize = subparsers.add_parser(
        "optimize", help="Optimize and validate an IFC file"
    )
    optimize.add_argument("input_path")
    optimize.add_argument("output_path")
    optimize.add_argument("--report")
    optimize.add_argument("--job-dir")
    optimize.add_argument(
        "--profile",
        choices=("exact", "compact"),
        default="exact",
    )
    subparsers.add_parser("serve", help="Run the NDJSON sidecar protocol")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.command == "serve":
            return serve()
        if args.command == "analyze":
            result = analyze_ifc(
                args.input_path,
                sink=_event_printer,
                job_dir=args.job_dir,
            )
        else:
            result = optimize_ifc(
                args.input_path,
                args.output_path,
                report_path=args.report,
                sink=_event_printer,
                job_dir=args.job_dir,
                profile=args.profile,
            )
        print(json.dumps(result, ensure_ascii=False, indent=2), file=sys.stderr)
        return 0
    except Exception as exc:  # noqa: BLE001 - CLI boundary reports engine failures
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1
