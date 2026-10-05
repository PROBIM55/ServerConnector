from __future__ import annotations

import math
import time
from collections import Counter, defaultdict
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .events import EventSink, emit, null_sink
from .optimize import ENTITY_LINE_RE, _record_ends

Vec3 = tuple[float, float, float]
Edge = tuple[int, int]


@dataclass(frozen=True)
class FaceGeometry:
    face: Any
    points: tuple[Any, ...]
    coordinates: tuple[Vec3, ...]
    normal: Vec3
    plane_offset: float


@dataclass(frozen=True)
class PlanarMergePlan:
    changed_records: dict[int, bytes]
    removed_ids: frozenset[int]
    appended_records: tuple[bytes, ...]
    expected_entity_count_delta: dict[str, int]
    metrics: dict[str, Any]


class _UnionFind:
    def __init__(self, size: int) -> None:
        self.parent = list(range(size))
        self.rank = [0] * size

    def find(self, item: int) -> int:
        parent = self.parent[item]
        if parent != item:
            self.parent[item] = self.find(parent)
        return self.parent[item]

    def union(self, left: int, right: int) -> None:
        left_root = self.find(left)
        right_root = self.find(right)
        if left_root == right_root:
            return
        if self.rank[left_root] < self.rank[right_root]:
            left_root, right_root = right_root, left_root
        self.parent[right_root] = left_root
        if self.rank[left_root] == self.rank[right_root]:
            self.rank[left_root] += 1


def _subtract(left: Vec3, right: Vec3) -> Vec3:
    return (
        left[0] - right[0],
        left[1] - right[1],
        left[2] - right[2],
    )


def _cross(left: Vec3, right: Vec3) -> Vec3:
    return (
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    )


def _dot(left: Vec3, right: Vec3) -> float:
    return left[0] * right[0] + left[1] * right[1] + left[2] * right[2]


def _length(vector: Vec3) -> float:
    return math.sqrt(_dot(vector, vector))


def _normal_from_polygon(coordinates: tuple[Vec3, ...]) -> Vec3 | None:
    origin = coordinates[0]
    for index in range(1, len(coordinates) - 1):
        first = _subtract(coordinates[index], origin)
        for next_index in range(index + 1, len(coordinates)):
            second = _subtract(coordinates[next_index], origin)
            normal = _cross(first, second)
            magnitude = _length(normal)
            if magnitude > 1e-15:
                return (
                    normal[0] / magnitude,
                    normal[1] / magnitude,
                    normal[2] / magnitude,
                )
    return None


def _area_vector(coordinates: Iterable[Vec3]) -> Vec3:
    points = tuple(coordinates)
    x = y = z = 0.0
    for index, point in enumerate(points):
        following = points[(index + 1) % len(points)]
        cross = _cross(point, following)
        x += cross[0]
        y += cross[1]
        z += cross[2]
    return (x * 0.5, y * 0.5, z * 0.5)


def _edge(left: int, right: int) -> Edge:
    return (left, right) if left < right else (right, left)


def _face_geometry(model: Any, shell: Any, face: Any) -> FaceGeometry | None:
    if str(face.is_a()) != "IfcFace":
        return None
    inverses = tuple(model.get_inverse(face))
    if any(
        inverse.id() != shell.id() and not inverse.is_a("IfcConnectedFaceSet")
        for inverse in inverses
    ):
        return None

    bounds = tuple(face.Bounds or ())
    if len(bounds) != 1 or str(bounds[0].is_a()) != "IfcFaceOuterBound":
        return None
    bound = bounds[0]
    loop = bound.Bound
    if loop is None or str(loop.is_a()) != "IfcPolyLoop":
        return None

    points = tuple(loop.Polygon or ())
    if not bool(bound.Orientation):
        points = tuple(reversed(points))
    if len(points) < 3 or any(not point.id() for point in points):
        return None
    if len({int(point.id()) for point in points}) != len(points):
        return None

    try:
        coordinates = tuple(
            tuple(float(value) for value in point.Coordinates[:3]) for point in points
        )
    except (AttributeError, TypeError, ValueError):
        return None
    if any(len(coordinate) != 3 for coordinate in coordinates):
        return None

    normal = _normal_from_polygon(coordinates)
    if normal is None:
        return None
    area = _dot(_area_vector(coordinates), normal)
    if area <= 1e-15:
        return None
    return FaceGeometry(
        face=face,
        points=points,
        coordinates=coordinates,
        normal=normal,
        plane_offset=_dot(normal, coordinates[0]),
    )


