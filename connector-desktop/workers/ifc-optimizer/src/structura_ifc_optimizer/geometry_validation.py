from __future__ import annotations

import math
import os
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import ifcopenshell
import ifcopenshell.geom

from .events import EventSink, emit, null_sink


@dataclass(frozen=True)
class GeometrySignature:
    bbox: tuple[float, ...]
    area: float
    volume: float
    signed_volume: float
    triangles: int


def _subtract(
    left: tuple[float, float, float],
    right: tuple[float, float, float],
) -> tuple[float, float, float]:
    return (
        left[0] - right[0],
        left[1] - right[1],
        left[2] - right[2],
    )


def _cross(
    left: tuple[float, float, float],
    right: tuple[float, float, float],
) -> tuple[float, float, float]:
    return (
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    )


def _signature(shape: Any) -> GeometrySignature:
    geometry = shape.geometry
    flat_vertices = tuple(float(value) for value in geometry.verts)
    faces = tuple(int(value) for value in geometry.faces)
    vertices = tuple(
        (
            flat_vertices[index],
            flat_vertices[index + 1],
            flat_vertices[index + 2],
        )
        for index in range(0, len(flat_vertices), 3)
    )
    bbox = (
        (
            tuple(min(vertex[axis] for vertex in vertices) for axis in range(3))
            + tuple(max(vertex[axis] for vertex in vertices) for axis in range(3))
        )
        if vertices
        else ()
    )
    area = 0.0
    signed_volume = 0.0
    for index in range(0, len(faces), 3):
        first = vertices[faces[index]]
        second = vertices[faces[index + 1]]
        third = vertices[faces[index + 2]]
        cross = _cross(_subtract(second, first), _subtract(third, first))
        area += 0.5 * math.sqrt(sum(value * value for value in cross))
        signed_volume += (
            first[0] * (second[1] * third[2] - second[2] * third[1])
            - first[1] * (second[0] * third[2] - second[2] * third[0])
            + first[2] * (second[0] * third[1] - second[1] * third[0])
        ) / 6.0
    return GeometrySignature(
        bbox=bbox,
        area=area,
        volume=abs(signed_volume),
        signed_volume=signed_volume,
        triangles=len(faces) // 3,
    )


def _build_signatures(
    path: Path,
    *,
    workers: int,
) -> tuple[dict[int, GeometrySignature], float]:
    started = time.perf_counter()
    model = ifcopenshell.open(str(path))
    settings = ifcopenshell.geom.settings()
    settings.set(settings.USE_WORLD_COORDS, True)
    iterator = ifcopenshell.geom.iterator(settings, model, workers)
    signatures: dict[int, GeometrySignature] = {}
    if iterator.initialize():
        while True:
            shape = iterator.get()
            signatures[int(shape.id)] = _signature(shape)
            if not iterator.next():
                break
    return signatures, round(time.perf_counter() - started, 3)


def _close(left: float, right: float, tolerance: float) -> bool:
    return abs(left - right) <= tolerance * max(
        1.0,
        abs(left),
        abs(right),
    )


def compare_rendered_geometry(
    source_path: Path,
    output_path: Path,
    *,
    sink: EventSink = null_sink,
    tolerance: float = 1e-8,
    workers: int | None = None,
    require_positive_output: bool = False,
) -> dict[str, Any]:
    effective_workers = workers or max(1, min(os.cpu_count() or 1, 4))
    emit(
        sink,
        stage="validate-rendered-geometry",
        progress=0.0,
        message="Тесселируется исходная геометрия",
    )
    source, source_seconds = _build_signatures(
        source_path,
        workers=effective_workers,
    )
    emit(
        sink,
        stage="validate-rendered-geometry",
        progress=0.5,
        message="Тесселируется compact-геометрия",
    )
    output, output_seconds = _build_signatures(
        output_path,
        workers=effective_workers,
    )

    missing = sorted(set(source) - set(output))
    added = sorted(set(output) - set(source))
    mismatches: list[dict[str, Any]] = []
    for step_id in sorted(set(source) & set(output)):
        source_signature = source[step_id]
        output_signature = output[step_id]
        bbox_ok = len(source_signature.bbox) == len(output_signature.bbox) and all(
            _close(left, right, tolerance)
            for left, right in zip(
                source_signature.bbox,
                output_signature.bbox,
                strict=True,
            )
        )
        area_ok = _close(
            source_signature.area,
            output_signature.area,
            tolerance,
        )
        volume_ok = _close(
            source_signature.volume,
            output_signature.volume,
            tolerance,
        )
        if bbox_ok and area_ok and volume_ok:
            continue
        mismatches.append(
            {
                "step_id": step_id,
                "bbox_ok": bbox_ok,
                "area_ok": area_ok,
                "volume_ok": volume_ok,
                "source": {
                    "bbox": source_signature.bbox,
                    "area": source_signature.area,
                    "volume": source_signature.volume,
                    "signed_volume": source_signature.signed_volume,
                    "triangles": source_signature.triangles,
                },
                "output": {
                    "bbox": output_signature.bbox,
                    "area": output_signature.area,
                    "volume": output_signature.volume,
                    "signed_volume": output_signature.signed_volume,
                    "triangles": output_signature.triangles,
                },
            }
        )

    source_negative_shape_ids = sorted(
        step_id
        for step_id, signature in source.items()
        if signature.signed_volume < -tolerance
    )
    output_negative_shape_ids = sorted(
        step_id
        for step_id, signature in output.items()
        if signature.signed_volume < -tolerance
    )
    orientation_ok = not require_positive_output or not output_negative_shape_ids
    result = {
        "ok": not missing and not added and not mismatches and orientation_ok,
        "source_shapes": len(source),
        "output_shapes": len(output),
        "missing_shape_ids": missing,
        "added_shape_ids": added,
        "mismatch_count": len(mismatches),
        "mismatches": mismatches[:50],
        "require_positive_output": require_positive_output,
        "orientation_ok": orientation_ok,
        "source_negative_shape_ids": source_negative_shape_ids,
        "output_negative_shape_ids": output_negative_shape_ids,
        "tolerance": tolerance,
        "workers": effective_workers,
        "source_elapsed_seconds": source_seconds,
        "output_elapsed_seconds": output_seconds,
    }
    emit(
        sink,
        event="validation",
        stage="rendered-geometry-complete",
        progress=1.0,
        message=(
            "Отображаемая геометрия совпадает"
            if result["ok"]
            else "Обнаружены расхождения отображаемой геометрии"
        ),
        data=result,
    )
    return result
