from __future__ import annotations

import re
from collections import Counter
from pathlib import Path
from typing import Any

import ifcopenshell
import ifcopenshell.validate

from .events import EventSink, emit, null_sink
from .manifest import (
    build_representation_manifest,
    build_semantic_manifest,
    compare_representation_manifests,
    compare_semantic_manifests,
)
from .normalization import find_empty_relationships

STEP_ID_RE = re.compile(r"#\d+")


def entity_counts(model: Any) -> Counter[str]:
    return Counter(str(entity.is_a()) for entity in model)


def _issue_category(message: str) -> str:
    normalized = STEP_ID_RE.sub("#<id>", message)
    lines = [line.strip() for line in normalized.splitlines() if line.strip()]
    if not lines:
        return "Unknown IFC validation issue"
    if lines[0] == "With inverse:" and len(lines) > 1:
        return f"With inverse: {lines[1].split(':', 1)[0]}"
    return lines[0][:240]


def _validation_summary(model: Any) -> dict[str, Any]:
    logger = ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(model, logger, express_rules=False)
    categories = Counter(
        _issue_category(str(row.get("message", ""))) for row in logger.statements
    )
    levels = Counter(str(row.get("level", "unknown")) for row in logger.statements)
    return {
        "issue_count": len(logger.statements),
        "levels": dict(sorted(levels.items())),
        "categories": dict(sorted(categories.items())),
    }


def validate_exact_optimization(
    *,
    source_model: Any,
    source_manifest: dict[str, Any],
    output_path: Path,
    expected_removed_by_type: dict[str, int],
    sink: EventSink = null_sink,
    validate_ifc: bool = True,
) -> dict[str, Any]:
    expected_entity_count_delta = {
        type_name: -int(removed)
        for type_name, removed in expected_removed_by_type.items()
        if int(removed)
    }
    return validate_optimization(
        source_model=source_model,
        source_manifest=source_manifest,
        output_path=output_path,
        expected_entity_count_delta=expected_entity_count_delta,
        sink=sink,
        validate_ifc=validate_ifc,
    )


def validate_optimization(
    *,
    source_model: Any,
    source_manifest: dict[str, Any],
    output_path: Path,
    expected_entity_count_delta: dict[str, int],
    sink: EventSink = null_sink,
    source_representation_manifest: dict[str, Any] | None = None,
    source_ifc_validation: dict[str, Any] | None = None,
    allow_geometry_representation_changes: bool = False,
    validate_ifc: bool = True,
) -> dict[str, Any]:
    emit(sink, stage="validate-open", progress=0.0, message="Проверяется итоговый IFC")
    output_model = ifcopenshell.open(str(output_path))
    emit(
        sink,
        stage="validate-semantic",
        progress=0.0,
        message="Сравнивается семантический контракт",
    )
    output_manifest = build_semantic_manifest(output_model, sink=sink)
    semantic = compare_semantic_manifests(source_manifest, output_manifest)
    emit(
        sink,
        stage="validate-representation",
        progress=0.0,
        message="Сравниваются стили и преобразования представлений",
    )
    if source_representation_manifest is None:
        source_representation_manifest = build_representation_manifest(
            source_model,
            sink=sink,
        )
    output_representation_manifest = build_representation_manifest(
        output_model,
        sink=sink,
    )
    representation = compare_representation_manifests(
        source_representation_manifest,
        output_representation_manifest,
        allow_geometry_representation_changes=(allow_geometry_representation_changes),
    )

    before = entity_counts(source_model)
    after = entity_counts(output_model)
    changed_types: dict[str, dict[str, int]] = {}
    for type_name in sorted(set(before) | set(after)):
        if before[type_name] != after[type_name]:
            changed_types[type_name] = {
                "before": before[type_name],
                "after": after[type_name],
                "delta": after[type_name] - before[type_name],
            }

    expected_changes = {
        type_name: {
            "before": before[type_name],
            "after": before[type_name] + int(delta),
            "delta": int(delta),
        }
        for type_name, delta in sorted(expected_entity_count_delta.items())
        if int(delta)
    }
    counts_ok = changed_types == expected_changes

    if validate_ifc:
        emit(
            sink,
            stage="validate-ifc",
            progress=0.0,
            message="Проверяются ссылки и IFC-ограничения",
        )
        if source_ifc_validation is None:
            source_ifc_validation = _validation_summary(source_model)
        output_ifc_validation = _validation_summary(output_model)
        source_categories = source_ifc_validation["categories"]
        output_categories = output_ifc_validation["categories"]
        new_issue_categories = {
            category: count - int(source_categories.get(category, 0))
            for category, count in output_categories.items()
            if count > int(source_categories.get(category, 0))
        }
        ifc_validation_ok = not new_issue_categories
    else:
        source_ifc_validation = {
            "issue_count": 0,
            "levels": {},
            "categories": {},
            "skipped": True,
        }
        output_ifc_validation = dict(source_ifc_validation)
        new_issue_categories = {}
        ifc_validation_ok = True
    empty_relationships = find_empty_relationships(output_model)
    speckle_import_compatibility = {
        "ok": not empty_relationships,
        "empty_relationships": [
            {
                "step_id": item.step_id,
                "type": item.type_name,
                "global_id": item.global_id,
                "empty_attribute": item.empty_attribute,
            }
            for item in empty_relationships
        ],
    }

    result = {
        "ok": bool(
            semantic["ok"]
            and representation["ok"]
            and counts_ok
            and ifc_validation_ok
            and speckle_import_compatibility["ok"]
        ),
        "semantic": semantic,
        "representation": representation,
        "entity_counts_ok": counts_ok,
        "entity_count_changes": changed_types,
        "expected_entity_count_changes": expected_changes,
        "ifc_validation_ok": ifc_validation_ok,
        "ifc_validation_skipped": not validate_ifc,
        "source_ifc_validation": source_ifc_validation,
        "output_ifc_validation": output_ifc_validation,
        "new_ifc_issue_categories": new_issue_categories,
        "speckle_import_compatibility": speckle_import_compatibility,
        "output_manifest": output_manifest,
        "_source_representation_manifest": source_representation_manifest,
    }
    emit(
        sink,
        event="validation",
        stage="validation-complete",
        progress=1.0,
        message="Проверка завершена"
        if result["ok"]
        else "Проверка выявила расхождения",
        data={
            key: value
            for key, value in result.items()
            if key not in {"output_manifest", "_source_representation_manifest"}
        },
    )
    return result


def validate_exact_point_optimization(
    *,
    source_model: Any,
    source_manifest: dict[str, Any],
    output_path: Path,
    expected_removed_points: int,
    sink: EventSink = null_sink,
) -> dict[str, Any]:
    """Compatibility wrapper for the first exact-point contract."""
    return validate_exact_optimization(
        source_model=source_model,
        source_manifest=source_manifest,
        output_path=output_path,
        expected_removed_by_type={"IfcCartesianPoint": expected_removed_points},
        sink=sink,
    )
