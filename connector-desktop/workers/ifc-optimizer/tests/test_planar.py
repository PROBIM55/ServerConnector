from __future__ import annotations

from collections import Counter
from pathlib import Path

import ifcopenshell
import ifcopenshell.guid
import pytest
from structura_ifc_optimizer import extrusion
from structura_ifc_optimizer.events import EngineEvent
from structura_ifc_optimizer.extrusion import build_extrusion_plan
from structura_ifc_optimizer.pipeline import optimize_ifc
from structura_ifc_optimizer.planar import (
    build_planar_merge_plan,
    write_planar_merge_result,
)


def _face(model: ifcopenshell.file, points: tuple[object, ...]) -> object:
    loop = model.create_entity("IfcPolyLoop", Polygon=points)
    bound = model.create_entity(
        "IfcFaceOuterBound",
        Bound=loop,
        Orientation=True,
    )
    return model.create_entity("IfcFace", Bounds=(bound,))


def create_split_cube(
    path: Path,
    *,
    style_top_face: bool = False,
    schema: str = "IFC4",
) -> None:
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
        Name="Planar fixture",
        **root_args,
    )
    coordinates = (
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0),
        (1.0, 1.0, 0.0),
        (0.0, 1.0, 0.0),
        (0.0, 0.0, 1.0),
        (1.0, 0.0, 1.0),
        (1.0, 1.0, 1.0),
        (0.0, 1.0, 1.0),
    )
    points = tuple(
        model.create_entity("IfcCartesianPoint", Coordinates=coordinate)
        for coordinate in coordinates
    )
    top_left = _face(model, (points[4], points[5], points[6]))
    top_right = _face(model, (points[4], points[6], points[7]))
    faces = (
        _face(model, (points[0], points[3], points[2], points[1])),
        top_left,
        top_right,
        _face(model, (points[0], points[1], points[5], points[4])),
        _face(model, (points[1], points[2], points[6], points[5])),
        _face(model, (points[2], points[3], points[7], points[6])),
        _face(model, (points[3], points[0], points[4], points[7])),
    )
    shell = model.create_entity("IfcClosedShell", CfsFaces=faces)
    brep = model.create_entity("IfcFacetedBrep", Outer=shell)
    context = model.create_entity(
        "IfcGeometricRepresentationContext",
        ContextIdentifier="Model",
        ContextType="Model",
        CoordinateSpaceDimension=3,
        Precision=1e-5,
        WorldCoordinateSystem=model.create_entity(
            "IfcAxis2Placement3D",
            Location=model.create_entity(
                "IfcCartesianPoint",
                Coordinates=(0.0, 0.0, 0.0),
            ),
        ),
    )
    project.RepresentationContexts = (context,)
    project.UnitsInContext = model.create_entity(
        "IfcUnitAssignment",
        Units=(
            model.create_entity(
                "IfcSIUnit",
                UnitType="LENGTHUNIT",
                Prefix=None,
                Name="METRE",
            ),
        ),
    )
    representation = model.create_entity(
        "IfcShapeRepresentation",
        ContextOfItems=context,
        RepresentationIdentifier="Body",
        RepresentationType="Brep",
        Items=(brep,),
    )
    shape = model.create_entity(
        "IfcProductDefinitionShape",
        Representations=(representation,),
    )
    proxy = model.create_entity(
        "IfcBuildingElementProxy",
        GlobalId=ifcopenshell.guid.new(),
        Name="Split cube",
        Representation=shape,
        **root_args,
    )
    code = model.create_entity(
        "IfcPropertySingleValue",
        Name="Code",
        NominalValue=model.create_entity("IfcLabel", "keep literal #321"),
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
    if style_top_face:
        colour = model.create_entity(
            "IfcColourRgb",
            Name="Top",
            Red=1.0,
            Green=0.0,
            Blue=0.0,
        )
        shading = model.create_entity("IfcSurfaceStyleShading", SurfaceColour=colour)
        surface = model.create_entity(
            "IfcSurfaceStyle",
            Name="Top",
            Side="BOTH",
            Styles=(shading,),
        )
        assignment = model.create_entity(
            "IfcPresentationStyleAssignment",
            Styles=(surface,),
        )
        model.create_entity(
            "IfcStyledItem",
            Item=top_left,
            Styles=(assignment,),
            Name="Keep face style",
        )
    assert project is not None
    model.write(str(path))


def _shell_edge_counts(shell: object) -> Counter[tuple[int, int]]:
    edges: Counter[tuple[int, int]] = Counter()
    for face in shell.CfsFaces:
        for bound in face.Bounds:
            point_ids = [int(point.id()) for point in bound.Bound.Polygon]
            for index, left in enumerate(point_ids):
                right = point_ids[(index + 1) % len(point_ids)]
                edges[tuple(sorted((left, right)))] += 1
    return edges


def test_planar_merge_replaces_split_face_and_preserves_step_literals(
    tmp_path: Path,
) -> None:
    source = tmp_path / "split-cube.ifc"
    output = tmp_path / "compact.ifc"
    create_split_cube(source)
    source_property_line = next(
        line
        for line in source.read_bytes().splitlines(keepends=True)
        if b"IFCPROPERTYSINGLEVALUE" in line
    )

    model = ifcopenshell.open(str(source))
    plan = build_planar_merge_plan(model)
    write_planar_merge_result(source, output, plan)

    assert plan.metrics["shells_changed"] == 1
    assert plan.metrics["clusters_merged"] == 1
    assert plan.metrics["faces_merged"] == 1
    result = ifcopenshell.open(str(output))
    shell = result.by_type("IfcClosedShell")[0]
    assert len(shell.CfsFaces) == 6
    assert set(_shell_edge_counts(shell).values()) == {2}
    output_property_line = next(
        line
        for line in output.read_bytes().splitlines(keepends=True)
        if b"IFCPROPERTYSINGLEVALUE" in line
    )
    assert output_property_line == source_property_line


def test_planar_merge_skips_faces_with_direct_style_assignments(
    tmp_path: Path,
) -> None:
    source = tmp_path / "styled-split-cube.ifc"
    create_split_cube(source, style_top_face=True)

    model = ifcopenshell.open(str(source))
    plan = build_planar_merge_plan(model)

    assert plan.metrics["clusters_merged"] == 0
    assert plan.changed_records == {}
    assert plan.removed_ids == frozenset()


def test_compact_pipeline_keeps_brep_with_face_level_style(tmp_path: Path) -> None:
    source = tmp_path / "styled-split-cube.ifc"
    output = tmp_path / "styled-compact.ifc"
    create_split_cube(source, style_top_face=True)

    report = optimize_ifc(source, output, profile="compact")

    assert report["optimization"]["extrusion"]["extrusions_created"] == 0
    assert report["optimization"]["extrusion"]["skipped_presentation"] == 1
    assert report["validation"]["representation"]["ok"] is True
    assert report["validation"]["rendered_geometry"]["ok"] is True
    result = ifcopenshell.open(str(output))
    assert len(result.by_type("IfcFacetedBrep")) == 1
    assert len(result.by_type("IfcExtrudedAreaSolid")) == 0


def test_extrusion_recognizer_rejects_twisted_side_pairing(tmp_path: Path) -> None:
    source = tmp_path / "twisted-cube.ifc"
    create_split_cube(source)
    model = ifcopenshell.open(str(source))
    build_planar_merge_plan(model)
    shell = model.by_type("IfcClosedShell")[0]
    side = next(
        face
        for face in shell.CfsFaces
        if len(face.Bounds[0].Bound.Polygon) == 4
        and {float(point.Coordinates[2]) for point in face.Bounds[0].Bound.Polygon}
        == {0.0, 1.0}
    )
    points = model.by_type("IfcCartesianPoint")[:8]
    side_loop = side.Bounds[0].Bound
    side_loop.Polygon = (points[0], points[1], points[6], points[5])

    plan = build_extrusion_plan(model)

    assert plan.metrics["extrusions_created"] == 0
    assert plan.changed_records == {}


def test_extrusion_recognizer_rejects_non_manifold_duplicate_face(
    tmp_path: Path,
) -> None:
    source = tmp_path / "non-manifold-cube.ifc"
    create_split_cube(source)
    model = ifcopenshell.open(str(source))
    build_planar_merge_plan(model)
    shell = model.by_type("IfcClosedShell")[0]
    shell.CfsFaces = (*shell.CfsFaces, shell.CfsFaces[-1])

    plan = build_extrusion_plan(model)

    assert plan.metrics["extrusions_created"] == 0
    assert plan.changed_records == {}


def test_extrusion_geometry_mismatch_falls_back_without_appended_entities(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    source = tmp_path / "geometry-fallback.ifc"
    create_split_cube(source)
    model = ifcopenshell.open(str(source))
    build_planar_merge_plan(model)
    before_count = len(list(model))
    monkeypatch.setattr(extrusion, "_geometry_matches", lambda *_args: False)

    plan = build_extrusion_plan(model)

    assert plan.metrics["extrusions_created"] == 0
    assert plan.metrics["skipped_geometry"] == 1
    assert plan.appended_records == ()
    assert plan.changed_records == {}
    assert len(list(model)) == before_count


def test_extrusion_tolerance_uses_local_size_at_large_world_coordinates(
    tmp_path: Path,
) -> None:
    source = tmp_path / "far-from-origin.ifc"
    create_split_cube(source)
    model = ifcopenshell.open(str(source))
    for point in model.by_type("IfcCartesianPoint")[:8]:
        point.Coordinates = (
            float(point.Coordinates[0]) + 1_000_000.0,
            float(point.Coordinates[1]) + 2_000_000.0,
            float(point.Coordinates[2]) + 3_000_000.0,
        )
    build_planar_merge_plan(model)

    plan = build_extrusion_plan(model)

    assert plan.metrics["extrusions_created"] == 1
    assert plan.metrics["skipped_geometry"] == 0


def test_compact_pipeline_is_opt_in_and_validated(tmp_path: Path) -> None:
    source = tmp_path / "split-cube.ifc"
    output = tmp_path / "compact.ifc"
    create_split_cube(source)
    events: list[EngineEvent] = []

    report = optimize_ifc(
        source,
        output,
        profile="compact",
        sink=events.append,
    )

    assert report["optimization"]["profile"] == "compact"
    assert report["optimization"]["planar"]["clusters_merged"] == 1
    assert report["validation"]["ok"] is True
    assert report["validation"]["source_appearance"]["ok"] is True
    assert (
        report["validation"]["representation"][
            "geometry_representation_changes_allowed"
        ]
        is False
    )
    assert report["validation"]["rendered_geometry"]["ok"] is True
    assert report["validation"]["rendered_geometry"]["mismatch_count"] == 0
    assert report["input"]["unchanged"] is True
    prepared = next(event for event in events if event.stage == "job-prepared")
    assert len(prepared.data["temp_paths"]) == 6
    assert prepared.data["temp_path"] == prepared.data["final_temp_path"]
    validation_events = [
        event for event in events if event.stage == "validation-complete"
    ]
    assert validation_events
    assert all(
        "_source_representation_manifest" not in (event.data or {})
        for event in validation_events
    )
    extrusion_events = [
        event for event in events if event.stage == "recognize-extrusions"
    ]
    assert extrusion_events[-1].progress == 1.0
    assert report["optimization"]["post_planar_exact"] is not None
    assert report["optimization"]["extrusion"]["extrusions_created"] == 1
    assert report["optimization"]["mode"] == "compact-analytic-validated"
    result = ifcopenshell.open(str(output))
    assert len(result.by_type("IfcExtrudedAreaSolid")) == 1
    assert len(result.by_type("IfcFacetedBrep")) == 0


def test_compact_extrusion_preserves_ifc2x3_schema(tmp_path: Path) -> None:
    source = tmp_path / "split-cube-ifc2x3.ifc"
    output = tmp_path / "compact-ifc2x3.ifc"
    create_split_cube(source, schema="IFC2X3")

    report = optimize_ifc(source, output, profile="compact")

    assert report["input"]["schema"] == "IFC2X3"
    assert report["optimization"]["extrusion"]["extrusions_created"] == 1
    assert report["validation"]["ok"] is True
    assert report["validation"]["rendered_geometry"]["ok"] is True
    result = ifcopenshell.open(str(output))
    assert result.schema == "IFC2X3"
    assert len(result.by_type("IfcExtrudedAreaSolid")) == 1
