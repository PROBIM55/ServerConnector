from __future__ import annotations

import io
import json

from structura_ifc_optimizer import protocol as protocol_module
from structura_ifc_optimizer.events import PROTOCOL_VERSION
from structura_ifc_optimizer.orientation import UnsupportedMirroredMapError
from structura_ifc_optimizer.protocol import serve


def test_protocol_rejects_unknown_command() -> None:
    source = io.StringIO(
        json.dumps(
            {
                "protocol": PROTOCOL_VERSION,
                "request_id": "r-1",
                "command": "missing",
                "payload": {},
            }
        )
        + "\n"
    )
    output = io.StringIO()

    assert serve(source, output) == 0

    row = json.loads(output.getvalue())
    assert row["event"] == "error"
    assert row["request_id"] == "r-1"
    assert "Unsupported command" in row["message"]


def test_protocol_forces_utf8_for_windows_sidecar_streams(monkeypatch) -> None:
    input_path = r"G:\models\(Сборка)КС_ПС2-1_Панель 9.ifc"
    request = json.dumps(
        {
            "protocol": PROTOCOL_VERSION,
            "request_id": "r-utf8",
            "command": "analyze",
            "payload": {"input_path": input_path},
        },
        ensure_ascii=False,
    )
    source = io.TextIOWrapper(
        io.BytesIO((request + "\n").encode("utf-8")),
        encoding="cp1251",
    )
    output_buffer = io.BytesIO()
    output = io.TextIOWrapper(output_buffer, encoding="cp1251")
    received: list[str] = []

    def analyze(input_value, *, sink, job_dir=None):
        received.append(input_value)

    monkeypatch.setattr(protocol_module, "analyze_ifc", analyze)

    assert serve(source, output) == 0
    output.flush()
    assert received == [input_path]
    assert output.encoding.lower().replace("-", "") == "utf8"


def test_protocol_exposes_unsupported_mirrored_map_code(monkeypatch) -> None:
    def optimize(*_args, **_kwargs):
        raise UnsupportedMirroredMapError(42, "несколько элементов")

    monkeypatch.setattr(protocol_module, "optimize_ifc", optimize)
    source = io.StringIO(
        json.dumps(
            {
                "protocol": PROTOCOL_VERSION,
                "request_id": "r-mirror",
                "command": "optimize",
                "payload": {"input_path": "in.ifc", "output_path": "out.ifc"},
            }
        )
        + "\n"
    )
    output = io.StringIO()

    assert serve(source, output) == 0

    row = json.loads(output.getvalue())
    assert row["event"] == "error"
    assert row["data"]["code"] == "UNSUPPORTED_MIRRORED_MAP"
    assert row["data"]["details"] == {
        "map_id": 42,
        "reason": "несколько элементов",
    }
    assert "не создан" in row["message"]
