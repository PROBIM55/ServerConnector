from __future__ import annotations

import time
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import ifcopenshell.geom

from .events import EventSink, emit, null_sink
from .optimize import ENTITY_LINE_RE, _record_ends
from .planar import FaceGeometry, _dot, _face_geometry, _length, _subtract

Vec3 = tuple[float, float, float]


@dataclass(frozen=True)
class ExtrusionPlan:
    changed_records: dict[int, bytes]
    removed_ids: frozenset[int]
    appended_records: tuple[bytes, ...]
    expected_entity_count_delta: dict[str, int]
    metrics: dict[str, Any]


@dataclass(frozen=True)
class _Prism:
    base: FaceGeometry
    top: FaceGeometry
    direction: Vec3
    depth: float


def _centroid(points: tuple[Vec3, ...]) -> Vec3:
    count = len(points)
    return (
        sum(point[0] for point in points) / count,
        sum(point[1] for point in points) / count,
        sum(point[2] for point in points) / count,
    )


def _scale(points: tuple[Vec3, ...]) -> float:
    minimum = tuple(min(point[axis] for point in points) for axis in range(3))
    maximum = tuple(max(point[axis] for point in points) for axis in range(3))
    return max(1.0, *(maximum[axis] - minimum[axis] for axis in range(3)))


def _close_point(left: Vec3, right: Vec3, tolerance: float, scale: float) -> bool:
    return _length(_subtract(left, right)) <= tolerance * scale


def _translated_vertex_map(
    base: FaceGeometry,
    top: FaceGeometry,
    translation: Vec3,
    *,
    tolerance: float,
    scale: float,
) -> dict[int, int] | None:
    unmatched = set(range(len(top.coordinates)))
    mapping: dict[int, int] = {}
    for base_index, point in enumerate(base.coordinates):
        expected = (
            point[0] + translation[0],
            point[1] + translation[1],
            point[2] + translation[2],
        )
        matches = [
            top_index
            for top_index in unmatched
            if _close_point(
                expected,
                top.coordinates[top_index],
                tolerance,
                scale,
            )
        ]
        if len(matches) != 1:
            return None
        top_index = matches[0]
        mapping[base_index] = top_index
        unmatched.remove(top_index)
    return mapping if not unmatched else None


def _point_level(
    point: Vec3,
    origin: Vec3,
    direction: Vec3,
    depth: float,
    *,
    tolerance: float,
    scale: float,
) -> int | None:
    distance = _dot(_subtract(point, origin), direction)
    threshold = tolerance * scale
    if abs(distance) <= threshold:
        return 0
    if abs(distance - depth) <= threshold:
        return 1
    return None


