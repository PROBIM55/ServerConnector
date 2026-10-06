from __future__ import annotations

import math
import os
import time
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import ifcopenshell
import ifcopenshell.geom
import ifcopenshell.util.element
import ifcopenshell.util.placement
import numpy as np

from .events import EventSink, emit, null_sink

_EPSILON = 1e-10


@dataclass(frozen=True)
class OrientationRepairResult:
    metrics: dict[str, Any]
    entity_count_delta: dict[str, int]


class UnsupportedMirroredMapError(RuntimeError):
    code = "UNSUPPORTED_MIRRORED_MAP"

    def __init__(self, map_id: int, reason: str) -> None:
        self.details = {"map_id": map_id, "reason": reason}
        super().__init__(
            f"Зеркальная IfcRepresentationMap #{map_id} не поддерживается: "
            f"{reason}. Оптимизированный IFC не создан"
        )


@dataclass(frozen=True)
class _BrepOrientationPlan:
    face_flips: tuple[bool, ...]
    component_count: int
    boundary_edges: int
    non_manifold_edges: int
    ambiguous_components: int
    signed_volume_before: float
    signed_volume_after: float


@dataclass(frozen=True)
class _ProductMesh:
    vertices: tuple[float, ...]
    faces: tuple[int, ...]


def _entity_counts(model: Any) -> Counter[str]:
    return Counter(str(entity.is_a()) for entity in model)


def _mapped_matrix(item: Any) -> np.ndarray:
    matrix = ifcopenshell.util.placement.get_mappeditem_transformation(item)
    if matrix is None:
        raise RuntimeError(f"Не удалось прочитать матрицу IfcMappedItem #{item.id()}")
    return np.asarray(matrix, dtype=float)


def _determinant(matrix: np.ndarray) -> float:
    return float(np.linalg.det(matrix[:3, :3]))


def _loop_entities(bound: Any) -> list[Any]:
    loop = getattr(bound, "Bound", None)
    if loop is None or not loop.is_a("IfcPolyLoop"):
        raise RuntimeError(
            "Коррекция ориентации поддерживает только полигональные границы "
            f"IfcPolyLoop, получено {loop.is_a() if loop is not None else 'None'}"
        )
    points = list(loop.Polygon)
    for point in points:
        coordinates = tuple(float(value) for value in point.Coordinates)
        if len(coordinates) != 3:
            raise RuntimeError(
                f"Точка #{point.id()} имеет размерность {len(coordinates)}, ожидалась 3D"
            )
    if not bool(bound.Orientation):
        points.reverse()
    return points


def _loop_points(bound: Any) -> list[tuple[float, float, float]]:
    return [
        tuple(float(value) for value in point.Coordinates)
        for point in _loop_entities(bound)
    ]


def _signed_tetrahedron_volume(
    first: tuple[float, float, float],
    second: tuple[float, float, float],
    third: tuple[float, float, float],
) -> float:
    return (
        first[0] * (second[1] * third[2] - second[2] * third[1])
        - first[1] * (second[0] * third[2] - second[2] * third[0])
        + first[2] * (second[0] * third[1] - second[1] * third[0])
    ) / 6.0


def _face_points(face: Any, *, flipped: bool = False) -> list[list[tuple[float, ...]]]:
    loops = [_loop_points(bound) for bound in face.Bounds]
    if flipped:
        for points in loops:
            points.reverse()
    return loops


def _faces_signed_volume(
    faces: tuple[Any, ...],
    face_flips: dict[int, bool] | None = None,
) -> float:
    loops = [
        points
        for face_index, face in enumerate(faces)
        for points in _face_points(
            face,
            flipped=bool(face_flips and face_flips.get(face_index, False)),
        )
        if points
    ]
    all_points = [point for points in loops for point in points]
    if not all_points:
        return 0.0
    reference = tuple(
        sum(point[axis] for point in all_points) / len(all_points) for axis in range(3)
    )
    volume = 0.0
    for points in loops:
        if len(points) < 3:
            continue
        translated = [
            tuple(point[axis] - reference[axis] for axis in range(3))
            for point in points
        ]
        anchor = translated[0]
        for index in range(1, len(translated) - 1):
            volume += _signed_tetrahedron_volume(
                anchor,
                translated[index],
                translated[index + 1],
            )
    return volume


