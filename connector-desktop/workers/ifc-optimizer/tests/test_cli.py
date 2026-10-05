from __future__ import annotations

import sys
from typing import Any

from structura_ifc_optimizer import cli


def test_optimize_cli_routes_compact_profile(
    monkeypatch: Any,
) -> None:
    captured: dict[str, Any] = {}

    def fake_optimize(*args: Any, **kwargs: Any) -> dict[str, Any]:
        captured["args"] = args
        captured["kwargs"] = kwargs
        return {"status": "completed"}

    monkeypatch.setattr(cli, "optimize_ifc", fake_optimize)
    monkeypatch.setattr(
        sys,
        "argv",
        [
            "structura-ifc-optimizer",
            "optimize",
            "source.ifc",
            "output.ifc",
            "--profile",
            "compact",
        ],
    )

    assert cli.main() == 0
    assert captured["kwargs"]["profile"] == "compact"


def test_analyze_cli_does_not_require_profile(
    monkeypatch: Any,
) -> None:
    captured: dict[str, Any] = {}

    def fake_analyze(*args: Any, **kwargs: Any) -> dict[str, Any]:
        captured["args"] = args
        captured["kwargs"] = kwargs
        return {"status": "completed"}

    monkeypatch.setattr(cli, "analyze_ifc", fake_analyze)
    monkeypatch.setattr(
        sys,
        "argv",
        ["structura-ifc-optimizer", "analyze", "source.ifc"],
    )

    assert cli.main() == 0
    assert "profile" not in captured["kwargs"]
