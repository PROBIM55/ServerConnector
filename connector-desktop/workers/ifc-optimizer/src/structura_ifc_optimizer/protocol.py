from __future__ import annotations

import json
import sys
import traceback
from dataclasses import replace
from typing import Any, TextIO

from .analysis import analyze_ifc
from .events import PROTOCOL_VERSION, EngineEvent
from .pipeline import optimize_ifc


def _force_utf8(stream: TextIO) -> None:
    """Keep the NDJSON boundary deterministic on localized Windows hosts."""
    reconfigure = getattr(stream, "reconfigure", None)
    if callable(reconfigure):
        reconfigure(encoding="utf-8", errors="strict")


def _write(stream: TextIO, payload: dict[str, Any]) -> None:
    stream.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    stream.flush()


def serve(input_stream: TextIO = sys.stdin, output_stream: TextIO = sys.stdout) -> int:
    _force_utf8(input_stream)
    _force_utf8(output_stream)
    for raw_line in input_stream:
        line = raw_line.strip()
        if not line:
            continue
        request_id: str | None = None
        try:
            request = json.loads(line)
            request_id = str(request.get("request_id") or "")
            if request.get("protocol") != PROTOCOL_VERSION:
                raise ValueError("Unsupported engine protocol")
            command = request.get("command")
            payload = request.get("payload") or {}

            def sink(
                event: EngineEvent,
                _request_id: str | None = request_id,
            ) -> None:
                _write(
                    output_stream,
                    replace(event, request_id=_request_id).to_dict(),
                )

            if command == "analyze":
                analyze_ifc(
                    payload["input_path"],
                    sink=sink,
                    job_dir=payload.get("job_dir"),
                )
            elif command == "optimize":
                optimize_ifc(
                    payload["input_path"],
                    payload["output_path"],
                    report_path=payload.get("report_path"),
                    sink=sink,
                    job_dir=payload.get("job_dir"),
                    profile=payload.get("profile", "exact"),
                )
            else:
                raise ValueError(f"Unsupported command: {command}")
        except Exception as exc:  # noqa: BLE001 - one failed request must not stop sidecar
            error_data: dict[str, Any] = {
                "type": type(exc).__name__,
                "traceback": traceback.format_exc(),
            }
            code = getattr(exc, "code", None)
            details = getattr(exc, "details", None)
            if code is not None:
                error_data["code"] = str(code)
            if isinstance(details, dict):
                error_data["details"] = details
            _write(
                output_stream,
                {
                    "protocol": PROTOCOL_VERSION,
                    "request_id": request_id,
                    "event": "error",
                    "stage": "failed",
                    "progress": 1.0,
                    "message": str(exc),
                    "data": error_data,
                },
            )
    return 0