def _point_key(point: Any) -> tuple[float, float, float]:
    coordinates = tuple(float(value) for value in point.Coordinates)
    if len(coordinates) != 3:
        raise RuntimeError(
            f"Точка #{point.id()} имеет размерность {len(coordinates)}, ожидалась 3D"
        )
    return tuple(round(value, 9) for value in coordinates)


def _brep_orientation_plan(brep: Any) -> _BrepOrientationPlan:
    faces = tuple(brep.Outer.CfsFaces)
    edge_uses: dict[
        tuple[tuple[float, float, float], tuple[float, float, float]],
        list[tuple[int, int]],
    ] = defaultdict(list)
    for face_index, face in enumerate(faces):
        for bound in face.Bounds:
            points = _loop_entities(bound)
            for index, first in enumerate(points):
                second = points[(index + 1) % len(points)]
                first_key = _point_key(first)
                second_key = _point_key(second)
                if first_key == second_key:
                    continue
                edge_key = tuple(sorted((first_key, second_key)))
                direction = 1 if first_key == edge_key[0] else -1
                edge_uses[edge_key].append((face_index, direction))

    boundary_edges = sum(len(uses) == 1 for uses in edge_uses.values())
    non_manifold_edges = sum(len(uses) > 2 for uses in edge_uses.values())
    if non_manifold_edges:
        raise RuntimeError(
            f"IfcFacetedBrep #{brep.id()} содержит неманифолдные ребра: "
            f"{non_manifold_edges}"
        )

    graph: dict[int, list[tuple[int, bool]]] = defaultdict(list)
    for uses in edge_uses.values():
        if len(uses) != 2:
            continue
        (first_face, first_direction), (second_face, second_direction) = uses
        must_differ = first_direction == second_direction
        graph[first_face].append((second_face, must_differ))
        graph[second_face].append((first_face, must_differ))

    assigned: dict[int, bool] = {}
    components: list[tuple[int, ...]] = []
    for start in range(len(faces)):
        if start in assigned:
            continue
        assigned[start] = False
        pending = [start]
        component: list[int] = []
        while pending:
            face_index = pending.pop()
            component.append(face_index)
            for neighbour, must_differ in graph.get(face_index, ()):
                expected = assigned[face_index] ^ must_differ
                previous = assigned.get(neighbour)
                if previous is None:
                    assigned[neighbour] = expected
                    pending.append(neighbour)
                elif previous != expected:
                    raise RuntimeError(
                        f"Оболочка IfcFacetedBrep #{brep.id()} неориентируема"
                    )
        components.append(tuple(component))

    signed_before = _faces_signed_volume(faces)
    ambiguous_components = 0
    for component in components:
        component_faces = tuple(faces[index] for index in component)
        component_flips = {
            local_index: assigned[face_index]
            for local_index, face_index in enumerate(component)
        }
        signed = _faces_signed_volume(component_faces, component_flips)
        if signed < -_EPSILON:
            for face_index in component:
                assigned[face_index] = not assigned[face_index]
        elif abs(signed) <= _EPSILON:
            ambiguous_components += 1

    signed_after = _faces_signed_volume(faces, assigned)
    return _BrepOrientationPlan(
        face_flips=tuple(assigned[index] for index in range(len(faces))),
        component_count=len(components),
        boundary_edges=boundary_edges,
        non_manifold_edges=non_manifold_edges,
        ambiguous_components=ambiguous_components,
        signed_volume_before=signed_before,
        signed_volume_after=signed_after,
    )


