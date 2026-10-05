from __future__ import annotations

from collections.abc import Callable
from dataclasses import asdict, dataclass
from typing import Any

PROTOCOL_VERSION = "structura-ifc-optimizer/1"


@dataclass(frozen=True)
class EngineEvent:
    event: str
    stage: str
    progress: float
    message: str
    data: dict[str, Any] | None = None
    protocol: str = PROTOCOL_VERSION
    request_id: str | None = None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


EventSink = Callable[[EngineEvent], None]


def null_sink(_: EngineEvent) -> None:
    return None


def emit(
    sink: EventSink,
    *,
    event: str = "progress",
    stage: str,
    progress: float,
    message: str,
    data: dict[str, Any] | None = None,
) -> None:
    sink(
        EngineEvent(
            event=event,
            stage=stage,
            progress=max(0.0, min(1.0, float(progress))),
            message=message,
            data=data,
        )
    )
