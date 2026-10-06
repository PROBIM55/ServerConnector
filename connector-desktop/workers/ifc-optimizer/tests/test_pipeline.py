from __future__ import annotations

import json
from pathlib import Path

import ifcopenshell
import ifcopenshell.guid
import pytest
from structura_ifc_optimizer import pipeline
from structura_ifc_optimizer.analysis import analyze_ifc
from structura_ifc_optimizer.manifest import (
    build_semantic_manifest,
    compare_semantic_manifests,
)
from structura_ifc_optimizer.normalization import (
    build_compatibility_normalization_plan,
)
from structura_ifc_optimizer.pipeline import optimize_ifc


def create_fixture(path: Path, *, schema: str = "IFC4") -> None:
    model = ifcopenshell.file(schema=schema)
    owner_history = None
    if schema == "IFC2X3":
        person = model.create_entity("IfcPerson", FamilyName="Structura")
        organization = model.create_entity("IfcOrganization", Name="Structura")
        user = model.create_entity(
            "IfcPersonAndOrganization",
            ThePerson=person,
            TheOrganization=organization,
        )
        application = model.create_entity(
            "IfcApplication",
            ApplicationDeveloper=organization,
            Version="0.1",
            ApplicationFullName="Structura IFC Optimizer tests",
            ApplicationIdentifier="STRUCTURA_TEST",
        )
        owner_history = model.create_entity(
            "IfcOwnerHistory",
            OwningUser=user,
            OwningApplication=application,
            ChangeAction="ADDED",
            CreationDate=0,
        )
    root_args = {"OwnerHistory": owner_history} if owner_history else {}
    project = model.create_entity(
        "IfcProject",
        GlobalId=ifcopenshell.guid.new(),
        Name="Optimizer fixture",
        **root_args,
    )
    duplicate_a = model.create_entity("IfcCartesianPoint", Coordinates=(0.0, 0.0, 0.0))
    duplicate_b = model.create_entity("IfcCartesianPoint", Coordinates=(0.0, 0.0, 0.0))
    direction_a = model.create_entity("IfcDirection", DirectionRatios=(0.0, 0.0, 1.0))
    direction_b = model.create_entity("IfcDirection", DirectionRatios=(0.0, 0.0, 1.0))
    end_a = model.create_entity("IfcCartesianPoint", Coordinates=(1.0, 0.0, 0.0))
    end_b = model.create_entity("IfcCartesianPoint", Coordinates=(0.0, 1.0, 0.0))
    line_a = model.create_entity("IfcPolyline", Points=(duplicate_a, end_a))
    line_b = model.create_entity("IfcPolyline", Points=(duplicate_b, end_b))
    context = model.create_entity(
        "IfcGeometricRepresentationContext",
        ContextIdentifier="Model",
        ContextType="Model",
        CoordinateSpaceDimension=3,
        Precision=1e-5,
    )
    representation = model.create_entity(
        "IfcShapeRepresentation",
        ContextOfItems=context,
        RepresentationIdentifier="Body",
        RepresentationType="Curve2D",
        Items=(line_a, line_b),
    )
    shape = model.create_entity(
        "IfcProductDefinitionShape",
        Representations=(representation,),
    )
    proxy = model.create_entity(
        "IfcBuildingElementProxy",
        GlobalId=ifcopenshell.guid.new(),
        Name="Proxy",
        Representation=shape,
        **root_args,
    )
    code = model.create_entity(
        "IfcPropertySingleValue",
        Name="Code",
        NominalValue=model.create_entity("IfcLabel", "literal #123"),
    )
    pset = model.create_entity(
        "IfcPropertySet",
        GlobalId=ifcopenshell.guid.new(),
        Name="Metadata",
        HasProperties=(code,),
        **root_args,
    )
    model.create_entity(
        "IfcRelDefinesByProperties",
        GlobalId=ifcopenshell.guid.new(),
        RelatedObjects=(proxy,),
        RelatingPropertyDefinition=pset,
        **root_args,
    )
    assert project is not None
    assert direction_a is not direction_b
    model.write(str(path))


def test_analysis_reports_exact_point_duplicates(tmp_path: Path) -> None:
    source = tmp_path / "source.ifc"
    create_fixture(source)

    result = analyze_ifc(source)

    assert result["schema"] == "IFC4"
    assert result["duplicate_cartesian_points"] == 1
    assert result["watch_counts"]["IfcBuildingElementProxy"] == 1
    assert result["watch_counts"]["IfcProduct"] == 1
    assert result["watch_counts"]["IfcRoot"] == 4


