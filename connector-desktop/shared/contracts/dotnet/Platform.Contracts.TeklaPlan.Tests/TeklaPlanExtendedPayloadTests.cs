using System.Text.Json;
using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaPlanExtendedPayloadTests
{
    [Fact]
    public void PolyBeam_payload_parses_an_ordered_path_bend_and_developed_plate()
    {
        var command = Command("create-poly-beam", new Dictionary<string, JsonElement>
        {
            ["path"] = Path(
                new[]
                {
                    Node("n1", 0, 0, 0),
                    Node("n2", 1000, 0, 0),
                    Node("n3", 1000, 1000, 0),
                },
                new[] { Line("s1", "n1", "n2"), Line("s2", "n2", "n3") }),
            ["profile"] = JsonSerializer.SerializeToElement(new { kind = "plate", thicknessMm = 20d, widthMm = 300d }),
            ["material"] = Material(),
            ["bends"] = JsonSerializer.SerializeToElement(new[] { new { pathNodeId = "n2", radiusMm = 250d, neutralAxisFactor = 0.5d } }),
            ["developedPlate"] = DevelopedPlate(20d, 300d, -15d),
        });

        var result = TeklaPlanPayloads.ParseCreatePolyBeam(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal(3, result.Value!.Path.Nodes.Count);
        Assert.Equal(2, result.Value.Path.Segments.Count);
        Assert.Single(result.Value.Bends);
        Assert.Equal("n2", result.Value.Bends[0].PathNodeId);
        Assert.Equal(-15, result.Value.DevelopedPlate!.TransverseOffsetMm);
        Assert.Equal(1, result.Value.DevelopedPlate.StationFrame.Version);
        Assert.Equal(new[] { "n1", "n2", "n3" }, result.Value.DevelopedPlate.StationFrame.Stations.Select(item => item.SpineNodeId));
    }

    [Fact]
    public void PolyBeam_payload_rejects_disconnected_or_reversed_segment_order()
    {
        var command = PolyBeamCommand(
            new[] { Node("n1", 0, 0, 0), Node("n2", 1000, 0, 0), Node("n3", 2000, 0, 0) },
            new[] { Line("s1", "n1", "n2"), Line("s2", "n3", "n2") });

        var result = TeklaPlanPayloads.ParseCreatePolyBeam(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_PATH_TOPOLOGY_INVALID");
    }

    [Fact]
    public void PolyBeam_payload_rejects_endpoint_bends_and_profile_mismatch()
    {
        var command = PolyBeamCommand(
            new[] { Node("n1", 0, 0, 0), Node("n2", 1000, 0, 0) },
            new[] { Line("s1", "n1", "n2") });
        command.Payload["bends"] = JsonSerializer.SerializeToElement(new[] { new { pathNodeId = "n1", radiusMm = 100d } });
        command.Payload["developedPlate"] = DevelopedPlate(25d, 300d, 0d);

        var result = TeklaPlanPayloads.ParseCreatePolyBeam(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_BEND_NODE_INVALID");
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_DEVELOPED_PLATE_PROFILE_MISMATCH");
    }

    [Fact]
    public void PolyBeam_payload_rejects_a_signed_station_frame_flip()
    {
        var command = PolyBeamCommand(
            new[] { Node("n1", 0, 0, 0), Node("n2", 1000, 0, 0), Node("n3", 1000, 1000, 0) },
            new[] { Line("s1", "n1", "n2"), Line("s2", "n2", "n3") });
        command.Payload["developedPlate"] = DevelopedPlate(20d, 300d, 0d, flipMiddle: true);

        var result = TeklaPlanPayloads.ParseCreatePolyBeam(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_DEVELOPED_STATION_FRAME_TRANSPORT_MISMATCH");
    }

    [Fact]
    public void PolyBeam_payload_rejects_duplicate_station_identities()
    {
        var command = PolyBeamCommand(
            new[] { Node("n1", 0, 0, 0), Node("n2", 1000, 0, 0), Node("n3", 1000, 1000, 0) },
            new[] { Line("s1", "n1", "n2"), Line("s2", "n2", "n3") });
        command.Payload["developedPlate"] = DevelopedPlate(20d, 300d, 0d, duplicateMiddleId: true);

        var result = TeklaPlanPayloads.ParseCreatePolyBeam(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_DEVELOPED_STATION_FRAME_ID_DUPLICATE");
    }

    [Fact]
    public void PolyBeam_payload_rejects_a_collinear_arc()
    {
        var command = PolyBeamCommand(
            new[] { Node("n1", 0, 0, 0), Node("n2", 1000, 0, 0) },
            new[]
            {
                JsonSerializer.SerializeToElement(new
                {
                    id = "s1",
                    kind = "arc",
                    startNodeId = "n1",
                    endNodeId = "n2",
                    pointOnArc = new[] { 500d, 0d, 0d },
                }),
            });

        var result = TeklaPlanPayloads.ParseCreatePolyBeam(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_PATH_ARC_INVALID");
    }

    [Fact]
    public void ContourPlate_payload_parses_a_right_handed_plane_and_closed_contour()
    {
        var command = ContourPlateCommand(Plane(), RectangleEdges());

        var result = TeklaPlanPayloads.ParseCreateContourPlate(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal(4, result.Value!.Contour.Vertices.Count);
        Assert.Equal(4, result.Value.Contour.Edges.Count);
        Assert.Equal("symmetric", result.Value.ExtrusionSide);
        Assert.Equal(20, result.Value.ThicknessMm);
    }

    [Fact]
    public void ContourPlate_payload_rejects_a_left_handed_plane()
    {
        var plane = JsonSerializer.SerializeToElement(new
        {
            origin = new[] { 0d, 0d, 0d },
            axisX = new[] { 1d, 0d, 0d },
            axisY = new[] { 0d, 1d, 0d },
            axisZ = new[] { 0d, 0d, -1d },
        });
        var command = ContourPlateCommand(plane, RectangleEdges());

        var result = TeklaPlanPayloads.ParseCreateContourPlate(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_PLANE_INVALID");
    }

    [Fact]
    public void ContourPlate_payload_rejects_an_open_or_reordered_contour()
    {
        var edges = RectangleEdges();
        var reordered = new[]
        {
            edges[0], edges[2], edges[1], edges[3],
        };
        var command = ContourPlateCommand(Plane(), reordered);

        var result = TeklaPlanPayloads.ParseCreateContourPlate(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_CONTOUR_TOPOLOGY_INVALID");
    }

    [Fact]
    public void ContourPlate_payload_rejects_a_self_intersecting_contour()
    {
        var command = ContourPlateCommand(
            Plane(),
            new[]
            {
                JsonSerializer.SerializeToElement(new { id = "e1", kind = "line", startVertexId = "v1", endVertexId = "v3" }),
                JsonSerializer.SerializeToElement(new { id = "e2", kind = "line", startVertexId = "v3", endVertexId = "v2" }),
                JsonSerializer.SerializeToElement(new { id = "e3", kind = "line", startVertexId = "v2", endVertexId = "v4" }),
                JsonSerializer.SerializeToElement(new { id = "e4", kind = "line", startVertexId = "v4", endVertexId = "v1" }),
            });

        var result = TeklaPlanPayloads.ParseCreateContourPlate(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_CONTOUR_SELF_INTERSECTION");
    }

    [Fact]
    public void ContourPlate_payload_rejects_distinct_vertices_at_the_same_point()
    {
        var command = ContourPlateCommand(Plane(), RectangleEdges());
        command.Payload["contour"] = JsonSerializer.SerializeToElement(new
        {
            id = "contour-1",
            vertices = new[]
            {
                new { id = "v1", point = new[] { 0d, 0d } },
                new { id = "v2", point = new[] { 1000d, 0d } },
                new { id = "v3", point = new[] { 0d, 0d } },
                new { id = "v4", point = new[] { 1000d, 500d } },
                new { id = "v5", point = new[] { 0d, 500d } },
            },
            edges = new[]
            {
                new { id = "e1", kind = "line", startVertexId = "v1", endVertexId = "v2" },
                new { id = "e2", kind = "line", startVertexId = "v2", endVertexId = "v3" },
                new { id = "e3", kind = "line", startVertexId = "v3", endVertexId = "v4" },
                new { id = "e4", kind = "line", startVertexId = "v4", endVertexId = "v5" },
                new { id = "e5", kind = "line", startVertexId = "v5", endVertexId = "v1" },
            },
        });

        var result = TeklaPlanPayloads.ParseCreateContourPlate(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_CONTOUR_VERTEX_POSITION_DUPLICATE");
    }

    [Fact]
    public void Generic_payload_validation_dispatches_to_extended_commands()
    {
        var polyBeam = PolyBeamCommand(
            new[] { Node("n1", 0, 0, 0), Node("n2", 0, 0, 0) },
            new[] { Line("s1", "n1", "n2") });
        var contourPlate = ContourPlateCommand(Plane(), RectangleEdges());
        contourPlate.Payload["thicknessMm"] = JsonSerializer.SerializeToElement(-1d);

        var polyBeamDiagnostics = TeklaPlanPayloads.Validate(polyBeam);
        var contourPlateDiagnostics = TeklaPlanPayloads.Validate(contourPlate);

        Assert.Contains(polyBeamDiagnostics, item => item.Code == "TEKLA_PLAN_PATH_SEGMENT_ZERO_LENGTH");
        Assert.Contains(contourPlateDiagnostics, item => item.Code == "TEKLA_PLAN_PLATE_THICKNESS_INVALID");
    }

    [Fact]
    public void Contour_corner_payload_preserves_the_exact_semantic_vertex_target()
    {
        var command = ContourCornerCommand();

        var result = TeklaPlanPayloads.ParseApplyContourCorner(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("plate-1", result.Value!.Target.ElementId);
        Assert.Equal("outer-corner", result.Value.Target.Role);
        Assert.Equal("contour-vertex", result.Value.TargetTopology.Kind);
        Assert.Equal("outer", result.Value.TargetTopology.ContourId);
        Assert.Equal("outer-v2", result.Value.TargetTopology.VertexId);
        Assert.Equal("chamfer", result.Value.CornerType);
        Assert.Equal(40, result.Value.SizeXmm);
        Assert.Equal(25, result.Value.SizeYmm);
    }

    [Fact]
    public void Contour_corner_payload_accepts_native_cove()
    {
        var command = ContourCornerCommand("contour-vertex", "cove", 60d);

        var result = TeklaPlanPayloads.ParseApplyContourCorner(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("cove", result.Value!.CornerType);
        Assert.Equal(60, result.Value.SizeXmm);
    }

    [Theory]
    [InlineData("contour-edge", "chamfer", 40d, "TEKLA_PLAN_CONTOUR_CORNER_TARGET_INVALID")]
    [InlineData("contour-vertex", "bevel", 40d, "TEKLA_PLAN_CONTOUR_CORNER_TYPE_INVALID")]
    [InlineData("contour-vertex", "round", 0d, "TEKLA_PLAN_CONTOUR_CORNER_SIZE_X_INVALID")]
    public void Contour_corner_payload_rejects_ambiguous_or_invalid_native_input(
        string targetKind,
        string cornerType,
        double sizeXmm,
        string expectedCode)
    {
        var command = ContourCornerCommand(targetKind, cornerType, sizeXmm);

        var result = TeklaPlanPayloads.ParseApplyContourCorner(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Generic_payload_validation_dispatches_to_contour_corner()
    {
        var command = ContourCornerCommand();
        command.Payload["sizeYmm"] = JsonSerializer.SerializeToElement(-1d);

        var diagnostics = TeklaPlanPayloads.Validate(command);

        Assert.Contains(diagnostics, item => item.Code == "TEKLA_PLAN_CONTOUR_CORNER_SIZE_Y_INVALID");
    }

    [Fact]
    public void Edge_treatment_payload_preserves_the_exact_semantic_edge_target()
    {
        var command = EdgeTreatmentCommand();

        var result = TeklaPlanPayloads.ParseApplyEdgeTreatment(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("plate-1", result.Value!.Target.ElementId);
        Assert.Equal("outer-edge", result.Value.Target.Role);
        Assert.Equal("contour-edge", result.Value.TargetTopology.Kind);
        Assert.Equal("outer", result.Value.TargetTopology.ContourId);
        Assert.Equal("outer-e1", result.Value.TargetTopology.EdgeId);
        Assert.Equal("positive", result.Value.TargetTopology.Side);
        Assert.Equal("chamfer", result.Value.TreatmentType);
        Assert.Equal(12, result.Value.SizeMm);
        Assert.Equal(8, result.Value.SecondarySizeMm);
    }

    [Theory]
    [InlineData("contour-vertex", "chamfer", 12d, "TEKLA_PLAN_EDGE_TREATMENT_TARGET_INVALID")]
    [InlineData("contour-edge", "cope", 12d, "TEKLA_PLAN_EDGE_TREATMENT_TYPE_INVALID")]
    [InlineData("contour-edge", "round", 12d, "TEKLA_PLAN_EDGE_TREATMENT_TYPE_INVALID")]
    [InlineData("contour-edge", "round", 0d, "TEKLA_PLAN_EDGE_TREATMENT_SIZE_INVALID")]
    public void Edge_treatment_payload_rejects_ambiguous_or_invalid_native_input(
        string targetKind,
        string treatmentType,
        double sizeMm,
        string expectedCode)
    {
        var command = EdgeTreatmentCommand(targetKind, treatmentType, sizeMm);

        var result = TeklaPlanPayloads.ParseApplyEdgeTreatment(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Generic_payload_validation_dispatches_to_edge_treatment()
    {
        var command = EdgeTreatmentCommand();
        command.Payload["secondarySizeMm"] = JsonSerializer.SerializeToElement(-1d);

        var diagnostics = TeklaPlanPayloads.Validate(command);

        Assert.Contains(diagnostics, item => item.Code == "TEKLA_PLAN_EDGE_TREATMENT_SECONDARY_SIZE_INVALID");
    }

    [Fact]
    public void Fitting_payload_parses_stable_topology_target_plane_and_keep_side()
    {
        var command = Command("apply-fitting", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new
            {
                elementId = "beam-1",
                role = "end-face",
                path = new[] { "end", "outer" },
            }),
            ["plane"] = Plane(),
            ["keepSide"] = JsonSerializer.SerializeToElement("negative"),
        });

        var result = TeklaPlanPayloads.ParseApplyFitting(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("beam-1", result.Value!.Target.ElementId);
        Assert.Equal("end-face", result.Value.Target.Role);
        Assert.Equal(new[] { "end", "outer" }, result.Value.Target.Path);
        Assert.Equal("negative", result.Value.KeepSide);
        Assert.Equal(1, result.Value.Plane.AxisX.X);
    }

    [Theory]
    [InlineData("", "end-face", "positive", "TEKLA_PLAN_TOPOLOGY_REF_INVALID")]
    [InlineData("beam-1", "", "positive", "TEKLA_PLAN_TOPOLOGY_REF_INVALID")]
    [InlineData("beam-1", "end-face", "both", "TEKLA_PLAN_FITTING_KEEP_SIDE_INVALID")]
    public void Fitting_payload_rejects_invalid_target_or_keep_side(
        string elementId,
        string role,
        string keepSide,
        string expectedCode)
    {
        var command = Command("apply-fitting", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new { elementId, role }),
            ["plane"] = Plane(),
            ["keepSide"] = JsonSerializer.SerializeToElement(keepSide),
        });

        var result = TeklaPlanPayloads.ParseApplyFitting(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Generic_payload_validation_dispatches_to_fitting()
    {
        var command = Command("apply-fitting", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new { elementId = "beam-1", role = "part" }),
            ["plane"] = Plane(),
            ["keepSide"] = JsonSerializer.SerializeToElement("unsupported"),
        });

        var diagnostics = TeklaPlanPayloads.Validate(command);

        Assert.Contains(diagnostics, item => item.Code == "TEKLA_PLAN_FITTING_KEEP_SIDE_INVALID");
    }

    [Theory]
    [InlineData("prismatic", "positive", 750d)]
    [InlineData("through-contour", "negative", 120d)]
    public void Boolean_cut_payload_parses_stable_target_and_canonical_cutter(
        string kind,
        string extrusionSide,
        double thickness)
    {
        var command = BooleanCutCommand(kind, extrusionSide, thickness);

        var result = TeklaPlanPayloads.ParseApplyBooleanCut(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("beam-1", result.Value!.Target.ElementId);
        Assert.Equal(kind, result.Value.Cutter.Kind);
        Assert.Equal(thickness, result.Value.Cutter.ThicknessMm);
        Assert.Equal(kind == "prismatic" ? "positive" : extrusionSide, result.Value.Cutter.ExtrusionSide);
        Assert.Equal(4, result.Value.Cutter.Contour.Edges.Count);
    }

    [Theory]
    [InlineData("swept", "positive", 100d, "TEKLA_PLAN_BOOLEAN_CUTTER_KIND_INVALID")]
    [InlineData("prismatic", "positive", 0d, "TEKLA_PLAN_BOOLEAN_CUTTER_DEPTH_INVALID")]
    [InlineData("through-contour", "both", 100d, "TEKLA_PLAN_BOOLEAN_CUTTER_EXTRUSION_INVALID")]
    public void Boolean_cut_payload_rejects_unsupported_or_invalid_cutters(
        string kind,
        string extrusionSide,
        double thickness,
        string expectedCode)
    {
        var command = BooleanCutCommand(kind, extrusionSide, thickness);

        var result = TeklaPlanPayloads.ParseApplyBooleanCut(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
        Assert.Contains(TeklaPlanPayloads.Validate(command), item => item.Code == expectedCode);
    }

    [Fact]
    public void Round_hole_payload_parses_an_exact_target_center_axis_and_diameter()
    {
        var command = HoleCommand();

        var result = TeklaPlanPayloads.ParseCreateHole(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("beam-1", result.Value!.Target.ElementId);
        Assert.Equal(125d, result.Value.Center.X);
        Assert.Equal(1d, result.Value.Axis.Z);
        Assert.Equal(24d, result.Value.DiameterMm);
        Assert.Null(result.Value.DepthMm);
        Assert.Equal("round", result.Value.HoleType);
        Assert.Empty(TeklaPlanPayloads.Validate(command));
    }

    [Fact]
    public void Round_hole_payload_preserves_explicit_blind_depth()
    {
        var command = HoleCommand();
        command.Payload["depthMm"] = JsonSerializer.SerializeToElement(18d);

        var result = TeklaPlanPayloads.ParseCreateHole(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal(18d, result.Value!.DepthMm);
    }

    [Theory]
    [InlineData("axis", "TEKLA_PLAN_HOLE_AXIS_INVALID")]
    [InlineData("diameter", "TEKLA_PLAN_HOLE_DIAMETER_INVALID")]
    [InlineData("depth", "TEKLA_PLAN_HOLE_DEPTH_INVALID")]
    public void Round_hole_payload_rejects_degenerate_geometry(string invalidProperty, string expectedCode)
    {
        var command = HoleCommand();
        if (invalidProperty == "axis") command.Payload["axis"] = JsonSerializer.SerializeToElement(new[] { 0d, 0d, 0d });
        if (invalidProperty == "diameter") command.Payload["diameterMm"] = JsonSerializer.SerializeToElement(0d);
        if (invalidProperty == "depth") command.Payload["depthMm"] = JsonSerializer.SerializeToElement(-1d);

        var result = TeklaPlanPayloads.ParseCreateHole(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Slotted_hole_payload_preserves_total_length_and_in_plane_direction()
    {
        var slotted = HoleCommand();
        slotted.Payload["holeType"] = JsonSerializer.SerializeToElement("slotted");
        slotted.Payload["slotLengthMm"] = JsonSerializer.SerializeToElement(40d);
        slotted.Payload["slotDirection"] = JsonSerializer.SerializeToElement(new[] { 1d, 0d, 0d });

        var result = TeklaPlanPayloads.ParseCreateHole(slotted);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal(40d, result.Value!.SlotLengthMm);
        Assert.Equal(1d, result.Value.SlotDirection!.X);
        Assert.Empty(TeklaPlanPayloads.Validate(slotted));
    }

    [Theory]
    [InlineData("missing-direction", "TEKLA_PLAN_HOLE_SLOT_DIRECTION_INVALID")]
    [InlineData("zero-direction", "TEKLA_PLAN_HOLE_SLOT_DIRECTION_INVALID")]
    [InlineData("parallel-direction", "TEKLA_PLAN_HOLE_SLOT_DIRECTION_NOT_IN_PLANE")]
    [InlineData("short-slot", "TEKLA_PLAN_HOLE_SLOT_LENGTH_INVALID")]
    public void Slotted_hole_payload_rejects_ambiguous_or_degenerate_geometry(string invalidProperty, string expectedCode)
    {
        var command = HoleCommand();
        command.Payload["holeType"] = JsonSerializer.SerializeToElement("slotted");
        command.Payload["slotLengthMm"] = JsonSerializer.SerializeToElement(invalidProperty == "short-slot" ? 24d : 40d);
        if (invalidProperty != "missing-direction")
        {
            command.Payload["slotDirection"] = JsonSerializer.SerializeToElement(
                invalidProperty == "zero-direction"
                    ? new[] { 0d, 0d, 0d }
                    : invalidProperty == "parallel-direction"
                        ? new[] { 0d, 0d, 1d }
                        : new[] { 1d, 0d, 0d });
        }

        var result = TeklaPlanPayloads.ParseCreateHole(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Round_hole_payload_rejects_all_slot_parameters()
    {
        var roundWithSlot = HoleCommand();
        roundWithSlot.Payload["slotLengthMm"] = JsonSerializer.SerializeToElement(40d);
        roundWithSlot.Payload["slotDirection"] = JsonSerializer.SerializeToElement(new[] { 1d, 0d, 0d });

        Assert.Contains(
            TeklaPlanPayloads.ParseCreateHole(roundWithSlot).Diagnostics,
            item => item.Code == "TEKLA_PLAN_HOLE_SLOT_PARAMETERS_INVALID");
    }

    [Fact]
    public void Bolt_group_payload_preserves_two_parts_frame_and_grid_pattern()
    {
        var command = BoltGroupCommand();

        var result = TeklaPlanPayloads.ParseCreateBoltGroup(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("beam-1", result.Value!.Target.ElementId);
        Assert.Equal("plate-1", result.Value.Participant.ElementId);
        Assert.Equal(125d, result.Value.Frame.Origin.X);
        Assert.Equal("7990", result.Value.BoltStandard);
        Assert.Equal(24d, result.Value.DiameterMm);
        Assert.Equal(80d, result.Value.LengthMm);
        Assert.Equal(2d, result.Value.ToleranceMm);
        Assert.Equal("shop", result.Value.BoltType);
        Assert.Equal("grid", result.Value.Pattern.Kind);
        Assert.Equal(3, result.Value.Pattern.CountX);
        Assert.Equal(2, result.Value.Pattern.CountY);
        Assert.True(result.Value.CreateHoles);
        Assert.Empty(TeklaPlanPayloads.Validate(command));
    }

    [Fact]
    public void Bolt_group_payload_requires_exactly_one_distinct_participant()
    {
        var noParticipant = BoltGroupCommand();
        noParticipant.Payload["participants"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        var sameParticipant = BoltGroupCommand();
        sameParticipant.Payload["participants"] = JsonSerializer.SerializeToElement(new[]
        {
            new { elementId = "beam-1", role = "part" },
        });

        Assert.Contains(
            TeklaPlanPayloads.ParseCreateBoltGroup(noParticipant).Diagnostics,
            item => item.Code == "TEKLA_PLAN_BOLT_PARTICIPANTS_INVALID");
        Assert.Contains(
            TeklaPlanPayloads.ParseCreateBoltGroup(sameParticipant).Diagnostics,
            item => item.Code == "TEKLA_PLAN_BOLT_PARTICIPANTS_SAME");
    }

    [Theory]
    [InlineData("frame", "TEKLA_PLAN_PLANE_INVALID")]
    [InlineData("standard", "TEKLA_PLAN_BOLT_STANDARD_INVALID")]
    [InlineData("diameter", "TEKLA_PLAN_BOLT_DIAMETER_INVALID")]
    [InlineData("length", "TEKLA_PLAN_BOLT_LENGTH_INVALID")]
    [InlineData("tolerance", "TEKLA_PLAN_BOLT_TOLERANCE_INVALID")]
    [InlineData("type", "TEKLA_PLAN_BOLT_TYPE_INVALID")]
    [InlineData("holes", "TEKLA_PLAN_BOLT_CREATE_HOLES_INVALID")]
    public void Bolt_group_payload_rejects_invalid_native_parameters(string invalidProperty, string expectedCode)
    {
        var command = BoltGroupCommand();
        if (invalidProperty == "frame") command.Payload["frame"] = JsonSerializer.SerializeToElement(new
        {
            origin = new[] { 0d, 0d, 0d },
            axisX = new[] { 1d, 0d, 0d },
            axisY = new[] { 0d, 1d, 0d },
            axisZ = new[] { 0d, 0d, -1d },
        });
        if (invalidProperty == "standard") command.Payload["boltStandard"] = JsonSerializer.SerializeToElement("");
        if (invalidProperty == "diameter") command.Payload["diameterMm"] = JsonSerializer.SerializeToElement(0d);
        if (invalidProperty == "length") command.Payload["lengthMm"] = JsonSerializer.SerializeToElement(-1d);
        if (invalidProperty == "tolerance") command.Payload["toleranceMm"] = JsonSerializer.SerializeToElement(-1d);
        if (invalidProperty == "type") command.Payload["boltType"] = JsonSerializer.SerializeToElement("field");
        if (invalidProperty == "holes") command.Payload["createHoles"] = JsonSerializer.SerializeToElement("yes");

        var result = TeklaPlanPayloads.ParseCreateBoltGroup(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
    }

    [Theory]
    [InlineData("linear-count")]
    [InlineData("linear-spacing")]
    [InlineData("grid-count")]
    [InlineData("points-empty")]
    [InlineData("points-duplicate")]
    public void Bolt_group_payload_rejects_ambiguous_patterns(string invalidPattern)
    {
        var command = BoltGroupCommand();
        command.Payload["pattern"] = invalidPattern switch
        {
            "linear-count" => JsonSerializer.SerializeToElement(new { kind = "linear", count = 1, spacingMm = 80d }),
            "linear-spacing" => JsonSerializer.SerializeToElement(new { kind = "linear", count = 2, spacingMm = 0d }),
            "grid-count" => JsonSerializer.SerializeToElement(new { kind = "grid", countX = 1, countY = 2, spacingXmm = 100d, spacingYmm = 80d }),
            "points-empty" => JsonSerializer.SerializeToElement(new { kind = "points", points = Array.Empty<double[]>() }),
            _ => JsonSerializer.SerializeToElement(new { kind = "points", points = new[] { new[] { 0d, 0d }, new[] { 0d, 0d } } }),
        };

        var result = TeklaPlanPayloads.ParseCreateBoltGroup(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code.Contains("TEKLA_PLAN_BOLT_PATTERN", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("arrow")]
    [InlineData("other")]
    [InlineData("both")]
    public void Weld_payload_parses_two_distinct_native_parts(string side)
    {
        var command = WeldCommand(side: side);

        var result = TeklaPlanPayloads.ParseCreateWeld(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("beam-1", result.Value!.Target.ElementId);
        Assert.Equal("plate-1", result.Value.Participant.ElementId);
        Assert.Equal("fillet", result.Value.WeldType);
        Assert.Equal(8d, result.Value.SizeMm);
        Assert.Equal("shop", result.Value.ShopSite);
        Assert.Equal(side, result.Value.Side);
    }

    [Theory]
    [InlineData("butt", 8d, "shop", "arrow", "TEKLA_PLAN_WELD_TYPE_UNSUPPORTED")]
    [InlineData("fillet", 0d, "shop", "arrow", "TEKLA_PLAN_WELD_SIZE_INVALID")]
    [InlineData("fillet", 8d, "field", "arrow", "TEKLA_PLAN_WELD_SHOP_SITE_INVALID")]
    [InlineData("fillet", 8d, "shop", "top", "TEKLA_PLAN_WELD_SIDE_INVALID")]
    public void Weld_payload_rejects_unsupported_native_parameters(
        string weldType,
        double sizeMm,
        string shopSite,
        string side,
        string expectedCode)
    {
        var command = WeldCommand(weldType, sizeMm, shopSite, side);

        var result = TeklaPlanPayloads.ParseCreateWeld(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
        Assert.Contains(TeklaPlanPayloads.Validate(command), item => item.Code == expectedCode);
    }

    [Fact]
    public void Weld_payload_requires_exactly_one_distinct_participant()
    {
        var noParticipant = WeldCommand();
        noParticipant.Payload["participants"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        var sameParticipant = WeldCommand();
        sameParticipant.Payload["participants"] = JsonSerializer.SerializeToElement(new[]
        {
            new { elementId = "beam-1", role = "part" },
        });
        var multipleParticipants = WeldCommand();
        multipleParticipants.Payload["participants"] = JsonSerializer.SerializeToElement(new[]
        {
            new { elementId = "plate-1", role = "part" },
            new { elementId = "plate-2", role = "part" },
        });

        Assert.Contains(
            TeklaPlanPayloads.ParseCreateWeld(noParticipant).Diagnostics,
            item => item.Code == "TEKLA_PLAN_WELD_PARTICIPANTS_INVALID");
        Assert.Contains(
            TeklaPlanPayloads.ParseCreateWeld(sameParticipant).Diagnostics,
            item => item.Code == "TEKLA_PLAN_WELD_PARTICIPANTS_SAME");
        Assert.Contains(
            TeklaPlanPayloads.ParseCreateWeld(multipleParticipants).Diagnostics,
            item => item.Code == "TEKLA_PLAN_WELD_PARTICIPANTS_INVALID");
    }

    [Theory]
    [InlineData("path", "TEKLA_PLAN_WELD_PATH_UNSUPPORTED")]
    [InlineData("intermittent", "TEKLA_PLAN_WELD_INTERMITTENT_UNSUPPORTED")]
    public void Weld_payload_blocks_unproven_extensions(string propertyName, string expectedCode)
    {
        var command = WeldCommand();
        command.Payload[propertyName] = JsonSerializer.SerializeToElement(new { enabled = true });

        var diagnostics = TeklaPlanPayloads.Validate(command);

        Assert.Contains(diagnostics, item => item.Code == expectedCode);
    }

    [Fact]
    public void Weld_payload_preserves_preparation_anchor_for_plan_reference_validation()
    {
        var command = WeldCommand();
        command.Payload["edgePreparationCommandId"] = JsonSerializer.SerializeToElement("prep-1");

        var result = TeklaPlanPayloads.ParseCreateWeld(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("prep-1", result.Value!.EdgePreparationCommandId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("[]")]
    public void Weld_payload_rejects_malformed_preparation_anchor(string json)
    {
        var command = WeldCommand();
        command.Payload["edgePreparationCommandId"] = JsonSerializer.Deserialize<JsonElement>(json);

        Assert.Contains(TeklaPlanPayloads.Validate(command),
            item => item.Code == "TEKLA_PLAN_WELD_PREPARATION_REFERENCE_INVALID");
    }

    [Fact]
    public void Assembly_payload_parses_exact_members_features_and_metadata()
    {
        var command = AssemblyCommand();

        var result = TeklaPlanPayloads.ParseCreateAssembly(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Equal("beam-main", result.Value!.MainElementId);
        Assert.Equal(new[] { "plate-1", "plate-2" }, result.Value.SecondaryElementIds);
        Assert.Equal(new[] { "weld-1", "bolt-1" }, result.Value.FeatureIds);
        Assert.Equal("Cross member CM-1", result.Value.Name);
        Assert.Equal("CM-1", result.Value.Properties["mark"].GetString());
        Assert.Empty(TeklaPlanPayloads.Validate(command));
    }

    [Fact]
    public void Assembly_payload_allows_a_singleton_without_optional_metadata()
    {
        var command = Command("create-assembly", new Dictionary<string, JsonElement>
        {
            ["mainElementId"] = JsonSerializer.SerializeToElement("beam-main"),
            ["secondaryElementIds"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            ["featureIds"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
        });

        var result = TeklaPlanPayloads.ParseCreateAssembly(command);

        Assert.True(result.Success, Messages(result.Diagnostics));
        Assert.Empty(result.Value!.SecondaryElementIds);
        Assert.Empty(result.Value.FeatureIds);
        Assert.Null(result.Value.Name);
        Assert.Empty(result.Value.Properties);
    }

    [Theory]
    [InlineData("duplicate-secondary", "TEKLA_PLAN_ASSEMBLY_SECONDARY_ELEMENTS_INVALID")]
    [InlineData("main-in-secondary", "TEKLA_PLAN_ASSEMBLY_MAIN_ELEMENT_DUPLICATED")]
    [InlineData("duplicate-feature", "TEKLA_PLAN_ASSEMBLY_FEATURES_INVALID")]
    [InlineData("empty-name", "TEKLA_PLAN_ASSEMBLY_NAME_INVALID")]
    [InlineData("invalid-properties", "TEKLA_PLAN_ASSEMBLY_PROPERTIES_INVALID")]
    public void Assembly_payload_rejects_ambiguous_or_invalid_identity(string scenario, string expectedCode)
    {
        var command = AssemblyCommand();
        switch (scenario)
        {
            case "duplicate-secondary":
                command.Payload["secondaryElementIds"] = JsonSerializer.SerializeToElement(new[] { "plate-1", "plate-1" });
                break;
            case "main-in-secondary":
                command.Payload["secondaryElementIds"] = JsonSerializer.SerializeToElement(new[] { "beam-main" });
                break;
            case "duplicate-feature":
                command.Payload["featureIds"] = JsonSerializer.SerializeToElement(new[] { "weld-1", "weld-1" });
                break;
            case "empty-name":
                command.Payload["name"] = JsonSerializer.SerializeToElement(" ");
                break;
            case "invalid-properties":
                command.Payload["properties"] = JsonSerializer.SerializeToElement(new[] { "mark" });
                break;
        }

        var result = TeklaPlanPayloads.ParseCreateAssembly(command);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
        Assert.Contains(TeklaPlanPayloads.Validate(command), item => item.Code == expectedCode);
    }

    private static TeklaPlanCommand AssemblyCommand()
        => Command("create-assembly", new Dictionary<string, JsonElement>
        {
            ["mainElementId"] = JsonSerializer.SerializeToElement("beam-main"),
            ["secondaryElementIds"] = JsonSerializer.SerializeToElement(new[] { "plate-1", "plate-2" }),
            ["featureIds"] = JsonSerializer.SerializeToElement(new[] { "weld-1", "bolt-1" }),
            ["name"] = JsonSerializer.SerializeToElement("Cross member CM-1"),
            ["properties"] = JsonSerializer.SerializeToElement(new { mark = "CM-1" }),
        });

    private static TeklaPlanCommand PolyBeamCommand(JsonElement[] nodes, JsonElement[] segments)
        => Command("create-poly-beam", new Dictionary<string, JsonElement>
        {
            ["path"] = Path(nodes, segments),
            ["profile"] = JsonSerializer.SerializeToElement(new { kind = "plate", thicknessMm = 20d, widthMm = 300d }),
            ["material"] = Material(),
        });

    private static TeklaPlanCommand ContourPlateCommand(JsonElement plane, JsonElement[] edges)
        => Command("create-contour-plate", new Dictionary<string, JsonElement>
        {
            ["plane"] = plane,
            ["contour"] = JsonSerializer.SerializeToElement(new
            {
                id = "contour-1",
                vertices = new[]
                {
                    new { id = "v1", point = new[] { 0d, 0d } },
                    new { id = "v2", point = new[] { 1000d, 0d } },
                    new { id = "v3", point = new[] { 1000d, 500d } },
                    new { id = "v4", point = new[] { 0d, 500d } },
                },
                edges,
            }),
            ["thicknessMm"] = JsonSerializer.SerializeToElement(20d),
            ["extrusionSide"] = JsonSerializer.SerializeToElement("symmetric"),
            ["material"] = Material(),
        });

    private static TeklaPlanCommand ContourCornerCommand(
        string targetKind = "contour-vertex",
        string cornerType = "chamfer",
        double sizeXmm = 40d)
        => Command("apply-contour-corner", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new
            {
                elementId = "plate-1",
                role = "outer-corner",
                path = new[] { "outer", "outer-v2" },
            }),
            ["targetTopology"] = JsonSerializer.SerializeToElement(new
            {
                kind = targetKind,
                contourId = "outer",
                vertexId = "outer-v2",
            }),
            ["cornerType"] = JsonSerializer.SerializeToElement(cornerType),
            ["sizeXmm"] = JsonSerializer.SerializeToElement(sizeXmm),
            ["sizeYmm"] = JsonSerializer.SerializeToElement(25d),
        });

    private static TeklaPlanCommand EdgeTreatmentCommand(
        string targetKind = "contour-edge",
        string treatmentType = "chamfer",
        double sizeMm = 12d)
        => Command("apply-edge-treatment", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new
            {
                elementId = "plate-1",
                role = "outer-edge",
                path = new[] { "outer", "outer-e1" },
            }),
            ["targetTopology"] = JsonSerializer.SerializeToElement(new
            {
                kind = targetKind,
                contourId = "outer",
                edgeId = "outer-e1",
                side = "positive",
            }),
            ["treatmentType"] = JsonSerializer.SerializeToElement(treatmentType),
            ["sizeMm"] = JsonSerializer.SerializeToElement(sizeMm),
            ["secondarySizeMm"] = JsonSerializer.SerializeToElement(8d),
        });

    private static TeklaPlanCommand BooleanCutCommand(string kind, string extrusionSide, double thickness)
    {
        var cutter = kind == "prismatic"
            ? JsonSerializer.SerializeToElement(new
            {
                kind,
                plane = Plane(),
                contour = RectangleContour(),
                depthMm = thickness,
            })
            : JsonSerializer.SerializeToElement(new
            {
                kind,
                plane = Plane(),
                contour = RectangleContour(),
                thicknessMm = thickness,
                extrusionSide,
            });
        return Command("apply-boolean-cut", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new { elementId = "beam-1", role = "part" }),
            ["cutter"] = cutter,
        });
    }

    private static TeklaPlanCommand HoleCommand()
        => Command("create-hole", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new { elementId = "beam-1", role = "part" }),
            ["center"] = JsonSerializer.SerializeToElement(new[] { 125d, 250d, 375d }),
            ["axis"] = JsonSerializer.SerializeToElement(new[] { 0d, 0d, 1d }),
            ["diameterMm"] = JsonSerializer.SerializeToElement(24d),
            ["holeType"] = JsonSerializer.SerializeToElement("round"),
        });

    private static TeklaPlanCommand BoltGroupCommand()
        => Command("create-bolt-group", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new { elementId = "beam-1", role = "part" }),
            ["participants"] = JsonSerializer.SerializeToElement(new[]
            {
                new { elementId = "plate-1", role = "part" },
            }),
            ["frame"] = JsonSerializer.SerializeToElement(new
            {
                origin = new[] { 125d, 250d, 375d },
                axisX = new[] { 1d, 0d, 0d },
                axisY = new[] { 0d, 1d, 0d },
                axisZ = new[] { 0d, 0d, 1d },
            }),
            ["boltStandard"] = JsonSerializer.SerializeToElement("7990"),
            ["diameterMm"] = JsonSerializer.SerializeToElement(24d),
            ["lengthMm"] = JsonSerializer.SerializeToElement(80d),
            ["toleranceMm"] = JsonSerializer.SerializeToElement(2d),
            ["boltType"] = JsonSerializer.SerializeToElement("shop"),
            ["pattern"] = JsonSerializer.SerializeToElement(new
            {
                kind = "grid",
                countX = 3,
                countY = 2,
                spacingXmm = 100d,
                spacingYmm = 80d,
            }),
            ["createHoles"] = JsonSerializer.SerializeToElement(true),
        });

    private static TeklaPlanCommand WeldCommand(
        string weldType = "fillet",
        double sizeMm = 8d,
        string shopSite = "shop",
        string side = "arrow")
        => Command("create-weld", new Dictionary<string, JsonElement>
        {
            ["target"] = JsonSerializer.SerializeToElement(new { elementId = "beam-1", role = "part" }),
            ["participants"] = JsonSerializer.SerializeToElement(new[]
            {
                new { elementId = "plate-1", role = "part" },
            }),
            ["weldType"] = JsonSerializer.SerializeToElement(weldType),
            ["sizeMm"] = JsonSerializer.SerializeToElement(sizeMm),
            ["shopSite"] = JsonSerializer.SerializeToElement(shopSite),
            ["side"] = JsonSerializer.SerializeToElement(side),
        });

    private static object RectangleContour()
        => new
        {
            id = "contour-1",
            vertices = new[]
            {
                new { id = "v1", point = new[] { 0d, 0d } },
                new { id = "v2", point = new[] { 1000d, 0d } },
                new { id = "v3", point = new[] { 1000d, 500d } },
                new { id = "v4", point = new[] { 0d, 500d } },
            },
            edges = new[]
            {
                new { id = "e1", kind = "line", startVertexId = "v1", endVertexId = "v2" },
                new { id = "e2", kind = "line", startVertexId = "v2", endVertexId = "v3" },
                new { id = "e3", kind = "line", startVertexId = "v3", endVertexId = "v4" },
                new { id = "e4", kind = "line", startVertexId = "v4", endVertexId = "v1" },
            },
        };

    private static JsonElement Path(JsonElement[] nodes, JsonElement[] segments)
        => JsonSerializer.SerializeToElement(new { id = "path-1", nodes, segments, closed = false });

    private static JsonElement Node(string id, double x, double y, double z)
        => JsonSerializer.SerializeToElement(new { id, point = new[] { x, y, z } });

    private static JsonElement Line(string id, string startNodeId, string endNodeId)
        => JsonSerializer.SerializeToElement(new { id, kind = "line", startNodeId, endNodeId });

    private static JsonElement DevelopedPlate(
        double thicknessMm,
        double stockWidthMm,
        double transverseOffsetMm,
        bool flipMiddle = false,
        bool duplicateMiddleId = false)
    {
        var diagonal = Math.Sqrt(0.5d);
        return JsonSerializer.SerializeToElement(new
        {
            thicknessMm,
            stockWidthMm,
            transverseOffsetMm,
            stationFrame = new
            {
                version = 1,
                stations = new[]
                {
                    new
                    {
                        id = "station-0",
                        spineNodeId = "n1",
                        stationMm = 0d,
                        frame = new
                        {
                            origin = new[] { 0d, 0d, 0d },
                            axisX = new[] { 0d, 1d, 0d },
                            axisY = new[] { 0d, 0d, 1d },
                            axisZ = new[] { 1d, 0d, 0d },
                        },
                    },
                    new
                    {
                        id = duplicateMiddleId ? "station-0" : "station-1000",
                        spineNodeId = "n2",
                        stationMm = 1000d,
                        frame = new
                        {
                            origin = new[] { 1000d, 0d, 0d },
                            axisX = flipMiddle ? new[] { diagonal, -diagonal, 0d } : new[] { -diagonal, diagonal, 0d },
                            axisY = flipMiddle ? new[] { 0d, 0d, -1d } : new[] { 0d, 0d, 1d },
                            axisZ = new[] { diagonal, diagonal, 0d },
                        },
                    },
                    new
                    {
                        id = "station-2000",
                        spineNodeId = "n3",
                        stationMm = 2000d,
                        frame = new
                        {
                            origin = new[] { 1000d, 1000d, 0d },
                            axisX = new[] { -1d, 0d, 0d },
                            axisY = new[] { 0d, 0d, 1d },
                            axisZ = new[] { 0d, 1d, 0d },
                        },
                    },
                },
            },
        });
    }

    private static JsonElement Plane()
        => JsonSerializer.SerializeToElement(new
        {
            origin = new[] { 0d, 0d, 0d },
            axisX = new[] { 2d, 0d, 0d },
            axisY = new[] { 0d, 3d, 0d },
            axisZ = new[] { 0d, 0d, 4d },
        });

    private static JsonElement[] RectangleEdges()
        => new[]
        {
            JsonSerializer.SerializeToElement(new { id = "e1", kind = "line", startVertexId = "v1", endVertexId = "v2" }),
            JsonSerializer.SerializeToElement(new { id = "e2", kind = "line", startVertexId = "v2", endVertexId = "v3" }),
            JsonSerializer.SerializeToElement(new { id = "e3", kind = "line", startVertexId = "v3", endVertexId = "v4" }),
            JsonSerializer.SerializeToElement(new { id = "e4", kind = "line", startVertexId = "v4", endVertexId = "v1" }),
        };

    private static JsonElement Material()
        => JsonSerializer.SerializeToElement(new { id = "S355", grade = "S355" });

    private static TeklaPlanCommand Command(string kind, Dictionary<string, JsonElement> payload)
        => new()
        {
            CommandId = $"command/{kind}",
            Kind = kind,
            Phase = "base-parts",
            Source = new TeklaPlanSourceRef
            {
                Kind = "element",
                Id = kind,
                StableKey = $"element/{kind}",
                Role = "part",
                SourceLayer = "generated",
            },
            Owner = new ConstructiveOwner { ModuleId = "bridge", EntityId = kind },
            RevisionProvenance = new ConstructiveRevisionProvenance
            {
                RevisionId = "revision-1",
                SourceHash = "source-hash",
                AdapterId = "bridge",
                AdapterVersion = "1.0.0",
            },
            Ownership = new TeklaOwnershipStamp
            {
                Namespace = TeklaPlanContract.OwnershipNamespace,
                DocumentSourceHash = "source-hash",
                GenerationId = "generation-1",
                SourceKind = "element",
                SourceId = kind,
                StableKey = $"element/{kind}",
                ModuleId = "bridge",
                ModuleVariantId = "variant-1",
                RevisionId = "revision-1",
            },
            Payload = payload,
        };

    private static string Messages(IReadOnlyList<TeklaPlanValidationDiagnostic> diagnostics)
        => string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Code}: {item.Message}"));
}
