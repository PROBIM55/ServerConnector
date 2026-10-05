from __future__ import annotations

import re
import time
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import ifcopenshell
import ifcopenshell.util.element

from .events import EventSink, emit, null_sink

ENTITY_LINE_RE = re.compile(rb"^\s*#(\d+)=")
EXACT_REUSE_FAMILIES = (
    "IfcGeometricRepresentationItem",
    "IfcTopologicalRepresentationItem",
    "IfcRepresentation",
    "IfcRepresentationMap",
    "IfcProductRepresentation",
    "IfcPresentationStyle",
    "IfcPresentationStyleAssignment",
)
EXACT_TYPE_ORDER = {
    "IfcSurfaceStyle": 10,
    "IfcPresentationStyleAssignment": 20,
    "IfcCartesianPoint": 30,
    "IfcDirection": 40,
    "IfcAxis1Placement": 50,
    "IfcAxis2Placement2D": 50,
    "IfcAxis2Placement3D": 50,
    "IfcCartesianTransformationOperator2D": 60,
    "IfcCartesianTransformationOperator2DnonUniform": 60,
    "IfcCartesianTransformationOperator3D": 60,
    "IfcCartesianTransformationOperator3DnonUniform": 60,
    "IfcPolyLoop": 70,
    "IfcFaceBound": 80,
    "IfcFaceOuterBound": 80,
    "IfcFace": 90,
    "IfcOpenShell": 100,
    "IfcClosedShell": 100,
    "IfcConnectedFaceSet": 100,
}


@dataclass(frozen=True)
class ExactReusePlan:
    replacements: dict[int, int]
    metrics: dict[str, Any]


@dataclass(frozen=True)
class _MappedRepresentationChain:
    representation_map: Any
    source_representation: Any
    geometry_item: Any
    styled_items: tuple[Any, ...]
    key: tuple[Any, ...]


def _canonical_id(step_id: int, replacements: dict[int, int]) -> int:
    current = step_id
    trail: list[int] = []
    while current in replacements:
        trail.append(current)
        current = replacements[current]
    for item in trail:
        replacements[item] = current
    return current


def _value_key(value: Any, replacements: dict[int, int] | None = None) -> Any:
    if hasattr(value, "is_a") and callable(value.is_a):
        step_id = int(value.id()) if value.id() else 0
        if step_id:
            if replacements is not None:
                step_id = _canonical_id(step_id, replacements)
            return ("ref", step_id)
        return (
            "typed",
            str(value.is_a()),
            tuple(_value_key(item, replacements) for item in value),
        )
    if isinstance(value, (tuple, list)):
        return tuple(_value_key(item, replacements) for item in value)
    if isinstance(value, float):
        return ("float", value.hex())
    return value


def _entity_key(
    entity: Any,
    replacements: dict[int, int] | None = None,
) -> tuple[Any, ...]:
    return (
        str(entity.is_a()),
        tuple(_value_key(value, replacements) for value in entity),
    )


def _deep_value_key(
    value: Any,
    replacements: dict[int, int],
    active: set[int] | None = None,
) -> Any:
    if hasattr(value, "is_a") and callable(value.is_a):
        step_id = int(value.id()) if value.id() else 0
        active_ids = active if active is not None else set()
        canonical_id = _canonical_id(step_id, replacements) if step_id else 0
        if canonical_id and canonical_id in active_ids:
            return ("cycle", str(value.is_a()))
        if canonical_id:
            active_ids.add(canonical_id)
        try:
            return (
                "entity",
                str(value.is_a()),
                tuple(
                    _deep_value_key(item, replacements, active_ids) for item in value
                ),
            )
        finally:
            if canonical_id:
                active_ids.discard(canonical_id)
    if isinstance(value, (tuple, list)):
        return tuple(_deep_value_key(item, replacements, active) for item in value)
    if isinstance(value, float):
        return ("float", value.hex())
    return value


def _styled_item_key(
    styled_item: Any,
    replacements: dict[int, int],
) -> tuple[Any, ...]:
    return (
        getattr(styled_item, "Name", None),
        _deep_value_key(
            getattr(styled_item, "Styles", ()) or (),
            replacements,
        ),
    )


