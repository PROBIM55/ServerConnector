from __future__ import annotations

import json
from pathlib import Path

import ifcopenshell
import ifcopenshell.util.placement
import numpy as np
import pytest
from structura_ifc_optimizer.geometry_validation import compare_rendered_geometry
from structura_ifc_optimizer.orientation import (
    UnsupportedMirroredMapError,
    repair_mapped_item_orientation,
)
from structura_ifc_optimizer.pipeline import optimize_ifc
from test_mapping_reuse import create_mapped_fixture


def _determinants(model: ifcopenshell.file) -> list[float]:
    return [
        float(
            np.linalg.det(
                ifcopenshell.util.placement.get_mappeditem_transformation(item)[:3, :3]
            )
        )
        for item in model.by_type("IfcMappedItem", include_subtypes=False)
    ]


def test_orientation_repair_preserves_world_geometry_and_styles(
    tmp_path: Path,
) -> None:
    source = tmp_path / "mirrored.ifc"
    output = tmp_path / "oriented.ifc"
    create_mapped_fixture(source, mirrored_second=True)
    model = ifcopenshell.open(str(source))

    assert sum(value < 0.0 for value in _determinants(model)) == 1
    repair = repair_mapped_item_orientation(model)
    model.write(str(output))

    assert repair.metrics["negative_transforms_before"] == 1
    assert repair.metrics["negative_transforms_after"] == 0
    assert repair.metrics["repaired_items"] == 1
    result = ifcopenshell.open(str(output))
    assert all(value > 0.0 for value in _determinants(result))
    assert len(result.by_type("IfcBuildingElementProxy")) == 2
    assert len(result.by_type("IfcStyledItem")) >= 2

    geometry = compare_rendered_geometry(
        source,
        output,
        workers=1,
        require_positive_output=True,
    )
    assert geometry["ok"] is True
    assert len(geometry["source_negative_shape_ids"]) == 1
    assert geometry["output_negative_shape_ids"] == []


def test_orientation_repair_reconciles_mixed_face_winding(
    tmp_path: Path,
) -> None:
    source = tmp_path / "mixed-face-winding.ifc"
    output = tmp_path / "mixed-face-winding.oriented.ifc"
    create_mapped_fixture(source, mirrored_second=True)
    malformed = ifcopenshell.open(str(source))
    first_face = malformed.by_type("IfcFacetedBrep")[0].Outer.CfsFaces[0]
    first_face.Bounds[0].Orientation = False
    malformed.write(str(source))

    model = ifcopenshell.open(str(source))
    repair = repair_mapped_item_orientation(model)
    model.write(str(output))

    assert repair.metrics["negative_transforms_before"] == 1
    assert repair.metrics["negative_transforms_after"] == 0
    assert repair.metrics["faces_reoriented"] > 0
    result = ifcopenshell.open(str(output))
    assert all(value > 0.0 for value in _determinants(result))

    geometry = compare_rendered_geometry(
        source,
        output,
        workers=1,
        require_positive_output=True,
    )
    assert geometry["ok"] is True
    assert geometry["mismatch_count"] == 0
    assert geometry["output_negative_shape_ids"] == []


def test_exact_pipeline_repairs_mirrored_instance_after_map_reuse(
    tmp_path: Path,
) -> None:
    source = tmp_path / "mirrored-pipeline.ifc"
    output = tmp_path / "mirrored-pipeline.optimized.ifc"
    create_mapped_fixture(source, mirrored_second=True)

    report = optimize_ifc(source, output)

    orientation = report["optimization"]["orientation"]
    assert orientation["negative_transforms_before"] == 1
    assert orientation["negative_transforms_after"] == 0
    assert orientation["mirrored_maps_created"] == 1
    assert report["validation"]["rendered_geometry"]["orientation_ok"] is True
    result = ifcopenshell.open(str(output))
    assert all(value > 0.0 for value in _determinants(result))
    assert len(result.by_type("IfcRepresentationMap")) == 2


def test_mirrored_map_with_multiple_items_fails_without_output(tmp_path: Path) -> None:
    source = tmp_path / "unsupported-mirrored-map.ifc"
    output = tmp_path / "unsupported-mirrored-map.optimized.ifc"
    job_dir = tmp_path / "unsupported-job"
    create_mapped_fixture(source, mirrored_second=True)
    model = ifcopenshell.open(str(source))
    mirrored_item = next(
        item
        for item in model.by_type("IfcMappedItem", include_subtypes=False)
        if np.linalg.det(
            ifcopenshell.util.placement.get_mappeditem_transformation(item)[:3, :3]
        )
        < 0.0
    )
    representation = mirrored_item.MappingSource.MappedRepresentation
    other_brep = next(
        brep
        for brep in model.by_type("IfcFacetedBrep")
        if brep not in representation.Items
    )
    representation.Items = (*representation.Items, other_brep)
    model.write(str(source))

    with pytest.raises(UnsupportedMirroredMapError) as captured:
        optimize_ifc(source, output, job_dir=job_dir)

    assert captured.value.code == "UNSUPPORTED_MIRRORED_MAP"
    assert captured.value.details["map_id"] > 0
    assert "ожидался один" in captured.value.details["reason"]
    assert "не создан" in str(captured.value)
    assert not output.exists()
    job = json.loads((job_dir / "job.json").read_text(encoding="utf-8"))
    assert job["status"] == "failed"
    assert job["error"]["code"] == "UNSUPPORTED_MIRRORED_MAP"
    assert job["error"]["details"] == captured.value.details