def _apply_brep_orientation_plan(
    brep: Any,
    plan: _BrepOrientationPlan,
) -> int:
    flipped_faces = 0
    faces = tuple(brep.Outer.CfsFaces)
    if len(faces) != len(plan.face_flips):
        raise RuntimeError("Состав граней изменился во время коррекции ориентации")
    for face, flipped in zip(faces, plan.face_flips, strict=True):
        for bound in face.Bounds:
            loop = bound.Bound
            polygon = list(loop.Polygon)
            if not bool(bound.Orientation):
                polygon.reverse()
            if flipped:
                polygon.reverse()
            loop.Polygon = tuple(polygon)
            # Some IFC consumers triangulate complex FaceBounds incorrectly when
            # Orientation=False. Store the same effective winding canonically in
            # the PolyLoop and keep the flag positive.
            bound.Orientation = True
        if flipped:
            flipped_faces += 1
    return flipped_faces


def _representation_breps(
    representation_map: Any,
    *,
    required: bool = True,
) -> tuple[Any, ...]:
    representation = representation_map.MappedRepresentation
    items = tuple(representation.Items or ())
    unsupported = sorted(
        {str(item.is_a()) for item in items if not item.is_a("IfcFacetedBrep")}
    )
    if unsupported and required:
        raise UnsupportedMirroredMapError(
            int(representation_map.id()),
            "ожидался IfcFacetedBrep, обнаружены " + ", ".join(unsupported),
        )
    if unsupported:
        return ()
    return items


def _copy_inverse_appearance(
    model: Any,
    copied_entities: dict[int, Any],
) -> None:
    for source_id, target in tuple(copied_entities.items()):
        source = model.by_id(source_id)
        if source is None:
            continue
        for styled_item in tuple(getattr(source, "StyledByItem", None) or ()):
            model.create_entity(
                "IfcStyledItem",
                Item=target,
                Styles=tuple(styled_item.Styles or ()),
                Name=styled_item.Name,
            )
        for layer in tuple(getattr(source, "LayerAssignments", None) or ()):
            assigned = tuple(layer.AssignedItems or ())
            if target not in assigned:
                layer.AssignedItems = (*assigned, target)


def _copy_representation_map(model: Any, source_map: Any) -> Any:
    source_representation = source_map.MappedRepresentation
    copied_entities: dict[int, Any] = {}
    copied_items = tuple(
        ifcopenshell.util.element.copy_deep(
            model,
            item,
            copied_entities=copied_entities,
        )
        for item in source_representation.Items
    )
    copied_representation = model.create_entity(
        source_representation.is_a(),
        ContextOfItems=source_representation.ContextOfItems,
        RepresentationIdentifier=source_representation.RepresentationIdentifier,
        RepresentationType=source_representation.RepresentationType,
        Items=copied_items,
    )
    copied_entities[int(source_representation.id())] = copied_representation
    _copy_inverse_appearance(model, copied_entities)
    return model.create_entity(
        "IfcRepresentationMap",
        MappingOrigin=source_map.MappingOrigin,
        MappedRepresentation=copied_representation,
    )


def _copy_item_appearance(model: Any, source: Any, target: Any) -> None:
    for styled_item in tuple(getattr(source, "StyledByItem", None) or ()):
        model.create_entity(
            "IfcStyledItem",
            Item=target,
            Styles=tuple(styled_item.Styles or ()),
            Name=styled_item.Name,
        )
    for layer in tuple(getattr(source, "LayerAssignments", None) or ()):
        assigned = tuple(layer.AssignedItems or ())
        if target not in assigned:
            layer.AssignedItems = (*assigned, target)


def _mapped_item_product(model: Any, item: Any) -> Any:
    representations = tuple(
        inverse
        for inverse in model.get_inverse(item)
        if inverse.is_a("IfcShapeRepresentation")
    )
    if len(representations) != 1 or tuple(representations[0].Items or ()) != (item,):
        raise UnsupportedMirroredMapError(
            int(item.MappingSource.id()),
            f"IfcMappedItem #{item.id()} должен быть единственным элементом "
            "представления продукта",
        )
    definitions = tuple(
        inverse
        for inverse in model.get_inverse(representations[0])
        if inverse.is_a("IfcProductDefinitionShape")
    )
    products = tuple(
        inverse
        for definition in definitions
        for inverse in model.get_inverse(definition)
        if inverse.is_a("IfcProduct")
    )
    if len(products) != 1:
        raise UnsupportedMirroredMapError(
            int(item.MappingSource.id()),
            f"для IfcMappedItem #{item.id()} не удалось однозначно определить продукт",
        )
    return products[0]


