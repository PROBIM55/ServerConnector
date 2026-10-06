from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .events import EventSink, emit, null_sink
from .optimize import ENTITY_LINE_RE, _record_ends

RELATION_TARGET_ATTRIBUTES = (
    "RelatedElements",
    "RelatedObjects",
    "RelatedProducts",
)


@dataclass(frozen=True)
class EmptyRelationship:
    step_id: int
    type_name: str
    global_id: str | None
    empty_attribute: str


@dataclass(frozen=True)
class CompatibilityNormalizationPlan:
    removed_ids: frozenset[int]
    relationships: tuple[EmptyRelationship, ...]
    metrics: dict[str, Any]


def find_empty_relationships(model: Any) -> tuple[EmptyRelationship, ...]:
    relationships: list[EmptyRelationship] = []
    for relation in model.by_type("IfcRelationship"):
        for attribute in RELATION_TARGET_ATTRIBUTES:
            if not hasattr(relation, attribute):
                continue
            targets = getattr(relation, attribute)
            if targets is not None and len(targets) > 0:
                continue
            relationships.append(
                EmptyRelationship(
                    step_id=int(relation.id()),
                    type_name=str(relation.is_a()),
                    global_id=(
                        str(relation.GlobalId)
                        if getattr(relation, "GlobalId", None)
                        else None
                    ),
                    empty_attribute=attribute,
                )
            )
            break
    return tuple(relationships)


def build_compatibility_normalization_plan(
    model: Any,
) -> CompatibilityNormalizationPlan:
    relationships = find_empty_relationships(model)
    for relationship in relationships:
        relation = model.by_id(relationship.step_id)
        inverse_count = len(model.get_inverse(relation))
        if inverse_count:
            raise RuntimeError(
                "Cannot safely remove empty IFC relationship "
                f"#{relationship.step_id} ({relationship.type_name}): "
                f"it has {inverse_count} direct inverse reference(s)"
            )

    removed_by_type = Counter(item.type_name for item in relationships)
    metrics = {
        "mode": "speckle-compatible-empty-relationship-removal",
        "removed_relationships": len(relationships),
        "removed_by_type": dict(sorted(removed_by_type.items())),
        "relationships": [
            {
                "step_id": item.step_id,
                "type": item.type_name,
                "global_id": item.global_id,
                "empty_attribute": item.empty_attribute,
            }
            for item in relationships
        ],
    }
    return CompatibilityNormalizationPlan(
        removed_ids=frozenset(item.step_id for item in relationships),
        relationships=relationships,
        metrics=metrics,
    )


def write_compatibility_normalized_result(
    source_path: Path,
    output_path: Path,
    plan: CompatibilityNormalizationPlan,
    *,
    sink: EventSink = null_sink,
) -> None:
    emit(
        sink,
        stage="normalize-compatibility",
        progress=0.0,
        message="Удаляются пустые IFC-связи, несовместимые с импортёрами",
        data=plan.metrics,
    )
    total_bytes = source_path.stat().st_size
    processed_bytes = 0
    skip_record = False
    record_in_string = False

    with source_path.open("rb") as source, output_path.open("wb") as output:
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

            match = ENTITY_LINE_RE.match(line)
            if match and int(match.group(1)) in plan.removed_ids:
                ended, record_in_string = _record_ends(line, in_string=False)
                skip_record = not ended
                if ended:
                    record_in_string = False
                continue

            output.write(line)
            if index % 250000 == 0:
                emit(
                    sink,
                    stage="normalize-compatibility",
                    progress=processed_bytes / max(total_bytes, 1),
                    message=f"Проверено STEP-строк: {index:,}".replace(",", " "),
                )
        output.flush()

    emit(
        sink,
        stage="normalize-compatibility",
        progress=1.0,
        message=(
            f"Удалено пустых IFC-связей: {len(plan.removed_ids)}"
            if plan.removed_ids
            else "Пустых IFC-связей не найдено"
        ),
        data=plan.metrics,
    )
