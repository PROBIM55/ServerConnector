from __future__ import annotations

from pathlib import Path

import ifcopenshell
import ifcopenshell.guid
from structura_ifc_optimizer.manifest import (
    build_representation_manifest,
    compare_representation_manifests,
)
from structura_ifc_optimizer.pipeline import optimize_ifc


def _face(model: ifcopenshell.file, points: tuple[object, ...]) -> object:
    loop = model.create_entity("IfcPolyLoop", Polygon=points)
    bound = model.create_entity(
        "IfcFaceOuterBound",
        Bound=loop,
        Orientation=True,
    )
    return model.create_entity("IfcFace", Bounds=(bound,))


def _style(
    model: ifcopenshell.file,
    colour: tuple[float, float, float],
) -> object:
    rgb = model.create_entity(
        "IfcColourRgb",
        Name=None,
        Red=colour[0],
        Green=colour[1],
        Blue=colour[2],
    )
    shading = model.create_entity(
        "IfcSurfaceStyleShading",
        SurfaceColour=rgb,
    )
    surface = model.create_entity(
        "IfcSurfaceStyle",
        Name="Body",
        Side="BOTH",
        Styles=(shading,),
    )
    return model.create_entity(
        "IfcPresentationStyleAssignment",
        Styles=(surface,),
    )