def _strict_prism(
    geometries: tuple[FaceGeometry, ...],
    *,
    tolerance: float,
) -> _Prism | None:
    if len(geometries) < 5:
        return None
    all_points = tuple(
        point for geometry in geometries for point in geometry.coordinates
    )
    scale = _scale(all_points)
    quantized_edges: Counter[frozenset[tuple[int, int, int]]] = Counter()
    directed_edges: Counter[tuple[tuple[int, int, int], tuple[int, int, int]]] = (
        Counter()
    )
    for geometry in geometries:
        coordinates = tuple(
            tuple(round(value / (tolerance * scale)) for value in point)
            for point in geometry.coordinates
        )
        for index, left in enumerate(coordinates):
            right = coordinates[(index + 1) % len(coordinates)]
            quantized_edges[frozenset((left, right))] += 1
            directed_edges[(left, right)] += 1
    if any(count != 2 for count in quantized_edges.values()):
        return None
    if any(
        directed_edges[(left, right)] != directed_edges[(right, left)]
        for left, right in directed_edges
    ):
        return None

    for base_index, base in enumerate(geometries):
        for top in geometries[base_index + 1 :]:
            if len(base.coordinates) != len(top.coordinates):
                continue
            if _dot(base.normal, top.normal) > -1.0 + tolerance:
                continue

            translation = _subtract(
                _centroid(top.coordinates),
                _centroid(base.coordinates),
            )
            depth = _length(translation)
            if depth <= tolerance * scale:
                continue
            direction = (
                translation[0] / depth,
                translation[1] / depth,
                translation[2] / depth,
            )
            if abs(abs(_dot(base.normal, direction)) - 1.0) > tolerance:
                continue
            mapping = _translated_vertex_map(
                base,
                top,
                translation,
                tolerance=tolerance,
                scale=scale,
            )
            if mapping is None:
                continue
            if len(geometries) - 2 != len(base.coordinates):
                continue

            base_points = {
                tuple(round(value / (tolerance * scale)) for value in point)
                for point in base.coordinates
            }
            top_points = {
                tuple(round(value / (tolerance * scale)) for value in point)
                for point in top.coordinates
            }
            expected_side_edges = {
                frozenset(
                    (
                        tuple(
                            round(value / (tolerance * scale))
                            for value in base.coordinates[index]
                        ),
                        tuple(
                            round(value / (tolerance * scale))
                            for value in base.coordinates[
                                (index + 1) % len(base.coordinates)
                            ]
                        ),
                    )
                )
                for index in range(len(base.coordinates))
            }
            actual_side_edges: set[frozenset[tuple[int, ...]]] = set()
            valid = True
            for side in geometries:
                if side.face.id() in {base.face.id(), top.face.id()}:
                    continue
                if len(side.coordinates) != 4:
                    valid = False
                    break
                levels = [
                    _point_level(
                        point,
                        base.coordinates[0],
                        direction,
                        depth,
                        tolerance=tolerance,
                        scale=scale,
                    )
                    for point in side.coordinates
                ]
                if levels.count(0) != 2 or levels.count(1) != 2:
                    valid = False
                    break
                lower = [
                    tuple(round(value / (tolerance * scale)) for value in point)
                    for point, level in zip(side.coordinates, levels, strict=True)
                    if level == 0
                ]
                upper = [
                    tuple(round(value / (tolerance * scale)) for value in point)
                    for point, level in zip(side.coordinates, levels, strict=True)
                    if level == 1
                ]
                if not set(lower) <= base_points or not set(upper) <= top_points:
                    valid = False
                    break
                translated_lower = {
                    tuple(
                        round((value + translation[axis]) / (tolerance * scale))
                        for axis, value in enumerate(point)
                    )
                    for point in (
                        side.coordinates[point_index]
                        for point_index, level in enumerate(levels)
                        if level == 0
                    )
                }
                if translated_lower != set(upper):
                    valid = False
                    break
                actual_side_edges.add(frozenset(lower))
            if valid and actual_side_edges == expected_side_edges:
                return _Prism(
                    base=base,
                    top=top,
                    direction=direction,
                    depth=depth,
                )
    return None


def _has_unsupported_presentation(model: Any, entities: tuple[Any, ...]) -> bool:
    for entity in entities:
        if getattr(entity, "LayerAssignments", None):
            return True
        if entity.is_a("IfcFace") and getattr(entity, "StyledByItem", None):
            return True
        for inverse in model.get_inverse(entity):
            if inverse.is_a("IfcStyledItem") and not entity.is_a("IfcFacetedBrep"):
                return True
    return False


def _triangulation_signature(item: Any) -> tuple[tuple[float, ...], float, float]:
    settings = ifcopenshell.geom.settings()
    triangulation = ifcopenshell.geom.create_shape(settings, item)
    flat_vertices = tuple(float(value) for value in triangulation.verts)
    faces = tuple(int(value) for value in triangulation.faces)
    vertices = tuple(
        (
            flat_vertices[index],
            flat_vertices[index + 1],
            flat_vertices[index + 2],
        )
        for index in range(0, len(flat_vertices), 3)
    )
    if not vertices or not faces:
        raise ValueError("Geometry item did not produce a triangulation")
    bbox = tuple(min(vertex[axis] for vertex in vertices) for axis in range(3)) + tuple(
        max(vertex[axis] for vertex in vertices) for axis in range(3)
    )
    area = 0.0
    signed_volume = 0.0
    for index in range(0, len(faces), 3):
        first = vertices[faces[index]]
        second = vertices[faces[index + 1]]
        third = vertices[faces[index + 2]]
        edge_a = _subtract(second, first)
        edge_b = _subtract(third, first)
        cross = (
            edge_a[1] * edge_b[2] - edge_a[2] * edge_b[1],
            edge_a[2] * edge_b[0] - edge_a[0] * edge_b[2],
            edge_a[0] * edge_b[1] - edge_a[1] * edge_b[0],
        )
        area += 0.5 * _length(cross)
        signed_volume += (
            first[0] * (second[1] * third[2] - second[2] * third[1])
            - first[1] * (second[0] * third[2] - second[2] * third[0])
            + first[2] * (second[0] * third[1] - second[1] * third[0])
        ) / 6.0
    return bbox, area, abs(signed_volume)