def _coplanar(
    left: FaceGeometry,
    right: FaceGeometry,
    *,
    normal_tolerance: float,
    plane_tolerance: float,
) -> bool:
    if _dot(left.normal, right.normal) < 1.0 - normal_tolerance:
        return False
    scale = max(
        1.0,
        abs(left.plane_offset),
        abs(right.plane_offset),
    )
    if abs(left.plane_offset - right.plane_offset) > plane_tolerance * scale:
        return False
    return all(
        abs(_dot(left.normal, point) - left.plane_offset)
        <= plane_tolerance
        * max(1.0, abs(left.plane_offset), max(abs(value) for value in point))
        for point in right.coordinates
    )


def _trace_boundary_loops(
    faces: tuple[FaceGeometry, ...],
) -> tuple[tuple[Any, ...], ...] | None:
    occurrences: dict[Edge, list[tuple[int, int]]] = defaultdict(list)
    points_by_id: dict[int, Any] = {}
    for geometry in faces:
        point_ids = tuple(int(point.id()) for point in geometry.points)
        points_by_id.update((int(point.id()), point) for point in geometry.points)
        for index, left in enumerate(point_ids):
            right = point_ids[(index + 1) % len(point_ids)]
            occurrences[_edge(left, right)].append((left, right))

    boundary: list[tuple[int, int]] = []
    for directed in occurrences.values():
        if len(directed) == 1:
            boundary.append(directed[0])
        elif len(directed) == 2:
            if directed[0] != (directed[1][1], directed[1][0]):
                return None
        else:
            return None

    outgoing: dict[int, int] = {}
    incoming: Counter[int] = Counter()
    for left, right in boundary:
        if left in outgoing:
            return None
        outgoing[left] = right
        incoming[right] += 1
    vertices = set(outgoing) | set(incoming)
    if not vertices or any(incoming[vertex] != 1 for vertex in vertices):
        return None
    if any(vertex not in outgoing for vertex in vertices):
        return None

    unused = set(boundary)
    loops: list[tuple[Any, ...]] = []
    while unused:
        first_left, first_right = next(iter(unused))
        point_ids = [first_left]
        left, right = first_left, first_right
        for _ in range(len(boundary) + 1):
            edge = (left, right)
            if edge not in unused:
                return None
            unused.remove(edge)
            if right == first_left:
                break
            point_ids.append(right)
            left, right = right, outgoing[right]
        else:
            return None
        if len(point_ids) < 3 or len(set(point_ids)) != len(point_ids):
            return None
        loops.append(tuple(points_by_id[point_id] for point_id in point_ids))
    return tuple(loops)


def _validated_loops(
    faces: tuple[FaceGeometry, ...],
    *,
    area_tolerance: float,
) -> tuple[tuple[Any, ...], tuple[tuple[Any, ...], ...]] | None:
    loops = _trace_boundary_loops(faces)
    if not loops:
        return None
    normal = faces[0].normal
    loop_areas = [
        _dot(
            _area_vector(
                tuple(
                    tuple(float(value) for value in point.Coordinates[:3])
                    for point in loop
                )
            ),
            normal,
        )
        for loop in loops
    ]
    outer_index = max(range(len(loops)), key=lambda index: abs(loop_areas[index]))
    outer_area = loop_areas[outer_index]
    if outer_area <= 1e-15:
        return None
    holes = tuple(loop for index, loop in enumerate(loops) if index != outer_index)
    hole_areas = [area for index, area in enumerate(loop_areas) if index != outer_index]
    if any(area >= -1e-15 for area in hole_areas):
        return None

    source_area = sum(_dot(_area_vector(face.coordinates), normal) for face in faces)
    merged_area = outer_area + sum(hole_areas)
    tolerance = area_tolerance * max(1.0, abs(source_area), abs(merged_area))
    if source_area <= 1e-15 or abs(source_area - merged_area) > tolerance:
        return None
    return loops[outer_index], holes