def create_mapped_fixture(
    path: Path,
    *,
    different_colours: bool = False,
    mirrored_second: bool = False,
) -> None:
    model = ifcopenshell.file(schema="IFC4")
    model.create_entity(
        "IfcProject",
        GlobalId=ifcopenshell.guid.new(),
        Name="Mapped fixture",
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
    faces = (
        _face(model, (points[0], points[3], points[2], points[1])),
        _face(model, (points[4], points[5], points[6], points[7])),
        _face(model, (points[0], points[1], points[5], points[4])),
        _face(model, (points[1], points[2], points[6], points[5])),
        _face(model, (points[2], points[3], points[7], points[6])),
        _face(model, (points[3], points[0], points[4], points[7])),
    )
    shell = model.create_entity("IfcClosedShell", CfsFaces=faces)
    context = model.create_entity(
        "IfcGeometricRepresentationContext",
        ContextIdentifier="Model",
        ContextType="Model",
        CoordinateSpaceDimension=3,
        Precision=1e-5,
    )
    map_origin = model.create_entity(
        "IfcAxis2Placement3D",
        Location=points[0],
    )
    shared_style = _style(model, (0.4, 0.5, 0.6))

    for index in range(2):
        brep = model.create_entity("IfcFacetedBrep", Outer=shell)
        assignment = (
            _style(model, (0.7, 0.2, 0.1))
            if different_colours and index == 1
            else shared_style
        )
        model.create_entity(
            "IfcStyledItem",
            Item=brep,
            Styles=(assignment,),
            Name=None,
        )
        source = model.create_entity(
            "IfcShapeRepresentation",
            ContextOfItems=context,
            RepresentationIdentifier="Body",
            RepresentationType="Brep",
            Items=(brep,),
        )
        representation_map = model.create_entity(
            "IfcRepresentationMap",
            MappingOrigin=map_origin,
            MappedRepresentation=source,
        )
        local_origin = model.create_entity(
            "IfcCartesianPoint",
            Coordinates=(float(index) * 2.0, 0.0, 0.0),
        )
        target = model.create_entity(
            "IfcCartesianTransformationOperator3D",
            Axis2=(
                model.create_entity(
                    "IfcDirection",
                    DirectionRatios=(0.0, -1.0, 0.0),
                )
                if mirrored_second and index == 1
                else None
            ),
            LocalOrigin=local_origin,
            Scale=1.0,
        )
        mapped_item = model.create_entity(
            "IfcMappedItem",
            MappingSource=representation_map,
            MappingTarget=target,
        )
        representation = model.create_entity(
            "IfcShapeRepresentation",
            ContextOfItems=context,
            RepresentationIdentifier="Body",
            RepresentationType="MappedRepresentation",
            Items=(mapped_item,),
        )
        shape = model.create_entity(
            "IfcProductDefinitionShape",
            Representations=(representation,),
        )
        model.create_entity(
            "IfcBuildingElementProxy",
            GlobalId=ifcopenshell.guid.new(),
            Name=f"Instance {index + 1}",
            Representation=shape,
        )
    model.write(str(path))


def test_exact_pipeline_reuses_complete_style_aware_mapping_chain(
    tmp_path: Path,
) -> None:
    source = tmp_path / "mapped.ifc"
    output = tmp_path / "mapped-exact.ifc"
    create_mapped_fixture(source)

    report = optimize_ifc(source, output)

    reuse = report["optimization"]["representation_map_reuse"]
    assert reuse["chains_considered"] == 2
    assert reuse["unique_chains"] == 1
    assert reuse["chains_reused"] == 1
    assert reuse["removed_by_type"] == {
        "IfcFacetedBrep": 1,
        "IfcRepresentationMap": 1,
        "IfcShapeRepresentation": 1,
        "IfcStyledItem": 1,
    }
    assert report["validation"]["representation"]["ok"] is True
    result = ifcopenshell.open(str(output))
    assert len(result.by_type("IfcRepresentationMap")) == 1
    assert len(result.by_type("IfcMappedItem")) == 2
    assert len(result.by_type("IfcBuildingElementProxy")) == 2


def test_exact_pipeline_keeps_same_geometry_with_different_colours(
    tmp_path: Path,
) -> None:
    source = tmp_path / "different-colours.ifc"
    output = tmp_path / "different-colours-exact.ifc"
    create_mapped_fixture(source, different_colours=True)

    report = optimize_ifc(source, output)

    reuse = report["optimization"]["representation_map_reuse"]
    assert reuse["chains_considered"] == 2
    assert reuse["chains_reused"] == 0
    assert report["validation"]["representation"]["ok"] is True
    result = ifcopenshell.open(str(output))
    assert len(result.by_type("IfcRepresentationMap")) == 2
    assert len(result.by_type("IfcFacetedBrep")) == 2


def test_compact_pipeline_preserves_mapped_style_and_instances(
    tmp_path: Path,
) -> None:
    source = tmp_path / "mapped-compact.ifc"
    output = tmp_path / "mapped-compact-result.ifc"
    create_mapped_fixture(source)

    report = optimize_ifc(source, output, profile="compact")

    assert report["optimization"]["extrusion"]["extrusions_created"] == 1
    assert report["validation"]["source_appearance"]["ok"] is True
    assert report["validation"]["representation"]["ok"] is True
    assert report["validation"]["rendered_geometry"]["ok"] is True
    result = ifcopenshell.open(str(output))
    assert len(result.by_type("IfcRepresentationMap")) == 1
    assert len(result.by_type("IfcMappedItem")) == 2
    assert len(result.by_type("IfcExtrudedAreaSolid")) == 1
    styled_item = result.by_type("IfcStyledItem")[0]
    assert styled_item.Item.is_a("IfcExtrudedAreaSolid")
    assert len(result.by_type("IfcColourRgb")) == 1


def test_representation_manifest_detects_colour_changes(tmp_path: Path) -> None:
    source = tmp_path / "appearance.ifc"
    create_mapped_fixture(source)
    model = ifcopenshell.open(str(source))
    before = build_representation_manifest(model)

    model.by_type("IfcColourRgb")[0].Red = 0.9
    after = build_representation_manifest(model)

    comparison = compare_representation_manifests(before, after)
    assert comparison["ok"] is False
    assert len(comparison["changed_products"]) == 2
    appearance_comparison = compare_representation_manifests(
        before,
        after,
        allow_geometry_representation_changes=True,
    )
    assert appearance_comparison["ok"] is False


def test_appearance_contract_allows_only_geometry_representation_type_change(
    tmp_path: Path,
) -> None:
    source = tmp_path / "representation-type.ifc"
    create_mapped_fixture(source)
    model = ifcopenshell.open(str(source))
    before = build_representation_manifest(model)

    source_representation = next(
        representation
        for representation in model.by_type("IfcShapeRepresentation")
        if representation.RepresentationType == "Brep"
    )
    source_representation.RepresentationType = "SweptSolid"
    after = build_representation_manifest(model)

    assert compare_representation_manifests(before, after)["ok"] is False
    appearance_comparison = compare_representation_manifests(
        before,
        after,
        allow_geometry_representation_changes=True,
    )
    assert appearance_comparison["ok"] is True
