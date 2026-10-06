from __future__ import annotations

import gc
import os
import time
import uuid
from pathlib import Path
from typing import Any

import ifcopenshell

from .events import EventSink, emit, null_sink
from .extrusion import build_extrusion_plan, write_extrusion_result
from .geometry_validation import compare_rendered_geometry
from .io_utils import (
    bytes_label,
    require_distinct_output,
    require_ifc_file,
    sha256_file,
    write_json_atomic,
)
from .jobs import JobArtifacts
from .manifest import (
    build_representation_manifest,
    build_semantic_manifest,
    compare_representation_manifests,
)
from .normalization import (
    build_compatibility_normalization_plan,
    write_compatibility_normalized_result,
)
from .optimize import build_exact_reuse_plan, write_exact_reuse_result
from .orientation import (
    repair_mapped_item_orientation,
    write_orientation_repaired_result,
)
from .planar import build_planar_merge_plan, write_planar_merge_result
from .validation import validate_exact_optimization, validate_optimization

OPTIMIZATION_PROFILES = {"exact", "compact"}


def optimize_ifc(
    input_value: str | Path,
    output_value: str | Path,
    *,
    report_path: str | Path | None = None,
    sink: EventSink = null_sink,
    job_dir: str | Path | None = None,
    profile: str = "exact",
) -> dict[str, Any]:
    started = time.perf_counter()
    if profile not in OPTIMIZATION_PROFILES:
        raise ValueError(
            f"Unsupported optimization profile: {profile}. "
            f"Expected one of: {', '.join(sorted(OPTIMIZATION_PROFILES))}"
        )
    input_path = require_ifc_file(input_value)
    output_path = require_distinct_output(input_path, output_value)
    artifacts = (
        JobArtifacts(
            job_dir,
            operation="optimize",
            input_path=input_path,
            output_path=output_path,
        )
        if job_dir is not None
        else None
    )
    effective_sink = artifacts.sink(sink) if artifacts else sink
    job_token = uuid.uuid4().hex
    exact_temp_path = output_path.with_name(
        f".{output_path.stem}.{job_token}.exact.tmp.ifc"
    )
    planar_temp_path = output_path.with_name(
        f".{output_path.stem}.{job_token}.planar.tmp.ifc"
    )
    analytic_temp_path = output_path.with_name(
        f".{output_path.stem}.{job_token}.analytic.tmp.ifc"
    )
    compact_temp_path = output_path.with_name(
        f".{output_path.stem}.{job_token}.compact.tmp.ifc"
    )
    normalized_temp_path = output_path.with_name(
        f".{output_path.stem}.{job_token}.normalized.tmp.ifc"
    )
    oriented_temp_path = output_path.with_name(
        f".{output_path.stem}.{job_token}.oriented.tmp.ifc"
    )
    final_temp_path = oriented_temp_path if profile == "exact" else compact_temp_path
    try:
        emit(
            effective_sink,
            stage="job-prepared",
            progress=0.0,
            message="Подготовлена изолированная рабочая копия",
            data={
                "profile": profile,
                "temp_path": str(final_temp_path),
                "temp_paths": [
                    str(normalized_temp_path),
                    str(exact_temp_path),
                    str(oriented_temp_path),
                    *(
                        [
                            str(planar_temp_path),
                            str(analytic_temp_path),
                            str(compact_temp_path),
                        ]
                        if profile == "compact"
                        else []
                    ),
                ],
                "exact_temp_path": str(exact_temp_path),
                "oriented_temp_path": str(oriented_temp_path),
                "final_temp_path": str(final_temp_path),
            },
        )
        emit(
            effective_sink,
            stage="hash",
            progress=0.0,
            message="Вычисляется контрольная сумма исходного IFC",
        )
        input_sha256 = sha256_file(input_path)
        emit(
            effective_sink,
            stage="source-open",
            progress=0.0,
            message="Открывается неизменяемая исходная модель",
        )
        source_model = ifcopenshell.open(str(input_path))
        source_schema = str(source_model.schema)
        normalization_plan = build_compatibility_normalization_plan(source_model)
        normalization = normalization_plan.metrics
        optimization_source_path = input_path
        if artifacts and normalization_plan.removed_ids:
            raw_source_manifest = build_semantic_manifest(
                source_model,
                sink=effective_sink,
            )
            artifacts.write("source-manifest.json", raw_source_manifest)
            del raw_source_manifest
        if normalization_plan.removed_ids:
            write_compatibility_normalized_result(
                input_path,
                normalized_temp_path,
                normalization_plan,
                sink=effective_sink,
            )
            del source_model
            source_model = ifcopenshell.open(str(normalized_temp_path))
            optimization_source_path = normalized_temp_path
        del normalization_plan
        source_manifest = build_semantic_manifest(source_model, sink=effective_sink)
        if artifacts:
            artifacts.write("normalization.json", normalization)
            artifacts.write(
                (
                    "normalized-source-manifest.json"
                    if normalization["removed_relationships"]
                    else "source-manifest.json"
                ),
                source_manifest,
            )
    except Exception as exc:
        normalized_temp_path.unlink(missing_ok=True)
        if artifacts:
            artifacts.finish(
                "failed",
                error={"type": type(exc).__name__, "message": str(exc)},
            )
        raise

    try:
        reuse_plan = build_exact_reuse_plan(source_model, sink=effective_sink)
        exact_optimization = reuse_plan.metrics
        emit(
            effective_sink,
            stage="write-temp",
            progress=0.0,
            message="Записывается временный IFC",
        )
        write_exact_reuse_result(
            optimization_source_path,
            exact_temp_path,
            reuse_plan,
            sink=effective_sink,
        )
        del reuse_plan

        exact_validation = validate_exact_optimization(
            # The reuse planner and binary writer never mutate the parsed source model.
            source_model=source_model,
            source_manifest=source_manifest,
            output_path=exact_temp_path,
            expected_removed_by_type=exact_optimization["removed_by_type"],
            sink=effective_sink,
            validate_ifc=False,
        )
        if not exact_validation["ok"]:
            raise RuntimeError(
                "Optimized IFC did not pass the semantic and entity-count contract"
            )

        emit(
            effective_sink,
            stage="orientation-open",
            progress=0.0,
            message="Проверяется ориентация экземпляров",
        )
        orientation_model = ifcopenshell.open(str(exact_temp_path))
        orientation_result = repair_mapped_item_orientation(
            orientation_model,
            sink=effective_sink,
        )
        orientation_optimization = orientation_result.metrics
        orientation_expected_delta = dict(orientation_result.entity_count_delta)
        write_orientation_repaired_result(
            orientation_model,
            oriented_temp_path,
            sink=effective_sink,
        )
        del orientation_result
        del orientation_model

        expected_oriented_delta = {
            type_name: -int(removed)
            for type_name, removed in exact_optimization["removed_by_type"].items()
            if int(removed)
        }
        for type_name, delta in orientation_expected_delta.items():
            expected_oriented_delta[type_name] = expected_oriented_delta.get(
                type_name, 0
            ) + int(delta)
            if expected_oriented_delta[type_name] == 0:
                del expected_oriented_delta[type_name]

        validation = validate_optimization(
            source_model=source_model,
            source_manifest=source_manifest,
            output_path=oriented_temp_path,
            expected_entity_count_delta=expected_oriented_delta,
            sink=effective_sink,
            allow_geometry_representation_changes=True,
        )
        if profile == "exact":
            # IFC parsing is the dominant memory cost for large SolidWorks/T-FLEX
            # exports. Release the semantic-validation model before opening the
            # source and result again for rendered-geometry comparison.
            exact_validation.pop("output_manifest", None)
            exact_validation.pop("_source_representation_manifest", None)
            del source_model
            del source_manifest
            gc.collect()
            rendered_geometry = (
                compare_rendered_geometry(
                    optimization_source_path,
                    oriented_temp_path,
                    sink=effective_sink,
                    require_positive_output=True,
                )
                if int(orientation_optimization["repaired_items"])
                else {
                    "ok": True,
                    "skipped": True,
                    "reason": "orientation-unchanged",
                    "orientation_ok": True,
                    "output_negative_shape_ids": [],
                }
            )
        else:
            # Compact performs further geometry rewrites. Validating this
            # intermediate file would double tessellation time and peak memory;
            # the final compact output is checked below instead.
            rendered_geometry = {
                "ok": True,
                "skipped": True,
                "reason": "deferred-to-compact-output",
                "orientation_ok": True,
                "output_negative_shape_ids": [],
            }
        validation["rendered_geometry"] = rendered_geometry
        validation["ok"] = bool(validation["ok"] and rendered_geometry["ok"])
        if not validation["ok"]:
            raise RuntimeError(
                "IFC с исправленной ориентацией не прошел геометрический контракт"
            )

        planar_optimization: dict[str, Any] | None = None
        extrusion_optimization: dict[str, Any] | None = None
        post_planar_exact_optimization: dict[str, Any] | None = None
        if profile == "compact":
            source_ifc_validation = validation["source_ifc_validation"]
            emit(
                effective_sink,
                stage="compact-open",
                progress=0.0,
                message="Открывается точный результат для compact-прохода",
            )
            compact_model = ifcopenshell.open(str(oriented_temp_path))
            oriented_representation_manifest = build_representation_manifest(
                compact_model,
                sink=effective_sink,
            )
            planar_plan = build_planar_merge_plan(
                compact_model,
                sink=effective_sink,
            )
            del compact_model
            planar_optimization = planar_plan.metrics
            planar_expected_delta = dict(planar_plan.expected_entity_count_delta)
            write_planar_merge_result(
                oriented_temp_path,
                planar_temp_path,
                planar_plan,
                sink=effective_sink,
            )
            del planar_plan
            emit(
                effective_sink,
                stage="analytic-open",
                progress=0.0,
                message="Распознаются строгие линейные выдавливания",
            )
            planar_model = ifcopenshell.open(str(planar_temp_path))
            extrusion_plan = build_extrusion_plan(
                planar_model,
                sink=effective_sink,
            )
            del planar_model
            extrusion_optimization = extrusion_plan.metrics
            extrusion_expected_delta = dict(extrusion_plan.expected_entity_count_delta)
            write_extrusion_result(
                planar_temp_path,
                analytic_temp_path,
                extrusion_plan,
                sink=effective_sink,
            )
            del extrusion_plan
            emit(
                effective_sink,
                stage="post-planar-exact",
                progress=0.0,
                message="Повторно объединяются точные сущности compact-геометрии",
            )
            analytic_model = ifcopenshell.open(str(analytic_temp_path))
            analytic_representation_manifest = build_representation_manifest(
                analytic_model,
                sink=effective_sink,
            )
            source_appearance = compare_representation_manifests(
                oriented_representation_manifest,
                analytic_representation_manifest,
                allow_geometry_representation_changes=True,
            )
            if not source_appearance["ok"]:
                raise RuntimeError(
                    "Analytic IFC did not preserve styles, layers, and mapping "
                    "transforms"
                )
            post_planar_exact = build_exact_reuse_plan(
                analytic_model,
                sink=effective_sink,
            )
            del analytic_model
            post_planar_exact_optimization = post_planar_exact.metrics
            write_exact_reuse_result(
                analytic_temp_path,
                compact_temp_path,
                post_planar_exact,
                sink=effective_sink,
            )
            del post_planar_exact
            expected_final_delta = {
                type_name: -int(removed)
                for type_name, removed in exact_optimization["removed_by_type"].items()
                if int(removed)
            }
            for type_name, delta in orientation_expected_delta.items():
                expected_final_delta[type_name] = expected_final_delta.get(
                    type_name, 0
                ) + int(delta)
                if expected_final_delta[type_name] == 0:
                    del expected_final_delta[type_name]
            for type_name, delta in planar_expected_delta.items():
                expected_final_delta[type_name] = expected_final_delta.get(
                    type_name, 0
                ) + int(delta)
                if expected_final_delta[type_name] == 0:
                    del expected_final_delta[type_name]
            for type_name, delta in extrusion_expected_delta.items():
                expected_final_delta[type_name] = expected_final_delta.get(
                    type_name, 0
                ) + int(delta)
                if expected_final_delta[type_name] == 0:
                    del expected_final_delta[type_name]
            for (
                type_name,
                removed,
            ) in post_planar_exact_optimization["removed_by_type"].items():
                expected_final_delta[type_name] = expected_final_delta.get(
                    type_name,
                    0,
                ) - int(removed)
                if expected_final_delta[type_name] == 0:
                    del expected_final_delta[type_name]
            validation = validate_optimization(
                source_model=source_model,
                source_manifest=source_manifest,
                output_path=compact_temp_path,
                expected_entity_count_delta=expected_final_delta,
                sink=effective_sink,
                source_representation_manifest=analytic_representation_manifest,
                source_ifc_validation=source_ifc_validation,
            )
            validation["source_appearance"] = source_appearance
            del analytic_representation_manifest
            exact_validation.pop("output_manifest", None)
            exact_validation.pop("_source_representation_manifest", None)
            del source_model
            del source_manifest
            gc.collect()
            rendered_geometry = compare_rendered_geometry(
                optimization_source_path,
                compact_temp_path,
                sink=effective_sink,
                require_positive_output=True,
            )
            validation["rendered_geometry"] = rendered_geometry
            validation["ok"] = bool(validation["ok"] and rendered_geometry["ok"])
            if not validation["ok"]:
                raise RuntimeError(
                    "Compact IFC did not pass semantic, geometry, and entity-count "
                    "contracts"
                )

        if artifacts and "output_manifest" in validation:
            artifacts.write("output-manifest.json", validation["output_manifest"])

        gc.collect()
        source_sha256_after = sha256_file(input_path)
        if source_sha256_after != input_sha256:
            raise RuntimeError("Source IFC changed during optimization")

        optimization_elapsed = round(time.perf_counter() - started, 3)
        combined_delta = {
            type_name: -int(removed)
            for type_name, removed in exact_optimization["removed_by_type"].items()
            if int(removed)
        }
        for type_name, delta in orientation_expected_delta.items():
            combined_delta[type_name] = combined_delta.get(type_name, 0) + int(delta)
        for type_name, removed in normalization["removed_by_type"].items():
            combined_delta[type_name] = combined_delta.get(type_name, 0) - int(removed)
        if profile == "compact":
            for type_name, delta in planar_expected_delta.items():
                combined_delta[type_name] = combined_delta.get(type_name, 0) + int(
                    delta
                )
            for type_name, delta in extrusion_expected_delta.items():
                combined_delta[type_name] = combined_delta.get(type_name, 0) + int(
                    delta
                )
            for (
                type_name,
                removed,
            ) in post_planar_exact_optimization["removed_by_type"].items():
                combined_delta[type_name] = combined_delta.get(type_name, 0) - int(
                    removed
                )
        combined_delta = {
            type_name: delta
            for type_name, delta in combined_delta.items()
            if int(delta)
        }
        combined_removed_by_type = {
            type_name: -int(delta)
            for type_name, delta in combined_delta.items()
            if int(delta) < 0
        }
        combined_added_by_type = {
            type_name: int(delta)
            for type_name, delta in combined_delta.items()
            if int(delta) > 0
        }
        removed_points = int(combined_removed_by_type.get("IfcCartesianPoint", 0))
        optimization = {
            **exact_optimization,
            "mode": (
                "compact-analytic-validated"
                if profile == "compact"
                else exact_optimization["mode"]
            ),
            "profile": profile,
            "normalization": normalization,
            "exact": exact_optimization,
            "orientation": orientation_optimization,
            "planar": planar_optimization,
            "extrusion": extrusion_optimization,
            "post_planar_exact": post_planar_exact_optimization,
            "removed_by_type": dict(sorted(combined_removed_by_type.items())),
            "added_by_type": dict(sorted(combined_added_by_type.items())),
            "removed_entities": sum(combined_removed_by_type.values())
            - sum(combined_added_by_type.values()),
            "removed_points": removed_points,
            "unique_points": int(exact_optimization["input_points"]) - removed_points,
            "elapsed_seconds": optimization_elapsed,
        }

        os.replace(final_temp_path, output_path)
        normalized_temp_path.unlink(missing_ok=True)
        oriented_temp_path.unlink(missing_ok=True)
        exact_temp_path.unlink(missing_ok=True)
        if profile == "compact":
            planar_temp_path.unlink(missing_ok=True)
            analytic_temp_path.unlink(missing_ok=True)
        output_size = output_path.stat().st_size
        report = {
            "version": 1,
            "status": "completed",
            "input": {
                "path": str(input_path),
                "size_bytes": input_path.stat().st_size,
                "sha256": input_sha256,
                "sha256_after": source_sha256_after,
                "unchanged": True,
                "schema": source_schema,
            },
            "output": {
                "path": str(output_path),
                "size_bytes": output_size,
                "size_label": bytes_label(output_size),
                "sha256": sha256_file(output_path),
            },
            "optimization": optimization,
            "validation": {
                key: value
                for key, value in validation.items()
                if key not in {"output_manifest", "_source_representation_manifest"}
            },
            "saved_bytes": input_path.stat().st_size - output_size,
            "saved_percent": round(
                (input_path.stat().st_size - output_size)
                * 100.0
                / input_path.stat().st_size,
                3,
            ),
            "elapsed_seconds": round(time.perf_counter() - started, 3),
        }
        destination_report = (
            Path(report_path).expanduser().resolve()
            if report_path
            else output_path.with_suffix(".report.json")
        )
        write_json_atomic(destination_report, report)
        report["report_path"] = str(destination_report)
        if artifacts:
            artifacts.write("report.json", report)
            artifacts.finish(
                "completed",
                input_sha256=input_sha256,
                output_sha256=report["output"]["sha256"],
                report_path=str(destination_report),
                saved_percent=report["saved_percent"],
            )
        emit(
            effective_sink,
            event="completed",
            stage="complete",
            progress=1.0,
            message="IFC оптимизирован и проверен",
            data=report,
        )
        return report
    except Exception as exc:
        normalized_temp_path.unlink(missing_ok=True)
        exact_temp_path.unlink(missing_ok=True)
        oriented_temp_path.unlink(missing_ok=True)
        planar_temp_path.unlink(missing_ok=True)
        analytic_temp_path.unlink(missing_ok=True)
        compact_temp_path.unlink(missing_ok=True)
        if artifacts:
            error: dict[str, Any] = {
                "type": type(exc).__name__,
                "message": str(exc),
            }
            code = getattr(exc, "code", None)
            details = getattr(exc, "details", None)
            if code is not None:
                error["code"] = str(code)
            if isinstance(details, dict):
                error["details"] = details
            artifacts.finish(
                "failed",
                error=error,
            )
        raise