def _geometry_matches(left: Any, right: Any, tolerance: float = 1e-8) -> bool:
    try:
        left_signature = _triangulation_signature(left)
        right_signature = _triangulation_signature(right)
    except (RuntimeError, ValueError):
        return False
    left_values = (*left_signature[0], left_signature[1], left_signature[2])
    right_values = (*right_signature[0], right_signature[1], right_signature[2])
    return all(
        abs(left_value - right_value)
        <= tolerance * max(1.0, abs(left_value), abs(right_value))
        for left_value, right_value in zip(left_values, right_values, strict=True)
    )


def _replacement_targets(
    model: Any, brep: Any
) -> tuple[tuple[Any, ...], tuple[Any, ...]] | None:
    representations: list[Any] = []
    styled_items: list[Any] = []
    for inverse in model.get_inverse(brep):
        if inverse.is_a("IfcShapeRepresentation"):
            if tuple(inverse.Items or ()) != (brep,):
                return None
            representations.append(inverse)
        elif inverse.is_a("IfcStyledItem") and inverse.Item == brep:
            styled_items.append(inverse)
        else:
            return None
    if not representations:
        return None
    return tuple(representations), tuple(styled_items)


def _profile_coordinates(
    prism: _Prism,
) -> tuple[Vec3, Vec3, tuple[tuple[float, float], ...]]:
    origin = prism.base.coordinates[0]
    first_edge = next(
        edge
        for point in prism.base.coordinates[1:]
        if _length(edge := _subtract(point, origin)) > 1e-15
    )
    first_edge_length = _length(first_edge)
    x_axis = tuple(value / first_edge_length for value in first_edge)
    z_axis = prism.direction
    y_axis = (
        z_axis[1] * x_axis[2] - z_axis[2] * x_axis[1],
        z_axis[2] * x_axis[0] - z_axis[0] * x_axis[2],
        z_axis[0] * x_axis[1] - z_axis[1] * x_axis[0],
    )
    profile = tuple(
        (
            _dot(_subtract(point, origin), x_axis),
            _dot(_subtract(point, origin), y_axis),
        )
        for point in prism.base.coordinates
    )
    signed_area = sum(
        point[0] * profile[(index + 1) % len(profile)][1]
        - profile[(index + 1) % len(profile)][0] * point[1]
        for index, point in enumerate(profile)
    )
    if signed_area < 0:
        profile = tuple(reversed(profile))
    return origin, x_axis, profile


def _create_extrusion(model: Any, prism: _Prism) -> Any:
    origin, x_axis, profile_coordinates = _profile_coordinates(prism)
    profile_points = tuple(
        model.create_entity("IfcCartesianPoint", Coordinates=coordinates)
        for coordinates in profile_coordinates
    )
    polyline = model.create_entity(
        "IfcPolyline",
        Points=(*profile_points, profile_points[0]),
    )
    profile = model.create_entity(
        "IfcArbitraryClosedProfileDef",
        ProfileType="AREA",
        ProfileName=None,
        OuterCurve=polyline,
    )
    position = model.create_entity(
        "IfcAxis2Placement3D",
        Location=model.create_entity("IfcCartesianPoint", Coordinates=origin),
        Axis=model.create_entity(
            "IfcDirection",
            DirectionRatios=prism.direction,
        ),
        RefDirection=model.create_entity(
            "IfcDirection",
            DirectionRatios=x_axis,
        ),
    )
    return model.create_entity(
        "IfcExtrudedAreaSolid",
        SweptArea=profile,
        Position=position,
        ExtrudedDirection=model.create_entity(
            "IfcDirection",
            DirectionRatios=(0.0, 0.0, 1.0),
        ),
        Depth=prism.depth,
    )


