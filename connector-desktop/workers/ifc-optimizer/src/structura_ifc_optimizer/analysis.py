from __future__ import annotations

import time
from collections import Counter
from pathlib import Path
from typing import Any

import ifcopenshell

from .events import EventSink, emit, null_sink
from .io_utils import bytes_label, require_ifc_file, sha256_file
from .jobs import JobArtifacts

WATCH_TYPES = (
    "IfcRoot",
    "IfcProduct",
    "IfcElement",
    "IfcBuildingElementProxy",
    "IfcRepresentationMap",
    "IfcMappedItem",
    "IfcFacetedBrep",
    "IfcClosedShell",
    "IfcFace",
    "IfcFaceOuterBound",
    "IfcPolyLoop",
    "IfcCartesianPoint",
    "IfcShapeRepresentation",
    "IfcProductDefinitionShape",
    "IfcPropertySet",
    "IfcElementQuantity",
    "IfcRelationship",
)


def _schema(model: Any) -> str:
    value = getattr(model, "schema", "")
    return str(value() if callable(value) else value)


def _point_key(point: Any) -> tuple[float, ...]:
    return tuple(float(value) for value in point.Coordinates)


def _point_metrics(model: Any, sink: EventSink) -> dict[str, int | float]:
    points = list(model.by_type("IfcCartesianPoint"))
    unique: set[tuple[float, ...]] = set()
    total = len(points)
    for index, point in enumerate(points, start=1):
        unique.add(_point_key(point))
        if index % 50000 == 0:
            emit(
                sink,
                stage="analyze-points",
                progress=index / max(total, 1),
                message=f"Проверено точек: {index:,}".replace(",", " "),
            )
    duplicates = total - len(unique)
    return {
        "cartesian_points": total,
        "unique_cartesian_points": len(unique),
        "duplicate_cartesian_points": duplicates,
        "duplicate_cartesian_points_percent": round(
            duplicates * 100.0 / total if total else 0.0,
            3,
        ),
    }


def _diagnostics(
    counts: Counter[str], point_metrics: dict[str, Any]
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    duplicate_percent = float(point_metrics["duplicate_cartesian_points_percent"])
    if duplicate_percent >= 10:
        rows.append(
            {
                "code": "duplicate-points",
                "severity": "high" if duplicate_percent >= 50 else "medium",
                "title": "Повторяющиеся координатные точки",
                "description": (
                    f"{duplicate_percent:.1f}% IfcCartesianPoint повторяются и могут "
                    "быть объединены без изменения формы."
                ),
            }
        )
    faces = counts.get("IfcFace", 0)
    breps = counts.get("IfcFacetedBrep", 0)
    if faces and breps:
        rows.append(
            {
                "code": "faceted-brep",
                "severity": "high" if faces >= 100000 else "medium",
                "title": "Раздутая BREP-геометрия",
                "description": (
                    f"Найдено {faces:,} граней в {breps:,} IfcFacetedBrep. "
                    "Следующий уровень оптимизации должен восстанавливать компактные тела."
                ).replace(",", " "),
            }
        )
    mapped = counts.get("IfcMappedItem", 0)
    products = counts.get("IfcProduct", 0)
    if products > 10 and mapped < products * 0.1:
        rows.append(
            {
                "code": "low-instancing",
                "severity": "medium",
                "title": "Геометрия почти не переиспользуется",
                "description": (
                    f"Для {products:,} продуктов найдено только {mapped:,} mapped instances."
                ).replace(",", " "),
            }
        )
    return rows


def _watch_counts(model: Any, exact_counts: Counter[str]) -> dict[str, int]:
    result: dict[str, int] = {}
    for type_name in WATCH_TYPES:
        try:
            result[type_name] = len(model.by_type(type_name))
        except RuntimeError:
            result[type_name] = exact_counts.get(type_name, 0)
    return result


def _analyze_ifc(
    path_value: str | Path,
    *,
    sink: EventSink = null_sink,
) -> dict[str, Any]:
    path = require_ifc_file(path_value)
    started = time.perf_counter()
    emit(sink, stage="hash", progress=0.0, message="Вычисляется контрольная сумма")
    digest = sha256_file(path)
    emit(sink, stage="open", progress=0.0, message="Открывается IFC через IfcOpenShell")
    model = ifcopenshell.open(str(path))
    emit(sink, stage="inventory", progress=0.0, message="Считаются IFC-сущности")

    counts: Counter[str] = Counter()
    for entity in model:
        counts[str(entity.is_a())] += 1

    point_metrics = _point_metrics(model, sink)
    size_bytes = path.stat().st_size
    result = {
        "input_path": str(path),
        "file_name": path.name,
        "file_size_bytes": size_bytes,
        "file_size_label": bytes_label(size_bytes),
        "sha256": digest,
        "schema": _schema(model),
        "entity_total": sum(counts.values()),
        "entity_counts": dict(sorted(counts.items())),
        "watch_counts": _watch_counts(model, counts),
        **point_metrics,
        "diagnostics": _diagnostics(counts, point_metrics),
        "elapsed_seconds": round(time.perf_counter() - started, 3),
    }
    emit(
        sink,
        event="analysis",
        stage="analysis-complete",
        progress=1.0,
        message="Анализ IFC завершён",
        data=result,
    )
    return result


def analyze_ifc(
    path_value: str | Path,
    *,
    sink: EventSink = null_sink,
    job_dir: str | Path | None = None,
) -> dict[str, Any]:
    path = require_ifc_file(path_value)
    artifacts = (
        JobArtifacts(job_dir, operation="analyze", input_path=path)
        if job_dir is not None
        else None
    )
    effective_sink = artifacts.sink(sink) if artifacts else sink
    try:
        result = _analyze_ifc(path, sink=effective_sink)
        if artifacts:
            artifacts.write("analysis.json", result)
            artifacts.finish("completed", sha256=result["sha256"])
        return result
    except Exception as exc:
        if artifacts:
            artifacts.finish(
                "failed",
                error={"type": type(exc).__name__, "message": str(exc)},
            )
        raise
