from __future__ import annotations

import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from .events import EngineEvent, EventSink
from .io_utils import write_json_atomic


def utc_now() -> str:
    return datetime.now(UTC).isoformat()


class JobArtifacts:
    def __init__(
        self,
        directory: str | Path,
        *,
        operation: str,
        input_path: str | Path,
        output_path: str | Path | None = None,
    ) -> None:
        self.directory = Path(directory).expanduser().resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.job_path = self.directory / "job.json"
        self.events_path = self.directory / "events.ndjson"
        self._state: dict[str, Any] = {
            "version": 1,
            "status": "running",
            "operation": operation,
            "input_path": str(input_path),
            "output_path": str(output_path) if output_path is not None else None,
            "started_at": utc_now(),
            "updated_at": utc_now(),
        }
        write_json_atomic(self.job_path, self._state)

    def sink(self, downstream: EventSink) -> EventSink:
        def write_event(event: EngineEvent) -> None:
            with self.events_path.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(
                    json.dumps(
                        event.to_dict(),
                        ensure_ascii=False,
                        separators=(",", ":"),
                    )
                )
                handle.write("\n")
                handle.flush()
            downstream(event)

        return write_event

    def write(self, name: str, payload: Any) -> Path:
        path = self.directory / name
        write_json_atomic(path, payload)
        return path

    def finish(self, status: str, **fields: Any) -> None:
        self._state.update(fields)
        self._state["status"] = status
        self._state["updated_at"] = utc_now()
        self._state["finished_at"] = utc_now()
        write_json_atomic(self.job_path, self._state)