def _mesh_signed_volume(
    vertices: tuple[tuple[float, float, float], ...],
    flat_faces: tuple[int, ...],
) -> float:
    if not vertices:
        return 0.0
    reference = tuple(
        sum(vertex[axis] for vertex in vertices) / len(vertices) for axis in range(3)
    )
    volume = 0.0
    for index in range(0, len(flat_faces), 3):
        points = tuple(vertices[flat_faces[index + offset]] for offset in range(3))
        translated = tuple(
            tuple(point[axis] - reference[axis] for axis in range(3))
            for point in points
        )
        volume += _signed_tetrahedron_volume(*translated)
    return volume


def _build_product_meshes(
    model: Any,
    items: tuple[Any, ...],
    *,
    sink: EventSink,
) -> dict[int, _ProductMesh]:
    item_products = {
        int(item.id()): _mapped_item_product(model, item) for item in items
    }
    products = tuple(
        {int(product.id()): product for product in item_products.values()}.values()
    )
    settings = ifcopenshell.geom.settings()
    iterator = ifcopenshell.geom.iterator(
        settings,
        model,
        max(1, min(os.cpu_count() or 1, 4)),
        include=list(products),
    )
    product_meshes: dict[int, _ProductMesh] = {}
    completed = 0
    if iterator.initialize():
        while True:
            shape = iterator.get()
            product_meshes[int(shape.id)] = _ProductMesh(
                vertices=tuple(float(value) for value in shape.geometry.verts),
                faces=tuple(int(value) for value in shape.geometry.faces),
            )
            completed += 1
            emit(
                sink,
                stage="tessellate-mirrored-products",
                progress=completed / max(len(products), 1),
                message=(
                    "Фиксируется геометрия зеркальных экземпляров: "
                    f"{completed}/{len(products)}"
                ),
            )
            if not iterator.next():
                break
    missing = sorted({int(product.id()) for product in products} - set(product_meshes))
    if missing:
        raise RuntimeError(
            "Не удалось тесселировать зеркальные продукты: "
            + ", ".join(str(step_id) for step_id in missing)
        )
    return {
        item_id: product_meshes[int(product.id())]
        for item_id, product in item_products.items()
    }


def _mirrored_tessellated_map(
    model: Any,
    source_map: Any,
    positive_target: Any,
    mesh: _ProductMesh,
) -> tuple[Any, int]:
    source_representation = source_map.MappedRepresentation
    flat_vertices = mesh.vertices
    flat_faces = mesh.faces

    target_matrix = ifcopenshell.util.placement.get_cartesiantransformationoperator3d(
        positive_target
    )
    origin_matrix = ifcopenshell.util.placement.get_axis2placement(
        source_map.MappingOrigin
    )
    inverse_mapping = np.linalg.inv(target_matrix @ origin_matrix)
    product_vertices = tuple(
        (
            flat_vertices[index],
            flat_vertices[index + 1],
            flat_vertices[index + 2],
        )
        for index in range(0, len(flat_vertices), 3)
    )
    coordinates = tuple(
        tuple(float(value) for value in (inverse_mapping @ (*vertex, 1.0))[:3])
        for vertex in product_vertices
    )
    reverse = _mesh_signed_volume(product_vertices, flat_faces) < -_EPSILON
    triangles = tuple(
        tuple(
            flat_faces[index + offset] + 1
            for offset in ((0, 2, 1) if reverse else (0, 1, 2))
        )
        for index in range(0, len(flat_faces), 3)
    )
    points = tuple(
        model.create_entity("IfcCartesianPoint", Coordinates=coordinate)
        for coordinate in coordinates
    )
    faces = []
    for triangle in triangles:
        loop = model.create_entity(
            "IfcPolyLoop",
            Polygon=tuple(points[index - 1] for index in triangle),
        )
        bound = model.create_entity(
            "IfcFaceOuterBound",
            Bound=loop,
            Orientation=True,
        )
        faces.append(model.create_entity("IfcFace", Bounds=(bound,)))
    shell = model.create_entity("IfcClosedShell", CfsFaces=tuple(faces))
    tessellated = model.create_entity("IfcFacetedBrep", Outer=shell)
    source_items = tuple(source_representation.Items or ())
    if len(source_items) != 1:
        raise UnsupportedMirroredMapError(
            int(source_map.id()),
            f"карта содержит элементов геометрии: {len(source_items)}, ожидался один",
        )
    _copy_item_appearance(model, source_items[0], tessellated)

    representation = model.create_entity(
        source_representation.is_a(),
        ContextOfItems=source_representation.ContextOfItems,
        RepresentationIdentifier=source_representation.RepresentationIdentifier,
        RepresentationType="Brep",
        Items=(tessellated,),
    )
    _copy_item_appearance(model, source_representation, representation)
    return (
        model.create_entity(
            "IfcRepresentationMap",
            MappingOrigin=source_map.MappingOrigin,
            MappedRepresentation=representation,
        ),
        len(triangles),
    )