def _removable_level(
    model: Any,
    entities: tuple[Any, ...],
    removed_parent_ids: set[int],
) -> set[int]:
    return {
        int(entity.id())
        for entity in entities
        if entity.id()
        and all(
            inverse.id() and int(inverse.id()) in removed_parent_ids
            for inverse in model.get_inverse(entity)
        )
    }


def build_extrusion_plan(
    model: Any,
    *,
    sink: EventSink = null_sink,
    tolerance: float = 1e-9,
) -> ExtrusionPlan:
    started = time.perf_counter()
    initial_max_id = max((int(entity.id()) for entity in model), default=0)
    breps = tuple(model.by_type("IfcFacetedBrep", include_subtypes=False))
    changed_entities: dict[int, Any] = {}
    accepted_breps: list[Any] = []
    accepted_shells: list[Any] = []
    accepted_faces: list[Any] = []
    accepted_bounds: list[Any] = []
    accepted_loops: list[Any] = []
    accepted_points: list[Any] = []
    skipped_topology = 0
    skipped_presentation = 0
    skipped_references = 0
    skipped_geometry = 0

    def emit_progress(index: int) -> None:
        if index % 25 == 0 or index == len(breps):
            emit(
                sink,
                stage="recognize-extrusions",
                progress=index / max(len(breps), 1),
                message=(
                    f"Проверено BREP: {index}/{len(breps)}, "
                    f"выдавливаний: {len(accepted_breps)}"
                ),
            )

    for index, brep in enumerate(breps, start=1):
        shell = brep.Outer
        if shell is None or not shell.is_a("IfcClosedShell"):
            skipped_topology += 1
            emit_progress(index)
            continue
        faces = tuple(shell.CfsFaces or ())
        hierarchy = (
            brep,
            shell,
            *faces,
            *(
                bound
                for face in faces
                for bound in (getattr(face, "Bounds", None) or ())
            ),
            *(
                loop
                for face in faces
                for bound in (getattr(face, "Bounds", None) or ())
                if (loop := getattr(bound, "Bound", None)) is not None
            ),
            *(
                point
                for face in faces
                for bound in (getattr(face, "Bounds", None) or ())
                if (loop := getattr(bound, "Bound", None)) is not None
                for point in (getattr(loop, "Polygon", None) or ())
            ),
        )
        if _has_unsupported_presentation(model, hierarchy):
            skipped_presentation += 1
            emit_progress(index)
            continue
        geometries = tuple(
            geometry
            for face in faces
            if (geometry := _face_geometry(model, shell, face)) is not None
        )
        if len(geometries) != len(faces):
            skipped_topology += 1
            emit_progress(index)
            continue
        targets = _replacement_targets(model, brep)
        if targets is None:
            skipped_references += 1
            emit_progress(index)
            continue
        prism = _strict_prism(geometries, tolerance=tolerance)
        if prism is None:
            skipped_topology += 1
            emit_progress(index)
            continue

        candidate_initial_max_id = max(
            (int(entity.id()) for entity in model),
            default=0,
        )
        extrusion = _create_extrusion(model, prism)
        if not _geometry_matches(brep, extrusion):
            for entity in sorted(
                (
                    entity
                    for entity in model
                    if int(entity.id()) > candidate_initial_max_id
                ),
                key=lambda entity: int(entity.id()),
                reverse=True,
            ):
                model.remove(entity)
            skipped_geometry += 1
            emit_progress(index)
            continue
        representations, styled_items = targets
        for representation in representations:
            representation.Items = (extrusion,)
            representation.RepresentationType = "SweptSolid"
            changed_entities[int(representation.id())] = representation
        for styled_item in styled_items:
            styled_item.Item = extrusion
            changed_entities[int(styled_item.id())] = styled_item

        accepted_breps.append(brep)
        accepted_shells.append(shell)
        accepted_faces.extend(faces)
        for face in faces:
            for bound in tuple(face.Bounds or ()):
                accepted_bounds.append(bound)
                loop = getattr(bound, "Bound", None)
                if loop is not None:
                    accepted_loops.append(loop)
                    accepted_points.extend(tuple(getattr(loop, "Polygon", ()) or ()))

        emit_progress(index)

    removed_ids = _removable_level(model, tuple(accepted_breps), set())
    shell_ids = _removable_level(model, tuple(accepted_shells), removed_ids)
    removed_ids.update(shell_ids)
    face_ids = _removable_level(model, tuple(accepted_faces), shell_ids)
    removed_ids.update(face_ids)
    bound_ids = _removable_level(model, tuple(accepted_bounds), face_ids)
    removed_ids.update(bound_ids)
    loop_ids = _removable_level(model, tuple(accepted_loops), bound_ids)
    removed_ids.update(loop_ids)
    point_ids = _removable_level(model, tuple(accepted_points), loop_ids)
    removed_ids.update(point_ids)

    current_entities = list(model)
    changed_records = {
        entity_id: (str(entity) + ";\n").encode("utf-8")
        for entity_id, entity in changed_entities.items()
    }
    appended_entities = tuple(
        entity for entity in current_entities if int(entity.id()) > initial_max_id
    )
    appended_records = tuple(
        (str(entity) + ";\n").encode("utf-8")
        for entity in sorted(appended_entities, key=lambda entity: int(entity.id()))
    )
    original_entities = {
        int(entity.id()): entity
        for entity in (
            *accepted_breps,
            *accepted_shells,
            *accepted_faces,
            *accepted_bounds,
            *accepted_loops,
            *accepted_points,
        )
        if entity.id()
    }
    removed_by_type = Counter(
        str(original_entities[entity_id].is_a()) for entity_id in removed_ids
    )
    appended_by_type = Counter(str(entity.is_a()) for entity in appended_entities)
    expected_delta = {
        type_name: appended_by_type[type_name] - removed_by_type[type_name]
        for type_name in sorted(set(removed_by_type) | set(appended_by_type))
        if appended_by_type[type_name] != removed_by_type[type_name]
    }
    metrics = {
        "mode": "strict-linear-extrusion",
        "breps_total": len(breps),
        "extrusions_created": len(accepted_breps),
        "skipped_topology": skipped_topology,
        "skipped_presentation": skipped_presentation,
        "skipped_references": skipped_references,
        "skipped_geometry": skipped_geometry,
        "removed_entities": len(removed_ids),
        "appended_entities": len(appended_entities),
        "tolerance": tolerance,
        "elapsed_seconds": round(time.perf_counter() - started, 3),
    }
    return ExtrusionPlan(
        changed_records=changed_records,
        removed_ids=frozenset(removed_ids),
        appended_records=appended_records,
        expected_entity_count_delta=expected_delta,
        metrics=metrics,
    )