def test_analysis_persists_job_artifacts(tmp_path: Path) -> None:
    source = tmp_path / "source.ifc"
    job_dir = tmp_path / "analysis-job"
    create_fixture(source)

    result = analyze_ifc(source, job_dir=job_dir)

    job = json.loads((job_dir / "job.json").read_text(encoding="utf-8"))
    saved_analysis = json.loads((job_dir / "analysis.json").read_text(encoding="utf-8"))
    assert job["status"] == "completed"
    assert job["operation"] == "analyze"
    assert saved_analysis["sha256"] == result["sha256"]
    assert (job_dir / "events.ndjson").read_text(encoding="utf-8").strip()


def test_semantic_manifest_is_deterministic_for_inline_values(tmp_path: Path) -> None:
    source = tmp_path / "source.ifc"
    create_fixture(source)
    model = ifcopenshell.open(str(source))

    first = build_semantic_manifest(model)
    second = build_semantic_manifest(model)

    assert compare_semantic_manifests(first, second)["ok"] is True
    assert first["digest"] == second["digest"]


def test_pipeline_deduplicates_and_validates(tmp_path: Path) -> None:
    source = tmp_path / "source.ifc"
    output = tmp_path / "optimized.ifc"
    create_fixture(source)

    source_property_line = next(
        line
        for line in source.read_bytes().splitlines(keepends=True)
        if b"IFCPROPERTYSINGLEVALUE" in line
    )
    report = optimize_ifc(source, output)

    assert output.exists()
    assert report["status"] == "completed"
    assert report["optimization"]["removed_points"] == 1
    assert report["optimization"]["removed_by_type"]["IfcDirection"] == 1
    assert report["optimization"]["removed_entities"] >= 2
    assert len(report["optimization"]["passes"]) == 2
    assert report["optimization"]["passes"][-1]["removed"] == 0
    assert "IfcPropertySingleValue" not in report["optimization"]["removed_by_type"]
    assert report["validation"]["ok"] is True
    assert report["input"]["unchanged"] is True
    optimized = ifcopenshell.open(str(output))
    assert len(optimized.by_type("IfcCartesianPoint")) == 3
    assert len(optimized.by_type("IfcDirection")) == 1
    assert optimized.by_type("IfcPropertySingleValue")[0].NominalValue.wrappedValue == (
        "literal #123"
    )
    output_property_line = next(
        line
        for line in output.read_bytes().splitlines(keepends=True)
        if b"IFCPROPERTYSINGLEVALUE" in line
    )
    assert output_property_line == source_property_line


def test_pipeline_persists_completed_job_artifacts(tmp_path: Path) -> None:
    source = tmp_path / "source.ifc"
    output = tmp_path / "optimized.ifc"
    job_dir = tmp_path / "optimization-job"
    create_fixture(source)

    report = optimize_ifc(source, output, job_dir=job_dir)

    job = json.loads((job_dir / "job.json").read_text(encoding="utf-8"))
    saved_report = json.loads((job_dir / "report.json").read_text(encoding="utf-8"))
    assert job["status"] == "completed"
    assert job["operation"] == "optimize"
    assert job["output_sha256"] == report["output"]["sha256"]
    assert saved_report["validation"]["ok"] is True
    assert (job_dir / "source-manifest.json").exists()
    assert (job_dir / "normalization.json").exists()
    assert (job_dir / "output-manifest.json").exists()


def test_pipeline_supports_ifc2x3_without_schema_migration(tmp_path: Path) -> None:
    source = tmp_path / "source-ifc2x3.ifc"
    output = tmp_path / "optimized-ifc2x3.ifc"
    create_fixture(source, schema="IFC2X3")

    report = optimize_ifc(source, output)

    assert report["input"]["schema"] == "IFC2X3"
    assert ifcopenshell.open(str(output)).schema == "IFC2X3"
    assert report["validation"]["semantic"]["schema_equal"] is True