def _mapped_representation_chain(
    model: Any,
    representation_map: Any,
    replacements: dict[int, int],
) -> _MappedRepresentationChain | None:
    source = representation_map.MappedRepresentation
    if not source.is_a("IfcShapeRepresentation") or len(source.Items) != 1:
        return None
    geometry_item = source.Items[0]
    if not geometry_item.is_a("IfcGeometricRepresentationItem"):
        return None
    if getattr(source, "LayerAssignments", None):
        return None
    if getattr(geometry_item, "LayerAssignments", None):
        return None

    map_inverses = set(model.get_inverse(representation_map))
    if not map_inverses or any(
        not inverse.is_a("IfcMappedItem") or inverse.MappingSource != representation_map
        for inverse in map_inverses
    ):
        return None
    if set(model.get_inverse(source)) != {representation_map}:
        return None

    styled_items = tuple(
        sorted(
            getattr(geometry_item, "StyledByItem", None) or (),
            key=lambda item: (
                repr(_styled_item_key(item, replacements)),
                int(item.id()),
            ),
        )
    )
    allowed_item_inverses = {source, *styled_items}
    if set(model.get_inverse(geometry_item)) != allowed_item_inverses:
        return None

    style_keys = tuple(_styled_item_key(item, replacements) for item in styled_items)
    key = (
        _value_key(representation_map.MappingOrigin, replacements),
        _value_key(source.ContextOfItems, replacements),
        source.RepresentationIdentifier,
        source.RepresentationType,
        _entity_key(geometry_item, replacements),
        style_keys,
    )
    return _MappedRepresentationChain(
        representation_map=representation_map,
        source_representation=source,
        geometry_item=geometry_item,
        styled_items=styled_items,
        key=key,
    )


def _add_style_aware_map_reuse(
    model: Any,
    replacements: dict[int, int],
) -> dict[str, Any]:
    canonical: dict[tuple[Any, ...], _MappedRepresentationChain] = {}
    removed_by_type: Counter[str] = Counter()
    chains_considered = 0
    chains_reused = 0

    for representation_map in sorted(
        model.by_type("IfcRepresentationMap", include_subtypes=False),
        key=lambda item: int(item.id()),
    ):
        chain = _mapped_representation_chain(
            model,
            representation_map,
            replacements,
        )
        if chain is None:
            continue
        chains_considered += 1
        existing = canonical.get(chain.key)
        if existing is None:
            canonical[chain.key] = chain
            continue
        if len(existing.styled_items) != len(chain.styled_items):
            continue

        pairs = (
            (chain.representation_map, existing.representation_map),
            (chain.source_representation, existing.source_representation),
            (chain.geometry_item, existing.geometry_item),
            *zip(chain.styled_items, existing.styled_items, strict=True),
        )
        for duplicate, replacement in pairs:
            duplicate_id = int(duplicate.id())
            replacement_id = _canonical_id(int(replacement.id()), replacements)
            replacements[duplicate_id] = replacement_id
            removed_by_type[str(duplicate.is_a())] += 1
        chains_reused += 1

    return {
        "chains_considered": chains_considered,
        "unique_chains": chains_considered - chains_reused,
        "chains_reused": chains_reused,
        "removed_entities": sum(removed_by_type.values()),
        "removed_by_type": dict(sorted(removed_by_type.items())),
    }


def _inverse_attribute_names(
    schema: Any,
    type_name: str,
    cache: dict[str, tuple[str, ...]],
) -> tuple[str, ...]:
    cached = cache.get(type_name)
    if cached is not None:
        return cached
    declaration = schema.declaration_by_name(type_name)
    names = tuple(
        attribute.name() for attribute in declaration.all_inverse_attributes()
    )
    cache[type_name] = names
    return names


def _has_inverse_state(
    entity: Any,
    schema: Any,
    cache: dict[str, tuple[str, ...]],
) -> bool:
    for name in _inverse_attribute_names(schema, str(entity.is_a()), cache):
        value = getattr(entity, name, None)
        if value is None:
            continue
        if isinstance(value, (tuple, list)):
            if value:
                return True
        else:
            return True
    return False


def _is_exact_reuse_candidate(
    entity: Any,
    schema: Any,
    inverse_cache: dict[str, tuple[str, ...]],
) -> bool:
    if not entity.id() or entity.is_a("IfcRoot"):
        return False
    if not any(entity.is_a(family) for family in EXACT_REUSE_FAMILIES):
        return False
    return not _has_inverse_state(entity, schema, inverse_cache)