def _collect_removable_ids(
    model: Any,
    candidates: Iterable[Any],
    *,
    removed_parent_ids: set[int] | None,
) -> tuple[set[int], dict[int, Any]]:
    unique = {
        int(entity.id()): entity
        for entity in candidates
        if entity is not None and entity.id()
    }
    removable: set[int] = set()
    for entity_id, entity in unique.items():
        inverses = tuple(model.get_inverse(entity))
        if not inverses or (
            removed_parent_ids is not None
            and all(
                inverse.id() and int(inverse.id()) in removed_parent_ids
                for inverse in inverses
            )
        ):
            removable.add(entity_id)
    return removable, unique


def build_planar_merge_plan(
    model: Any,
    *,
    sink: EventSink = null_sink,
    normal_tolerance: float = 1e-12,
    plane_tolerance: float = 1e-12,
    area_tolerance: float = 1e-10,
) -> PlanarMergePlan:
    started = time.perf_counter()
    initial_max_id = max((int(entity.id()) for entity in model), default=0)
    shells = list(model.by_type("IfcClosedShell", include_subtypes=False))
    changed_shells: dict[int, Any] = {}
    removed_ids: set[int] = set()
    candidate_faces: list[Any] = []
    candidate_bounds: list[Any] = []
    candidate_loops: list[Any] = []
    candidate_points: list[Any] = []
    clusters_considered = 0
    clusters_merged = 0
    faces_merged = 0
    shells_changed = 0

    for shell_index, shell in enumerate(shells, start=1):
        original_faces = tuple(shell.CfsFaces or ())
        geometries = [
            geometry
            for face in original_faces
            if (geometry := _face_geometry(model, shell, face)) is not None
        ]
        if len(geometries) < 2:
            continue

        edge_faces: dict[Edge, list[int]] = defaultdict(list)
        for index, geometry in enumerate(geometries):
            point_ids = tuple(int(point.id()) for point in geometry.points)
            for point_index, left in enumerate(point_ids):
                right = point_ids[(point_index + 1) % len(point_ids)]
                edge_faces[_edge(left, right)].append(index)

        union_find = _UnionFind(len(geometries))
        for face_indexes in edge_faces.values():
            if len(face_indexes) != 2:
                continue
            left, right = face_indexes
            if _coplanar(
                geometries[left],
                geometries[right],
                normal_tolerance=normal_tolerance,
                plane_tolerance=plane_tolerance,
            ):
                union_find.union(left, right)

        groups: dict[int, list[FaceGeometry]] = defaultdict(list)
        for index, geometry in enumerate(geometries):
            groups[union_find.find(index)].append(geometry)

        replacements: dict[int, Any] = {}
        for group in groups.values():
            if len(group) < 2:
                continue
            clusters_considered += 1
            group_tuple = tuple(group)
            validated = _validated_loops(
                group_tuple,
                area_tolerance=area_tolerance,
            )
            if validated is None:
                continue
            outer, holes = validated
            outer_loop = model.create_entity("IfcPolyLoop", Polygon=outer)
            bounds: list[Any] = [
                model.create_entity(
                    "IfcFaceOuterBound",
                    Bound=outer_loop,
                    Orientation=True,
                )
            ]
            for hole in holes:
                hole_loop = model.create_entity("IfcPolyLoop", Polygon=hole)
                bounds.append(
                    model.create_entity(
                        "IfcFaceBound",
                        Bound=hole_loop,
                        Orientation=True,
                    )
                )
            merged_face = model.create_entity("IfcFace", Bounds=tuple(bounds))
            for geometry in group_tuple:
                replacements[int(geometry.face.id())] = merged_face
                candidate_faces.append(geometry.face)
                for bound in tuple(geometry.face.Bounds or ()):
                    candidate_bounds.append(bound)
                    loop = getattr(bound, "Bound", None)
                    if loop is not None:
                        candidate_loops.append(loop)
                        candidate_points.extend(
                            tuple(getattr(loop, "Polygon", ()) or ())
                        )
            clusters_merged += 1
            faces_merged += len(group_tuple) - 1

        if not replacements:
            continue
        rebuilt_faces: list[Any] = []
        emitted_new: set[int] = set()
        for face in original_faces:
            replacement = replacements.get(int(face.id()))
            if replacement is None:
                rebuilt_faces.append(face)
            elif int(replacement.id()) not in emitted_new:
                rebuilt_faces.append(replacement)
                emitted_new.add(int(replacement.id()))
        shell.CfsFaces = tuple(rebuilt_faces)
        changed_shells[int(shell.id())] = shell
        shells_changed += 1

        if shell_index % 25 == 0 or shell_index == len(shells):
            emit(
                sink,
                stage="merge-planar-faces",
                progress=shell_index / max(len(shells), 1),
                message=(
                    f"Проверено оболочек: {shell_index}/{len(shells)}, "
                    f"объединено граней: {faces_merged}"
                ),
            )

    removable_faces, face_entities = _collect_removable_ids(
        model,
        candidate_faces,
        removed_parent_ids=None,
    )
    removed_ids.update(removable_faces)
    emit(
        sink,
        stage="prune-planar-entities",
        progress=0.25,
        message=f"Осиротевших граней: {len(removable_faces)}",
    )
    removable_bounds, bound_entities = _collect_removable_ids(
        model,
        candidate_bounds,
        removed_parent_ids=removable_faces,
    )
    removed_ids.update(removable_bounds)
    emit(
        sink,
        stage="prune-planar-entities",
        progress=0.5,
        message=f"Осиротевших границ: {len(removable_bounds)}",
    )
    removable_loops, loop_entities = _collect_removable_ids(
        model,
        candidate_loops,
        removed_parent_ids=removable_bounds,
    )
    removed_ids.update(removable_loops)
    emit(
        sink,
        stage="prune-planar-entities",
        progress=0.75,
        message=f"Осиротевших контуров: {len(removable_loops)}",
    )
    removable_points, point_entities = _collect_removable_ids(
        model,
        candidate_points,
        removed_parent_ids=removable_loops,
    )
    removed_ids.update(removable_points)
    emit(
        sink,
        stage="prune-planar-entities",
        progress=1.0,
        message=f"Осиротевших точек: {len(removable_points)}",
    )

    current_entities = list(model)
    changed_records = {
        shell_id: (str(shell) + ";\n").encode("utf-8")
        for shell_id, shell in changed_shells.items()
    }
    appended_records = tuple(
        (str(entity) + ";\n").encode("utf-8")
        for entity in sorted(
            (
                entity
                for entity in current_entities
                if int(entity.id()) > initial_max_id
            ),
            key=lambda entity: int(entity.id()),
        )
    )
    removed_entities_by_id = {
        **face_entities,
        **bound_entities,
        **loop_entities,
        **point_entities,
    }
    removed_by_type = Counter(
        str(removed_entities_by_id[entity_id].is_a()) for entity_id in removed_ids
    )
    appended_by_type = Counter(
        str(entity.is_a())
        for entity in current_entities
        if int(entity.id()) > initial_max_id
    )
    expected_entity_count_delta = {
        type_name: appended_by_type[type_name] - removed_by_type[type_name]
        for type_name in sorted(set(removed_by_type) | set(appended_by_type))
        if appended_by_type[type_name] != removed_by_type[type_name]
    }
    metrics = {
        "mode": "strict-coplanar-face-merge",
        "shells_total": len(shells),
        "shells_changed": shells_changed,
        "clusters_considered": clusters_considered,
        "clusters_merged": clusters_merged,
        "faces_removed_net": -expected_entity_count_delta.get("IfcFace", 0),
        "faces_merged": faces_merged,
        "removed_entities": len(removed_ids),
        "appended_entities": len(appended_records),
        "normal_tolerance": normal_tolerance,
        "plane_tolerance": plane_tolerance,
        "area_tolerance": area_tolerance,
        "elapsed_seconds": round(time.perf_counter() - started, 3),
    }
    return PlanarMergePlan(
        changed_records=changed_records,
        removed_ids=frozenset(removed_ids),
        appended_records=appended_records,
        expected_entity_count_delta=expected_entity_count_delta,
        metrics=metrics,
    )


def write_planar_merge_result(
    source_path: Path,
    output_path: Path,
    plan: PlanarMergePlan,
    *,
    sink: EventSink = null_sink,
) -> None:
    emit(
        sink,
        stage="serialize-planar",
        progress=0.0,
        message="Сериализуется compact STEP-граф",
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
                    stage="serialize-planar",
                    progress=processed_bytes / max(total_bytes, 1),
                    message=f"Обработано STEP-строк: {index:,}".replace(",", " "),
                )

    if plan.appended_records and not append_written:
        raise RuntimeError("Could not locate IFC DATA section terminator")