def test_pipeline_removes_only_provably_empty_relationships(tmp_path: Path) -> None:
    source = tmp_path / "empty-spatial-relation.ifc"
    output = tmp_path / "optimized.ifc"
    job_dir = tmp_path / "normalization-job"
    create_fixture(source)
    model = ifcopenshell.open(str(source))
    storey = model.create_entity(
        "IfcBuildingStorey",
        GlobalId=ifcopenshell.guid.new(),
        Name="Empty storey",
    )
    proxy = model.by_type("IfcBuildingElementProxy")[0]
    empty_relation_guid = ifcopenshell.guid.new()
    empty_relation = model.create_entity(
        "IfcRelContainedInSpatialStructure",
        GlobalId=empty_relation_guid,
        RelatedElements=(proxy,),
        RelatingStructure=storey,
    )
    model.write(str(source))
    valid_record_tail = b",(#%d),#%d);" % (proxy.id(), storey.id())
    invalid_record_tail = b",$,#%d);" % storey.id()
    payload = source.read_bytes()
    assert valid_record_tail in payload
    source.write_bytes(payload.replace(valid_record_tail, invalid_record_tail, 1))
    assert (
        ifcopenshell.open(str(source)).by_id(empty_relation.id()).RelatedElements
        is None
    )

    report = optimize_ifc(source, output, job_dir=job_dir)
    optimized = ifcopenshell.open(str(output))

    assert report["optimization"]["normalization"]["removed_relationships"] == 1
    assert report["optimization"]["normalization"]["removed_by_type"] == {
        "IfcRelContainedInSpatialStructure": 1
    }
    assert report["validation"]["speckle_import_compatibility"] == {
        "ok": True,
        "empty_relationships": [],
    }
    assert all(
        getattr(item, "GlobalId", None) != empty_relation_guid
        for item in optimized.by_type("IfcRelationship")
    )
    assert len(optimized.by_type("IfcBuildingStorey")) == 1
    raw_manifest = json.loads(
        (job_dir / "source-manifest.json").read_text(encoding="utf-8")
    )
    normalized_manifest = json.loads(
        (job_dir / "normalized-source-manifest.json").read_text(encoding="utf-8")
    )
    assert raw_manifest["root_count"] == normalized_manifest["root_count"] + 1


def test_normalization_rejects_empty_relationship_with_direct_reference() -> None:
    class FakeRelation:
        GlobalId = "unsafe-empty-relation"
        RelatedElements = None

        @staticmethod
        def id() -> int:
            return 75

        @staticmethod
        def is_a() -> str:
            return "IfcRelContainedInSpatialStructure"

    relation = FakeRelation()

    class FakeModel:
        @staticmethod
        def by_type(_type_name: str) -> list[FakeRelation]:
            return [relation]

        @staticmethod
        def by_id(_step_id: int) -> FakeRelation:
            return relation

        @staticmethod
        def get_inverse(_relation: FakeRelation) -> tuple[object]:
            return (object(),)

    with pytest.raises(RuntimeError, match="Cannot safely remove"):
        build_compatibility_normalization_plan(FakeModel())


def test_pipeline_never_overwrites_source(tmp_path: Path) -> None:
    source = tmp_path / "source.ifc"
    create_fixture(source)

    source_hash = source.read_bytes()
    try:
        optimize_ifc(source, source)
    except ValueError as exc:
        assert "cannot be overwritten" in str(exc)
    else:
        raise AssertionError("Expected source overwrite guard")
    assert source.read_bytes() == source_hash


def test_failed_validation_never_publishes_output(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    source = tmp_path / "source.ifc"
    output = tmp_path / "optimized.ifc"
    create_fixture(source)

    monkeypatch.setattr(
        pipeline,
        "validate_exact_optimization",
        lambda **_kwargs: {"ok": False},
    )

    with pytest.raises(RuntimeError, match="did not pass"):
        optimize_ifc(source, output)

    assert not output.exists()
    assert list(tmp_path.glob(".optimized.*.tmp.ifc")) == []


def test_failed_validation_marks_job_failed(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    source = tmp_path / "source.ifc"
    output = tmp_path / "optimized.ifc"
    job_dir = tmp_path / "failed-job"
    create_fixture(source)

    monkeypatch.setattr(
        pipeline,
        "validate_exact_optimization",
        lambda **_kwargs: {"ok": False, "output_manifest": {}},
    )

    with pytest.raises(RuntimeError, match="did not pass"):
        optimize_ifc(source, output, job_dir=job_dir)

    job = json.loads((job_dir / "job.json").read_text(encoding="utf-8"))
    assert job["status"] == "failed"
    assert job["error"]["type"] == "RuntimeError"
    assert not output.exists()


def test_invalid_ifc_is_rejected(tmp_path: Path) -> None:
    source = tmp_path / "invalid.ifc"
    source.write_text("not an IFC", encoding="utf-8")

    with pytest.raises(ValueError, match="Invalid IFC STEP header"):
        analyze_ifc(source)