def build_exact_reuse_plan(
    model: Any,
    *,
    sink: EventSink = null_sink,
    max_passes: int = 12,
) -> ExactReusePlan:
    started = time.perf_counter()
    replacements: dict[int, int] = {}
    removed_by_type: Counter[str] = Counter()
    pass_metrics: list[dict[str, Any]] = []
    schema = ifcopenshell.schema_by_name(str(model.schema))
    inverse_cache: dict[str, tuple[str, ...]] = {}

    type_stats: dict[str, dict[str, int]] = {}
    for entity in model:
        if not _is_exact_reuse_candidate(entity, schema, inverse_cache):
            continue
        type_name = str(entity.is_a())
        stats = type_stats.setdefault(
            type_name,
            {"count": 0, "first_id": int(entity.id())},
        )
        stats["count"] += 1
        stats["first_id"] = min(stats["first_id"], int(entity.id()))

    ordered_types = sorted(
        type_stats,
        key=lambda name: (
            EXACT_TYPE_ORDER.get(name, 500),
            type_stats[name]["first_id"],
            name,
        ),
    )
    candidate_total = sum(stats["count"] for stats in type_stats.values())

    for pass_index in range(1, max_passes + 1):
        pass_started = time.perf_counter()
        duplicate_total = 0

        for type_index, type_name in enumerate(ordered_types, start=1):
            entities = list(model.by_type(type_name, include_subtypes=False))
            canonical: dict[tuple[Any, ...], int] = {}
            type_removed = 0
            for entity in entities:
                if not _is_exact_reuse_candidate(entity, schema, inverse_cache):
                    continue
                entity_id = int(entity.id())
                if entity_id in replacements:
                    continue
                key = _entity_key(entity, replacements)
                existing = canonical.get(key)
                if existing is None:
                    canonical[key] = entity_id
                else:
                    replacements[entity_id] = existing
                    type_removed += 1
                    if type_removed % 25000 == 0:
                        emit(
                            sink,
                            stage="index-exact-entities",
                            progress=type_index / max(len(ordered_types), 1),
                            message=(
                                f"Точный индекс {type_name}: {type_removed:,} дублей"
                            ).replace(",", " "),
                            data={
                                "pass": pass_index,
                                "type": type_name,
                                "indexed_duplicates": type_removed,
                            },
                        )

            duplicate_total += type_removed
            removed_by_type[type_name] += type_removed

            emit(
                sink,
                stage="deduplicate-exact-entities",
                progress=type_index / max(len(ordered_types), 1),
                message=(
                    f"Точный проход {pass_index}: {type_name}, "
                    f"объединено {type_removed:,}"
                ).replace(",", " "),
                data={
                    "pass": pass_index,
                    "type": type_name,
                    "removed": type_removed,
                },
            )

        pass_metrics.append(
            {
                "pass": pass_index,
                "candidates": candidate_total,
                "removed": duplicate_total,
                "elapsed_seconds": round(time.perf_counter() - pass_started, 3),
            }
        )
        if duplicate_total == 0:
            break
        emit(
            sink,
            stage="exact-pass-complete",
            progress=pass_index / max_passes,
            message=(
                f"Точный проход {pass_index}: объединено {duplicate_total:,} сущностей"
            ).replace(",", " "),
            data=pass_metrics[-1],
        )

    for duplicate_id in tuple(replacements):
        replacements[duplicate_id] = _canonical_id(duplicate_id, replacements)

    representation_map_reuse = _add_style_aware_map_reuse(model, replacements)
    removed_by_type.update(representation_map_reuse["removed_by_type"])
    for duplicate_id in tuple(replacements):
        replacements[duplicate_id] = _canonical_id(duplicate_id, replacements)

    removed_total = sum(removed_by_type.values())
    removed_points = removed_by_type.get("IfcCartesianPoint", 0)
    input_points = len(model.by_type("IfcCartesianPoint"))
    metrics = {
        "mode": "exact-non-rooted",
        "removed_entities": removed_total,
        "removed_by_type": dict(sorted(removed_by_type.items())),
        "input_points": input_points,
        "unique_points": input_points - removed_points,
        "removed_points": removed_points,
        "passes": pass_metrics,
        "representation_map_reuse": representation_map_reuse,
        "elapsed_seconds": round(time.perf_counter() - started, 3),
    }
    return ExactReusePlan(replacements=replacements, metrics=metrics)