def _direction(model: Any, values: np.ndarray) -> Any:
    return model.create_entity(
        "IfcDirection",
        DirectionRatios=tuple(float(value) for value in values),
    )


def _operator_from_matrix(model: Any, original: Any, matrix: np.ndarray) -> Any:
    linear = matrix[:3, :3]
    scales = np.linalg.norm(linear, axis=0)
    if any(float(scale) <= _EPSILON for scale in scales):
        raise RuntimeError(
            f"Вырожденная матрица IfcMappedItem для оператора #{original.id()}"
        )
    axes = linear / scales
    gram = axes.T @ axes
    if not np.allclose(gram, np.eye(3), rtol=1e-8, atol=1e-8):
        raise RuntimeError(
            f"Матрица оператора #{original.id()} содержит сдвиг осей и не может "
            "быть безопасно нормализована"
        )
    if float(np.linalg.det(axes)) <= 0.0:
        raise RuntimeError(
            f"Не удалось получить правую систему координат для оператора #{original.id()}"
        )

    common = {
        "Axis1": _direction(model, axes[:, 0]),
        "Axis2": _direction(model, axes[:, 1]),
        "LocalOrigin": model.create_entity(
            "IfcCartesianPoint",
            Coordinates=tuple(float(value) for value in matrix[:3, 3]),
        ),
        "Scale": float(scales[0]),
        "Axis3": _direction(model, axes[:, 2]),
    }
    non_uniform = original.is_a("IfcCartesianTransformationOperator3DnonUniform")
    non_uniform = non_uniform or not math.isclose(
        float(scales[0]), float(scales[1]), rel_tol=1e-9, abs_tol=1e-9
    )
    non_uniform = non_uniform or not math.isclose(
        float(scales[0]), float(scales[2]), rel_tol=1e-9, abs_tol=1e-9
    )
    if non_uniform:
        return model.create_entity(
            "IfcCartesianTransformationOperator3DnonUniform",
            **common,
            Scale2=float(scales[1]),
            Scale3=float(scales[2]),
        )
    return model.create_entity("IfcCartesianTransformationOperator3D", **common)


def _positive_target(model: Any, item: Any) -> Any:
    target_matrix = ifcopenshell.util.placement.get_cartesiantransformationoperator3d(
        item.MappingTarget
    )
    origin_matrix = ifcopenshell.util.placement.get_axis2placement(
        item.MappingSource.MappingOrigin
    )
    reflection = np.eye(4)
    reflection[0, 0] = -1.0
    corrected = (
        target_matrix @ origin_matrix @ reflection @ np.linalg.inv(origin_matrix)
    )
    if _determinant(corrected) <= 0.0:
        raise RuntimeError(
            f"Не удалось устранить зеркальность IfcMappedItem #{item.id()}"
        )
    return _operator_from_matrix(model, item.MappingTarget, corrected)