def write_extrusion_result(
    source_path: Path,
    output_path: Path,
    plan: ExtrusionPlan,
    *,
    sink: EventSink = null_sink,
) -> None:
    emit(
        sink,
        stage="serialize-extrusions",
        progress=0.0,
        message="Сериализуются аналитические выдавливания",
    )
    total_bytes = source_path.stat().st_size
    processed_bytes = 0
    skip_record = False
    record_in_string = False
    data_section = False
    append_written = False

    with source_path.open("rb") as source, output_path.open("wb") as handle:
        for index, line in enumerate(source, start=1):
            processed_bytes += len(line)
            if skip_record:
                ended, record_in_string = _record_ends(
                    line,
                    in_string=record_in_string,
                )
                if ended:
                    skip_record = False
                    record_in_string = False
                continue

            stripped = line.strip().upper()
            if stripped == b"DATA;":
                data_section = True
            elif data_section and stripped == b"ENDSEC;" and not append_written:
                for record in plan.appended_records:
                    handle.write(record)
                append_written = True
                data_section = False

            match = ENTITY_LINE_RE.match(line)
            if match:
                entity_id = int(match.group(1))
                replacement = plan.changed_records.get(entity_id)
                if replacement is not None or entity_id in plan.removed_ids:
                    if replacement is not None:
                        handle.write(replacement)
                    ended, record_in_string = _record_ends(line, in_string=False)
                    skip_record = not ended
                    if ended:
                        record_in_string = False
                    continue

            handle.write(line)
            if index % 250000 == 0:
                emit(
                    sink,
                    stage="serialize-extrusions",
                    progress=processed_bytes / max(total_bytes, 1),
                    message=f"Обработано STEP-строк: {index:,}".replace(",", " "),
                )

    if plan.appended_records and not append_written:
        raise RuntimeError("Could not locate IFC DATA section terminator")