def _rewrite_step_references(
    line: bytes,
    replacements: dict[int, int],
) -> bytes:
    output = bytearray()
    index = 0
    in_string = False
    length = len(line)
    while index < length:
        char = line[index]
        if char == 0x27:
            output.append(char)
            if in_string and index + 1 < length and line[index + 1] == 0x27:
                output.append(0x27)
                index += 2
                continue
            in_string = not in_string
            index += 1
            continue
        if (
            not in_string
            and char == 0x23
            and index + 1 < length
            and 0x30 <= line[index + 1] <= 0x39
        ):
            end = index + 2
            while end < length and 0x30 <= line[end] <= 0x39:
                end += 1
            step_id = int(line[index + 1 : end].decode("ascii"))
            output.extend(f"#{_canonical_id(step_id, replacements)}".encode("ascii"))
            index = end
            continue
        output.append(char)
        index += 1
    return bytes(output)


def _record_ends(line: bytes, *, in_string: bool) -> tuple[bool, bool]:
    index = 0
    while index < len(line):
        char = line[index]
        if char == 0x27:
            if in_string and index + 1 < len(line) and line[index + 1] == 0x27:
                index += 2
                continue
            in_string = not in_string
        elif char == 0x3B and not in_string:
            return True, in_string
        index += 1
    return False, in_string


def write_exact_reuse_result(
    source_path: Path,
    output_path: Path,
    plan: ExactReusePlan,
    *,
    sink: EventSink = null_sink,
) -> None:
    emit(
        sink,
        stage="serialize-exact",
        progress=0.0,
        message="Сериализуется точный STEP-граф",
    )
    total_bytes = source_path.stat().st_size
    processed_bytes = 0
    skipped_record = False
    record_in_string = False
    with source_path.open("rb") as source, output_path.open("wb") as handle:
        for index, line in enumerate(source, start=1):
            processed_bytes += len(line)
            if skipped_record:
                ended, record_in_string = _record_ends(
                    line,
                    in_string=record_in_string,
                )
                if ended:
                    skipped_record = False
                    record_in_string = False
                continue

            match = ENTITY_LINE_RE.match(line)
            if match and int(match.group(1)) in plan.replacements:
                ended, record_in_string = _record_ends(line, in_string=False)
                skipped_record = not ended
                if ended:
                    record_in_string = False
                continue

            handle.write(_rewrite_step_references(line, plan.replacements))
            if index % 250000 == 0:
                emit(
                    sink,
                    stage="serialize-exact",
                    progress=processed_bytes / max(total_bytes, 1),
                    message=f"Обработано STEP-строк: {index:,}".replace(",", " "),
                )
        handle.flush()


def deduplicate_cartesian_points(
    model: Any,
    *,
    sink: EventSink = null_sink,
) -> dict[str, Any]:
    """Compatibility wrapper for callers that expect the original API."""
    points = list(model.by_type("IfcCartesianPoint"))
    canonical: dict[tuple[Any, ...], Any] = {}
    duplicates: list[tuple[Any, Any]] = []
    for index, point in enumerate(points, start=1):
        key = _entity_key(point)
        existing = canonical.get(key)
        if existing is None:
            canonical[key] = point
        else:
            duplicates.append((point, existing))
        if index % 50000 == 0:
            emit(
                sink,
                stage="index-points",
                progress=index / max(len(points), 1),
                message=f"Проиндексировано точек: {index:,}".replace(",", " "),
            )

    started = time.perf_counter()
    for index, (duplicate, replacement) in enumerate(duplicates, start=1):
        for inverse in list(model.get_inverse(duplicate)):
            ifcopenshell.util.element.replace_attribute(
                inverse,
                duplicate,
                replacement,
            )
        model.remove(duplicate)
        if index % 10000 == 0 or index == len(duplicates):
            emit(
                sink,
                stage="deduplicate-points",
                progress=index / max(len(duplicates), 1),
                message=f"Объединено точек: {index:,}".replace(",", " "),
            )

    return {
        "input_points": len(points),
        "unique_points": len(canonical),
        "removed_points": len(duplicates),
        "elapsed_seconds": round(time.perf_counter() - started, 3),
    }