def _remove_unused_operator(model: Any, operator: Any) -> None:
    if model.get_inverse(operator):
        return
    children = tuple(
        value
        for value in (
            operator.Axis1,
            operator.Axis2,
            operator.LocalOrigin,
            operator.Axis3,
        )
        if value is not None
    )
    model.remove(operator)
    for child in children:
        if not model.get_inverse(child):
            ifcopenshell.util.element.remove_deep2(model, child)


def _item_groups(model: Any) -> dict[int, list[Any]]:
    groups: dict[int, list[Any]] = defaultdict(list)
    for item in model.by_type("IfcMappedItem", include_subtypes=False):
        groups[int(item.MappingSource.id())].append(item)
    return groups


def repair_mapped_item_orientation(
    model: Any,
    *,
    sink: EventSink = null_sink,
) -> OrientationRepairResult:
    started = time.perf_counter()
    before = _entity_counts(model)
    groups = _item_groups(model)
    analyses: list[dict[str, Any]] = []
    negative_before = 0
    inward_before = 0

    # Validate the complete repair set before changing the IFC graph.
    for map_id, items in sorted(groups.items()):
        representation_map = model.by_id(map_id)
        determinants = [_determinant(_mapped_matrix(item)) for item in items]
        if any(abs(value) <= _EPSILON for value in determinants):
            raise RuntimeError(f"Вырожденная матрица в IfcRepresentationMap #{map_id}")
        negative_items = tuple(
            item
            for item, determinant in zip(items, determinants, strict=True)
            if determinant < 0.0
        )
        positive_items = tuple(
            item
            for item, determinant in zip(items, determinants, strict=True)
            if determinant > 0.0
        )
        try:
            breps = _representation_breps(
                representation_map,
                required=bool(negative_items),
            )
            orientation_plans = tuple(_brep_orientation_plan(brep) for brep in breps)
            if negative_items and len(breps) != 1:
                raise UnsupportedMirroredMapError(
                    map_id,
                    f"карта содержит IfcFacetedBrep: {len(breps)}, ожидался один",
                )
            if negative_items:
                boundary_edges = sum(plan.boundary_edges for plan in orientation_plans)
                ambiguous = sum(plan.ambiguous_components for plan in orientation_plans)
                if boundary_edges or ambiguous:
                    raise UnsupportedMirroredMapError(
                        map_id,
                        "оболочка должна быть замкнутой и однозначно ориентируемой "
                        f"(граничных ребер: {boundary_edges}, "
                        f"неоднозначных компонент: {ambiguous})",
                    )
                for item in negative_items:
                    _mapped_item_product(model, item)
        except UnsupportedMirroredMapError:
            raise
        except RuntimeError as exc:
            if negative_items:
                raise UnsupportedMirroredMapError(map_id, str(exc)) from exc
            raise
        negative_before += len(negative_items)
        if any(any(plan.face_flips) for plan in orientation_plans):
            inward_before += len(positive_items)
        analyses.append(
            {
                "map": representation_map,
                "breps": breps,
                "orientation_plans": orientation_plans,
                "negative_items": negative_items,
                "positive_items": positive_items,
            }
        )

    mirrored_maps_created = 0
    oriented_maps_created = 0
    face_sets_reoriented = 0
    faces_reoriented = 0
    triangles_reoriented = 0
    boundary_edges_detected = 0
    ambiguous_components = 0
    repaired_items = 0
    total = len(analyses)
    all_negative_items = tuple(
        item for analysis in analyses for item in analysis["negative_items"]
    )
    product_meshes = (
        _build_product_meshes(
            model,
            all_negative_items,
            sink=sink,
        )
        if all_negative_items
        else {}
    )
    for index, analysis in enumerate(analyses, start=1):
        representation_map = analysis["map"]
        representation_map_id = int(representation_map.id())
        breps = analysis["breps"]
        orientation_plans = analysis["orientation_plans"]
        positive_items = analysis["positive_items"]
        negative_items = analysis["negative_items"]

        # Never mutate source geometry. Exact optimization may legitimately share
        # points and shells between otherwise independent representation maps.
        if positive_items and any(any(plan.face_flips) for plan in orientation_plans):
            oriented_map = _copy_representation_map(model, representation_map)
            oriented_breps = _representation_breps(oriented_map)
            oriented_maps_created += 1
            for brep, plan in zip(oriented_breps, orientation_plans, strict=True):
                flipped = _apply_brep_orientation_plan(brep, plan)
                if flipped:
                    face_sets_reoriented += 1
                    faces_reoriented += flipped
                boundary_edges_detected += plan.boundary_edges
                ambiguous_components += plan.ambiguous_components
            for item in positive_items:
                item.MappingSource = oriented_map
                repaired_items += 1

        if negative_items:
            for item in negative_items:
                old_target = item.MappingTarget
                positive_target = _positive_target(model, item)
                mirrored_map, mirrored_triangles = _mirrored_tessellated_map(
                    model,
                    representation_map,
                    positive_target,
                    product_meshes[int(item.id())],
                )
                mirrored_maps_created += 1
                face_sets_reoriented += len(breps)
                triangles_reoriented += mirrored_triangles
                item.MappingSource = mirrored_map
                item.MappingTarget = positive_target
                repaired_items += 1
                _remove_unused_operator(model, old_target)

        # Exact deduplication may share deep face/point subgraphs between maps.
        # IfcOpenShell's deep removal does not reliably preserve every aggregate
        # inverse in that situation and can truncate still-live positive maps.
        # Keep the now-unreferenced source map; correctness is more important
        # than reclaiming this small amount of IFC metadata.

        emit(
            sink,
            stage="normalize-orientation",
            progress=index / max(total, 1),
            message=f"Проверено карт представлений: {index}/{total}",
            data={
                "map_id": representation_map_id,
                "mirrored_instances": len(negative_items),
            },
        )

    negative_after = sum(
        1
        for item in model.by_type("IfcMappedItem", include_subtypes=False)
        if _determinant(_mapped_matrix(item)) < -_EPSILON
    )
    if negative_after:
        raise RuntimeError(
            f"После нормализации осталось зеркальных IfcMappedItem: {negative_after}"
        )

    after = _entity_counts(model)
    entity_count_delta = {
        type_name: after[type_name] - before[type_name]
        for type_name in sorted(set(before) | set(after))
        if after[type_name] != before[type_name]
    }
    metrics = {
        "mode": "mapped-item-handedness-normalization",
        "maps_checked": len(groups),
        "mapped_items_checked": sum(len(items) for items in groups.values()),
        "negative_transforms_before": negative_before,
        "inward_positive_instances_before": inward_before,
        "repaired_items": repaired_items,
        "mirrored_maps_created": mirrored_maps_created,
        "oriented_maps_created": oriented_maps_created,
        "face_sets_reoriented": face_sets_reoriented,
        "faces_reoriented": faces_reoriented,
        "triangles_reoriented": triangles_reoriented,
        "boundary_edges_detected": boundary_edges_detected,
        "ambiguous_components": ambiguous_components,
        "negative_transforms_after": negative_after,
        "entity_count_delta": entity_count_delta,
        "elapsed_seconds": round(time.perf_counter() - started, 3),
    }
    emit(
        sink,
        event="orientation-normalized",
        stage="normalize-orientation-complete",
        progress=1.0,
        message=(
            f"Нормализовано зеркальных экземпляров: {negative_before}"
            if negative_before or inward_before
            else "Проблем ориентации экземпляров не обнаружено"
        ),
        data=metrics,
    )
    return OrientationRepairResult(
        metrics=metrics,
        entity_count_delta=entity_count_delta,
    )


def write_orientation_repaired_result(
    model: Any,
    output_path: Path,
    *,
    sink: EventSink = null_sink,
) -> None:
    emit(
        sink,
        stage="serialize-orientation",
        progress=0.0,
        message="Записывается IFC с нормализованной ориентацией",
    )
    model.write(str(output_path))
