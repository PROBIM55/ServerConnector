#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Platform.Contracts.TeklaPlan
{
    public sealed class TeklaVector3
    {
        public TeklaVector3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    public sealed class TeklaVector2
    {
        public TeklaVector2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public sealed class TeklaPathNode
    {
        public string Id { get; set; } = string.Empty;
        public TeklaVector3 Point { get; set; } = new(0, 0, 0);
    }

    public sealed class TeklaPathSegment
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string StartNodeId { get; set; } = string.Empty;
        public string EndNodeId { get; set; } = string.Empty;
        public TeklaVector3? PointOnArc { get; set; }
    }

    public sealed class TeklaPath3
    {
        public string Id { get; set; } = string.Empty;
        public IReadOnlyList<TeklaPathNode> Nodes { get; set; } = Array.Empty<TeklaPathNode>();
        public IReadOnlyList<TeklaPathSegment> Segments { get; set; } = Array.Empty<TeklaPathSegment>();
        public bool Closed { get; set; }
    }

    public sealed class TeklaBendSpec
    {
        public string PathNodeId { get; set; } = string.Empty;
        public double RadiusMm { get; set; }
        public double? NeutralAxisFactor { get; set; }
    }

    public sealed class TeklaDevelopedPlateSpec
    {
        public double ThicknessMm { get; set; }
        public double StockWidthMm { get; set; }
        public double TransverseOffsetMm { get; set; }
        public TeklaDevelopedStationFrameSpec StationFrame { get; set; } = new();
        public TeklaContour2? DevelopedContour { get; set; }
    }

    public sealed class TeklaDevelopedStationFrameSpec
    {
        public int Version { get; set; }
        public IReadOnlyList<TeklaDevelopedStationSpec> Stations { get; set; } = Array.Empty<TeklaDevelopedStationSpec>();
    }

    public sealed class TeklaDevelopedStationSpec
    {
        public string Id { get; set; } = string.Empty;
        public string SpineNodeId { get; set; } = string.Empty;
        public double StationMm { get; set; }
        public TeklaPlane3 Frame { get; set; } = new();
    }

    public sealed class TeklaPlane3
    {
        public TeklaVector3 Origin { get; set; } = new(0, 0, 0);
        public TeklaVector3 AxisX { get; set; } = new(1, 0, 0);
        public TeklaVector3 AxisY { get; set; } = new(0, 1, 0);
        public TeklaVector3 AxisZ { get; set; } = new(0, 0, 1);
    }

    public sealed class TeklaContourVertex2
    {
        public string Id { get; set; } = string.Empty;
        public TeklaVector2 Point { get; set; } = new(0, 0);
    }

    public sealed class TeklaContourEdge2
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string StartVertexId { get; set; } = string.Empty;
        public string EndVertexId { get; set; } = string.Empty;
        public double? Bulge { get; set; }
    }

    public sealed class TeklaContour2
    {
        public string Id { get; set; } = string.Empty;
        public IReadOnlyList<TeklaContourVertex2> Vertices { get; set; } = Array.Empty<TeklaContourVertex2>();
        public IReadOnlyList<TeklaContourEdge2> Edges { get; set; } = Array.Empty<TeklaContourEdge2>();
    }

    public sealed class TeklaProfileSpec
    {
        public string Kind { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? CatalogId { get; set; }
        public double? ThicknessMm { get; set; }
        public double? WidthMm { get; set; }
    }

    public sealed class TeklaMaterialSpec
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Grade { get; set; }
        public string? Standard { get; set; }
    }

    public sealed class TeklaSectionPlacementSpec
    {
        public string Anchor { get; set; } = string.Empty;
        public double? OffsetXmm { get; set; }
        public double? OffsetYmm { get; set; }
        public double? RotationDeg { get; set; }
    }

    public sealed class TeklaCreateBeamPayload
    {
        public TeklaVector3 Start { get; set; } = new(0, 0, 0);
        public TeklaVector3 End { get; set; } = new(0, 0, 0);
        public TeklaProfileSpec Profile { get; set; } = new();
        public TeklaMaterialSpec Material { get; set; } = new();
        public TeklaSectionPlacementSpec? Placement { get; set; }
    }

    public sealed class TeklaCreatePolyBeamPayload
    {
        public TeklaPath3 Path { get; set; } = new();
        public TeklaProfileSpec Profile { get; set; } = new();
        public TeklaMaterialSpec Material { get; set; } = new();
        public TeklaSectionPlacementSpec? Placement { get; set; }
        public IReadOnlyList<TeklaBendSpec> Bends { get; set; } = Array.Empty<TeklaBendSpec>();
        public TeklaDevelopedPlateSpec? DevelopedPlate { get; set; }
    }

    public sealed class TeklaCreateContourPlatePayload
    {
        public TeklaPlane3 Plane { get; set; } = new();
        public TeklaContour2 Contour { get; set; } = new();
        public double ThicknessMm { get; set; }
        public string ExtrusionSide { get; set; } = string.Empty;
        public TeklaMaterialSpec Material { get; set; } = new();
    }

    public sealed class TeklaTopologyRef
    {
        public string ElementId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public IReadOnlyList<string> Path { get; set; } = Array.Empty<string>();
    }

    public sealed class TeklaContourVertexTarget
    {
        public string Kind { get; set; } = string.Empty;
        public string ContourId { get; set; } = string.Empty;
        public string VertexId { get; set; } = string.Empty;
    }

    public sealed class TeklaContourEdgeTarget
    {
        public string Kind { get; set; } = string.Empty;
        public string ContourId { get; set; } = string.Empty;
        public string EdgeId { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
    }

    public sealed class TeklaApplyContourCornerPayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaContourVertexTarget TargetTopology { get; set; } = new();
        public string CornerType { get; set; } = string.Empty;
        public double SizeXmm { get; set; }
        public double? SizeYmm { get; set; }
    }

    public sealed class TeklaApplyEdgeTreatmentPayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaContourEdgeTarget TargetTopology { get; set; } = new();
        public string TreatmentType { get; set; } = string.Empty;
        public double SizeMm { get; set; }
        public double? SecondarySizeMm { get; set; }
    }

    public sealed class TeklaApplyFittingPayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaPlane3 Plane { get; set; } = new();
        public string KeepSide { get; set; } = string.Empty;
    }

    public sealed class TeklaBooleanCutGeometryPayload
    {
        public string Kind { get; set; } = string.Empty;
        public TeklaPlane3 Plane { get; set; } = new();
        public TeklaContour2 Contour { get; set; } = new();
        public double ThicknessMm { get; set; }
        public string ExtrusionSide { get; set; } = string.Empty;
    }

    public sealed class TeklaApplyBooleanCutPayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaBooleanCutGeometryPayload Cutter { get; set; } = new();
    }

    public sealed class TeklaCreateHolePayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaVector3 Center { get; set; } = new(0, 0, 0);
        public TeklaVector3 Axis { get; set; } = new(0, 0, 1);
        public double DiameterMm { get; set; }
        public double? DepthMm { get; set; }
        public string HoleType { get; set; } = string.Empty;
        public double? SlotLengthMm { get; set; }
        public TeklaVector3? SlotDirection { get; set; }
    }

    public sealed class TeklaBoltPatternSpec
    {
        public string Kind { get; set; } = string.Empty;
        public int? Count { get; set; }
        public double? SpacingMm { get; set; }
        public int? CountX { get; set; }
        public int? CountY { get; set; }
        public double? SpacingXmm { get; set; }
        public double? SpacingYmm { get; set; }
        public IReadOnlyList<TeklaVector2> Points { get; set; } = Array.Empty<TeklaVector2>();
    }

    public sealed class TeklaCreateBoltGroupPayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaTopologyRef Participant { get; set; } = new();
        public TeklaPlane3 Frame { get; set; } = new();
        public string BoltStandard { get; set; } = string.Empty;
        public double DiameterMm { get; set; }
        public double? LengthMm { get; set; }
        public double ToleranceMm { get; set; }
        public string BoltType { get; set; } = string.Empty;
        public TeklaBoltPatternSpec Pattern { get; set; } = new();
        public bool CreateHoles { get; set; }
    }

    public sealed class TeklaCreateWeldPayload
    {
        public TeklaTopologyRef Target { get; set; } = new();
        public TeklaTopologyRef Participant { get; set; } = new();
        public string WeldType { get; set; } = string.Empty;
        public double SizeMm { get; set; }
        public string ShopSite { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        // Anchor to the complete mapped cut feature, not a request for SDK weld preparation.
        public string? EdgePreparationCommandId { get; set; }
    }

    public sealed class TeklaCreateAssemblyPayload
    {
        public string MainElementId { get; set; } = string.Empty;
        public IReadOnlyList<string> SecondaryElementIds { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> FeatureIds { get; set; } = Array.Empty<string>();
        public string? Name { get; set; }
        public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } =
            new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    public sealed class TeklaPayloadParseResult<T>
    {
        public TeklaPayloadParseResult(T? value, IReadOnlyList<TeklaPlanValidationDiagnostic> diagnostics)
        {
            Value = value;
            Diagnostics = diagnostics;
        }

        public bool Success => Value is not null && Diagnostics.Count == 0;
        public T? Value { get; }
        public IReadOnlyList<TeklaPlanValidationDiagnostic> Diagnostics { get; }
    }

    /// <summary>
    /// Strict, CAD-independent parsing for native command payloads. Tekla API
    /// code consumes these typed values only after preflight has succeeded.
    /// </summary>
    public static class TeklaPlanPayloads
    {
        private static readonly HashSet<string> PlacementAnchors = new(StringComparer.Ordinal)
        {
            "center", "top", "bottom", "left", "right", "custom",
        };

        public static IReadOnlyList<TeklaPlanValidationDiagnostic> Validate(TeklaPlanCommand command)
        {
            if (string.Equals(command.Kind, "create-beam", StringComparison.Ordinal))
                return ParseCreateBeam(command).Diagnostics;
            if (string.Equals(command.Kind, "create-poly-beam", StringComparison.Ordinal))
                return ParseCreatePolyBeam(command).Diagnostics;
            if (string.Equals(command.Kind, "create-contour-plate", StringComparison.Ordinal))
                return ParseCreateContourPlate(command).Diagnostics;
            if (string.Equals(command.Kind, "apply-fitting", StringComparison.Ordinal))
                return ParseApplyFitting(command).Diagnostics;
            if (string.Equals(command.Kind, "apply-boolean-cut", StringComparison.Ordinal))
                return ParseApplyBooleanCut(command).Diagnostics;
            if (string.Equals(command.Kind, "create-hole", StringComparison.Ordinal))
                return ParseCreateHole(command).Diagnostics;
            if (string.Equals(command.Kind, "create-bolt-group", StringComparison.Ordinal))
                return ParseCreateBoltGroup(command).Diagnostics;
            if (string.Equals(command.Kind, "create-weld", StringComparison.Ordinal))
                return ParseCreateWeld(command).Diagnostics;
            if (string.Equals(command.Kind, "apply-contour-corner", StringComparison.Ordinal))
                return ParseApplyContourCorner(command).Diagnostics;
            if (string.Equals(command.Kind, "apply-edge-treatment", StringComparison.Ordinal))
                return ParseApplyEdgeTreatment(command).Diagnostics;
            if (string.Equals(command.Kind, "create-assembly", StringComparison.Ordinal))
                return ParseCreateAssembly(command).Diagnostics;
            return Array.Empty<TeklaPlanValidationDiagnostic>();
        }

        public static TeklaPayloadParseResult<TeklaCreateBeamPayload> ParseCreateBeam(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var start = ParseVector(command, "start", diagnostics);
            var end = ParseVector(command, "end", diagnostics);
            var profile = ParseProfile(command, diagnostics);
            var material = ParseMaterial(command, diagnostics);
            var placement = ParsePlacement(command, diagnostics);

            if (start is not null && end is not null)
            {
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var dz = end.Z - start.Z;
                if ((dx * dx) + (dy * dy) + (dz * dz) <= 1e-12)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BEAM_ZERO_LENGTH", "Beam start and end points must be distinct.");
                }
            }

            if (diagnostics.Count > 0 || start is null || end is null || profile is null || material is null)
                return new TeklaPayloadParseResult<TeklaCreateBeamPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreateBeamPayload>(
                new TeklaCreateBeamPayload
                {
                    Start = start,
                    End = end,
                    Profile = profile,
                    Material = material,
                    Placement = placement,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaCreatePolyBeamPayload> ParseCreatePolyBeam(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var path = ParsePath(command, diagnostics);
            var profile = ParseProfile(command, diagnostics);
            var material = ParseMaterial(command, diagnostics);
            var placement = ParsePlacement(command, diagnostics);
            var bends = ParseBends(command, path, diagnostics);
            var developedPlate = ParseDevelopedPlate(command, path, profile, diagnostics);

            if (diagnostics.Count > 0 || path is null || profile is null || material is null || bends is null)
                return new TeklaPayloadParseResult<TeklaCreatePolyBeamPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreatePolyBeamPayload>(
                new TeklaCreatePolyBeamPayload
                {
                    Path = path,
                    Profile = profile,
                    Material = material,
                    Placement = placement,
                    Bends = bends,
                    DevelopedPlate = developedPlate,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaCreateContourPlatePayload> ParseCreateContourPlate(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var plane = ParsePlane(command, diagnostics);
            var contour = ParseContour(command, diagnostics);
            var material = ParseMaterial(command, diagnostics);

            var thickness = 0d;
            if (!command.Payload.TryGetValue("thicknessMm", out var thicknessValue) ||
                thicknessValue.ValueKind != JsonValueKind.Number ||
                !thicknessValue.TryGetDouble(out thickness) ||
                !IsFinite(thickness) ||
                thickness <= 0)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLATE_THICKNESS_INVALID", "Contour plate thicknessMm must be a positive finite number.");
            }

            var extrusionSide = string.Empty;
            if (!command.Payload.TryGetValue("extrusionSide", out var sideValue) ||
                sideValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(extrusionSide = sideValue.GetString() ?? string.Empty) ||
                (extrusionSide != "positive" && extrusionSide != "negative" && extrusionSide != "symmetric"))
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLATE_EXTRUSION_INVALID", "Contour plate extrusionSide must be positive, negative, or symmetric.");
            }

            if (diagnostics.Count > 0 || plane is null || contour is null || material is null)
                return new TeklaPayloadParseResult<TeklaCreateContourPlatePayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreateContourPlatePayload>(
                new TeklaCreateContourPlatePayload
                {
                    Plane = plane,
                    Contour = contour,
                    ThicknessMm = thickness,
                    ExtrusionSide = extrusionSide,
                    Material = material,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaApplyFittingPayload> ParseApplyFitting(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            var plane = ParsePlane(command, diagnostics);

            var keepSide = string.Empty;
            if (!command.Payload.TryGetValue("keepSide", out var keepSideValue) ||
                keepSideValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(keepSide = keepSideValue.GetString() ?? string.Empty) ||
                (keepSide != "positive" && keepSide != "negative"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_FITTING_KEEP_SIDE_INVALID",
                    "Fitting keepSide must be positive or negative.");
            }

            if (diagnostics.Count > 0 || target is null || plane is null)
                return new TeklaPayloadParseResult<TeklaApplyFittingPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaApplyFittingPayload>(
                new TeklaApplyFittingPayload
                {
                    Target = target,
                    Plane = plane,
                    KeepSide = keepSide,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaApplyContourCornerPayload> ParseApplyContourCorner(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            TeklaContourVertexTarget? targetTopology = null;
            if (TryGetObject(command, "targetTopology", diagnostics, out var targetTopologyValue))
            {
                if (!TryGetRequiredString(targetTopologyValue, "kind", out var targetKind) ||
                    !string.Equals(targetKind, "contour-vertex", StringComparison.Ordinal) ||
                    !TryGetRequiredString(targetTopologyValue, "contourId", out var contourId) ||
                    !TryGetRequiredString(targetTopologyValue, "vertexId", out var vertexId))
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_CONTOUR_CORNER_TARGET_INVALID",
                        "Contour corner targetTopology must address a concrete contour-vertex with non-empty contourId and vertexId.");
                }
                else
                {
                    targetTopology = new TeklaContourVertexTarget
                    {
                        Kind = targetKind,
                        ContourId = contourId,
                        VertexId = vertexId,
                    };
                }
            }

            var cornerType = string.Empty;
            if (!command.Payload.TryGetValue("cornerType", out var cornerTypeValue) ||
                cornerTypeValue.ValueKind != JsonValueKind.String ||
                ((cornerType = cornerTypeValue.GetString() ?? string.Empty) != "chamfer" && cornerType != "round" && cornerType != "cove"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_TYPE_INVALID",
                    "Contour corner cornerType must be chamfer, round, or cove.");
            }

            var sizeXmm = 0d;
            if (!command.Payload.TryGetValue("sizeXmm", out var sizeXValue) ||
                sizeXValue.ValueKind != JsonValueKind.Number ||
                !sizeXValue.TryGetDouble(out sizeXmm) ||
                !IsFinite(sizeXmm) ||
                sizeXmm <= 0)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_SIZE_X_INVALID",
                    "Contour corner sizeXmm must be a positive finite number.");
            }

            double? sizeYmm = null;
            if (command.Payload.TryGetValue("sizeYmm", out var sizeYValue))
            {
                if (sizeYValue.ValueKind != JsonValueKind.Number ||
                    !sizeYValue.TryGetDouble(out var parsedSizeY) ||
                    !IsFinite(parsedSizeY) ||
                    parsedSizeY <= 0)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_CONTOUR_CORNER_SIZE_Y_INVALID",
                        "Contour corner sizeYmm must be a positive finite number when provided.");
                }
                else
                {
                    sizeYmm = parsedSizeY;
                }
            }

            if (diagnostics.Count > 0 || target is null || targetTopology is null)
                return new TeklaPayloadParseResult<TeklaApplyContourCornerPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaApplyContourCornerPayload>(
                new TeklaApplyContourCornerPayload
                {
                    Target = target,
                    TargetTopology = targetTopology,
                    CornerType = cornerType,
                    SizeXmm = sizeXmm,
                    SizeYmm = sizeYmm,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaApplyEdgeTreatmentPayload> ParseApplyEdgeTreatment(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            TeklaContourEdgeTarget? targetTopology = null;
            if (TryGetObject(command, "targetTopology", diagnostics, out var targetTopologyValue))
            {
                if (!TryGetRequiredString(targetTopologyValue, "kind", out var targetKind) ||
                    !string.Equals(targetKind, "contour-edge", StringComparison.Ordinal) ||
                    !TryGetRequiredString(targetTopologyValue, "contourId", out var contourId) ||
                    !TryGetRequiredString(targetTopologyValue, "edgeId", out var edgeId) ||
                    !TryGetRequiredString(targetTopologyValue, "side", out var side) ||
                    (side != "positive" && side != "negative"))
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_EDGE_TREATMENT_TARGET_INVALID",
                        "Edge treatment targetTopology must address a concrete contour-edge with non-empty contourId and edgeId plus physical side positive or negative.");
                }
                else
                {
                    targetTopology = new TeklaContourEdgeTarget
                    {
                        Kind = targetKind,
                        ContourId = contourId,
                        EdgeId = edgeId,
                        Side = side,
                    };
                }
            }

            var treatmentType = string.Empty;
            if (!command.Payload.TryGetValue("treatmentType", out var treatmentTypeValue) ||
                treatmentTypeValue.ValueKind != JsonValueKind.String ||
                (treatmentType = treatmentTypeValue.GetString() ?? string.Empty) != "chamfer")
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_EDGE_TREATMENT_TYPE_INVALID",
                    "Edge treatment treatmentType must be chamfer because Tekla EdgeChamfer only supports CHAMFER_LINE.");
            }

            var sizeMm = 0d;
            if (!command.Payload.TryGetValue("sizeMm", out var sizeValue) ||
                sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetDouble(out sizeMm) ||
                !IsFinite(sizeMm) ||
                sizeMm <= 0)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_EDGE_TREATMENT_SIZE_INVALID",
                    "Edge treatment sizeMm must be a positive finite number.");
            }

            double? secondarySizeMm = null;
            if (command.Payload.TryGetValue("secondarySizeMm", out var secondarySizeValue))
            {
                if (secondarySizeValue.ValueKind != JsonValueKind.Number ||
                    !secondarySizeValue.TryGetDouble(out var parsedSecondarySize) ||
                    !IsFinite(parsedSecondarySize) ||
                    parsedSecondarySize <= 0)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_EDGE_TREATMENT_SECONDARY_SIZE_INVALID",
                        "Edge treatment secondarySizeMm must be a positive finite number when provided.");
                }
                else
                {
                    secondarySizeMm = parsedSecondarySize;
                }
            }

            if (diagnostics.Count > 0 || target is null || targetTopology is null)
                return new TeklaPayloadParseResult<TeklaApplyEdgeTreatmentPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaApplyEdgeTreatmentPayload>(
                new TeklaApplyEdgeTreatmentPayload
                {
                    Target = target,
                    TargetTopology = targetTopology,
                    TreatmentType = treatmentType,
                    SizeMm = sizeMm,
                    SecondarySizeMm = secondarySizeMm,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaApplyBooleanCutPayload> ParseApplyBooleanCut(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            if (!TryGetObject(command, "cutter", diagnostics, out var cutterValue))
                return new TeklaPayloadParseResult<TeklaApplyBooleanCutPayload>(null, diagnostics);

            if (!TryGetRequiredString(cutterValue, "kind", out var kind) ||
                (kind != "prismatic" && kind != "through-contour"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_BOOLEAN_CUTTER_KIND_INVALID",
                    "Boolean cutter kind must be prismatic or through-contour.");
                return new TeklaPayloadParseResult<TeklaApplyBooleanCutPayload>(null, diagnostics);
            }

            var plane = ParseNestedPlane(command, cutterValue, diagnostics);
            var contour = ParseNestedContour(command, cutterValue, diagnostics);
            var thickness = 0d;
            var extrusionSide = string.Empty;
            if (kind == "prismatic")
            {
                if (!TryGetPositiveNumber(cutterValue, "depthMm", out thickness))
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_BOOLEAN_CUTTER_DEPTH_INVALID",
                        "Prismatic Boolean cutter depthMm must be a positive finite number.");
                }
                extrusionSide = "positive";
            }
            else
            {
                if (!TryGetPositiveNumber(cutterValue, "thicknessMm", out thickness))
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_BOOLEAN_CUTTER_THICKNESS_INVALID",
                        "Through-contour Boolean cutter thicknessMm must be a positive finite number.");
                }
                if (!TryGetRequiredString(cutterValue, "extrusionSide", out extrusionSide) ||
                    (extrusionSide != "positive" && extrusionSide != "negative" && extrusionSide != "symmetric"))
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_BOOLEAN_CUTTER_EXTRUSION_INVALID",
                        "Through-contour Boolean cutter extrusionSide must be positive, negative, or symmetric.");
                }
            }

            if (diagnostics.Count > 0 || target is null || plane is null || contour is null)
                return new TeklaPayloadParseResult<TeklaApplyBooleanCutPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaApplyBooleanCutPayload>(
                new TeklaApplyBooleanCutPayload
                {
                    Target = target,
                    Cutter = new TeklaBooleanCutGeometryPayload
                    {
                        Kind = kind,
                        Plane = plane,
                        Contour = contour,
                        ThicknessMm = thickness,
                        ExtrusionSide = extrusionSide,
                    },
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaCreateBoltGroupPayload> ParseCreateBoltGroup(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            TeklaTopologyRef? participant = null;
            if (!command.Payload.TryGetValue("participants", out var participantsValue) ||
                participantsValue.ValueKind != JsonValueKind.Array ||
                participantsValue.GetArrayLength() != 1)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_BOLT_PARTICIPANTS_INVALID",
                    "A native bolt group v1 requires exactly one secondary participant.");
            }
            else
            {
                participant = ParseTopologyRefValue(command, participantsValue[0], "participants[0]", diagnostics);
            }

            TeklaPlane3? frame = null;
            if (TryGetObject(command, "frame", diagnostics, out var frameValue))
                frame = ParsePlaneValue(command, frameValue, diagnostics);

            var boltStandard = string.Empty;
            if (!command.Payload.TryGetValue("boltStandard", out var standardValue) ||
                standardValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(boltStandard = standardValue.GetString() ?? string.Empty))
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_STANDARD_INVALID", "Bolt boltStandard must be a non-empty string.");
            }

            var diameterMm = 0d;
            if (!command.Payload.TryGetValue("diameterMm", out var diameterValue) ||
                diameterValue.ValueKind != JsonValueKind.Number ||
                !diameterValue.TryGetDouble(out diameterMm) ||
                !IsFinite(diameterMm) ||
                diameterMm <= 0)
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_DIAMETER_INVALID", "Bolt diameterMm must be a positive finite number.");
            }

            double? lengthMm = null;
            if (command.Payload.TryGetValue("lengthMm", out var lengthValue))
            {
                if (lengthValue.ValueKind != JsonValueKind.Number ||
                    !lengthValue.TryGetDouble(out var parsedLength) ||
                    !IsFinite(parsedLength) ||
                    parsedLength <= 0)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BOLT_LENGTH_INVALID", "Bolt lengthMm must be a positive finite number when supplied.");
                }
                else
                {
                    lengthMm = parsedLength;
                }
            }

            var toleranceMm = 0d;
            if (!command.Payload.TryGetValue("toleranceMm", out var toleranceValue) ||
                toleranceValue.ValueKind != JsonValueKind.Number ||
                !toleranceValue.TryGetDouble(out toleranceMm) ||
                !IsFinite(toleranceMm) ||
                toleranceMm < 0)
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_TOLERANCE_INVALID", "Bolt toleranceMm must be a non-negative finite number.");
            }

            var boltType = string.Empty;
            if (!command.Payload.TryGetValue("boltType", out var boltTypeValue) ||
                boltTypeValue.ValueKind != JsonValueKind.String ||
                ((boltType = boltTypeValue.GetString() ?? string.Empty) != "shop" && boltType != "site"))
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_TYPE_INVALID", "Bolt boltType must be shop or site.");
            }

            var pattern = ParseBoltPattern(command, diagnostics);

            var createHoles = false;
            if (!command.Payload.TryGetValue("createHoles", out var createHolesValue) ||
                (createHolesValue.ValueKind != JsonValueKind.True && createHolesValue.ValueKind != JsonValueKind.False))
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_CREATE_HOLES_INVALID", "Bolt createHoles must be a boolean.");
            }
            else
            {
                createHoles = createHolesValue.GetBoolean();
            }

            if (target is not null && participant is not null &&
                string.Equals(target.ElementId, participant.ElementId, StringComparison.Ordinal))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_BOLT_PARTICIPANTS_SAME",
                    "Bolt target and participant must reference different constructive elements.");
            }

            if (diagnostics.Count > 0 || target is null || participant is null || frame is null || pattern is null)
                return new TeklaPayloadParseResult<TeklaCreateBoltGroupPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreateBoltGroupPayload>(
                new TeklaCreateBoltGroupPayload
                {
                    Target = target,
                    Participant = participant,
                    Frame = frame,
                    BoltStandard = boltStandard,
                    DiameterMm = diameterMm,
                    LengthMm = lengthMm,
                    ToleranceMm = toleranceMm,
                    BoltType = boltType,
                    Pattern = pattern,
                    CreateHoles = createHoles,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaCreateWeldPayload> ParseCreateWeld(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            TeklaTopologyRef? participant = null;
            if (!command.Payload.TryGetValue("participants", out var participantsValue) ||
                participantsValue.ValueKind != JsonValueKind.Array ||
                participantsValue.GetArrayLength() != 1)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_WELD_PARTICIPANTS_INVALID",
                    "A native fillet weld requires exactly one secondary participant.");
            }
            else
            {
                participant = ParseTopologyRefValue(
                    command,
                    participantsValue[0],
                    "participants[0]",
                    diagnostics);
            }

            var weldType = string.Empty;
            if (!command.Payload.TryGetValue("weldType", out var weldTypeValue) ||
                weldTypeValue.ValueKind != JsonValueKind.String ||
                !string.Equals(weldType = weldTypeValue.GetString() ?? string.Empty, "fillet", StringComparison.Ordinal))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_WELD_TYPE_UNSUPPORTED",
                    "Native weld v1 supports only weldType 'fillet'.");
            }

            var sizeMm = 0d;
            if (!command.Payload.TryGetValue("sizeMm", out var sizeValue) ||
                sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetDouble(out sizeMm) ||
                !IsFinite(sizeMm) ||
                sizeMm <= 0)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_WELD_SIZE_INVALID",
                    "Weld sizeMm must be a positive finite number.");
            }

            var shopSite = string.Empty;
            if (!command.Payload.TryGetValue("shopSite", out var shopSiteValue) ||
                shopSiteValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(shopSite = shopSiteValue.GetString() ?? string.Empty) ||
                (shopSite != "shop" && shopSite != "site"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_WELD_SHOP_SITE_INVALID",
                    "Weld shopSite must be shop or site.");
            }

            var side = string.Empty;
            if (!command.Payload.TryGetValue("side", out var sideValue) ||
                sideValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(side = sideValue.GetString() ?? string.Empty) ||
                (side != "arrow" && side != "other" && side != "both"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_WELD_SIDE_INVALID",
                    "Weld side must be arrow, other, or both.");
            }

            if (command.Payload.ContainsKey("path"))
                Add(diagnostics, command, "TEKLA_PLAN_WELD_PATH_UNSUPPORTED", "Polygon weld paths are not supported by native weld v1.");
            if (command.Payload.ContainsKey("intermittent"))
                Add(diagnostics, command, "TEKLA_PLAN_WELD_INTERMITTENT_UNSUPPORTED", "Intermittent welds are not supported by native weld v1.");
            string? edgePreparationCommandId = null;
            if (command.Payload.TryGetValue("edgePreparationCommandId", out var preparationValue) &&
                (preparationValue.ValueKind != JsonValueKind.String ||
                 string.IsNullOrWhiteSpace(edgePreparationCommandId = preparationValue.GetString())))
                Add(diagnostics, command, "TEKLA_PLAN_WELD_PREPARATION_REFERENCE_INVALID", "Linked edge preparation must name a non-empty command id.");

            if (target is not null && participant is not null &&
                string.Equals(target.ElementId, participant.ElementId, StringComparison.Ordinal))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_WELD_PARTICIPANTS_SAME",
                    "Weld target and participant must reference different constructive elements.");
            }

            if (diagnostics.Count > 0 || target is null || participant is null)
                return new TeklaPayloadParseResult<TeklaCreateWeldPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreateWeldPayload>(
                new TeklaCreateWeldPayload
                {
                    Target = target,
                    Participant = participant,
                    WeldType = weldType,
                    SizeMm = sizeMm,
                    ShopSite = shopSite,
                    Side = side,
                    EdgePreparationCommandId = edgePreparationCommandId,
                },
                diagnostics);
        }

        public static TeklaPayloadParseResult<TeklaCreateAssemblyPayload> ParseCreateAssembly(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();

            var mainElementId = string.Empty;
            if (!command.Payload.TryGetValue("mainElementId", out var mainElementValue) ||
                mainElementValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(mainElementId = mainElementValue.GetString() ?? string.Empty))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_ASSEMBLY_MAIN_ELEMENT_INVALID",
                    "Assembly mainElementId must be a non-empty string.");
            }

            var secondaryElementIds = ParseAssemblyIdArray(
                command,
                "secondaryElementIds",
                "TEKLA_PLAN_ASSEMBLY_SECONDARY_ELEMENTS_INVALID",
                diagnostics);
            var featureIds = ParseAssemblyIdArray(
                command,
                "featureIds",
                "TEKLA_PLAN_ASSEMBLY_FEATURES_INVALID",
                diagnostics);

            if (!string.IsNullOrWhiteSpace(mainElementId) &&
                secondaryElementIds.Contains(mainElementId, StringComparer.Ordinal))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_ASSEMBLY_MAIN_ELEMENT_DUPLICATED",
                    "Assembly mainElementId cannot also appear in secondaryElementIds.");
            }

            string? name = null;
            if (command.Payload.TryGetValue("name", out var nameValue))
            {
                if (nameValue.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(name = nameValue.GetString()))
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_ASSEMBLY_NAME_INVALID",
                        "Assembly name must be a non-empty string when provided.");
                }
            }

            IReadOnlyDictionary<string, JsonElement> properties =
                new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (command.Payload.TryGetValue("properties", out var propertiesValue))
            {
                if (propertiesValue.ValueKind != JsonValueKind.Object)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_ASSEMBLY_PROPERTIES_INVALID",
                        "Assembly properties must be an object when provided.");
                }
                else
                {
                    properties = propertiesValue
                        .EnumerateObject()
                        .ToDictionary(
                            static property => property.Name,
                            static property => property.Value.Clone(),
                            StringComparer.Ordinal);
                }
            }

            if (diagnostics.Count > 0)
                return new TeklaPayloadParseResult<TeklaCreateAssemblyPayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreateAssemblyPayload>(
                new TeklaCreateAssemblyPayload
                {
                    MainElementId = mainElementId,
                    SecondaryElementIds = secondaryElementIds,
                    FeatureIds = featureIds,
                    Name = name,
                    Properties = properties,
                },
                diagnostics);
        }

        private static IReadOnlyList<string> ParseAssemblyIdArray(
            TeklaPlanCommand command,
            string propertyName,
            string diagnosticCode,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!command.Payload.TryGetValue(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                Add(diagnostics, command, diagnosticCode, $"Assembly {propertyName} must be an array of non-empty strings.");
                return Array.Empty<string>();
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(item.GetString()))
                {
                    Add(diagnostics, command, diagnosticCode, $"Assembly {propertyName} must contain only non-empty strings.");
                    continue;
                }

                var id = item.GetString()!;
                if (!seen.Add(id))
                {
                    Add(diagnostics, command, diagnosticCode, $"Assembly {propertyName} contains duplicate id '{id}'.");
                    continue;
                }
                result.Add(id);
            }
            return result;
        }

        public static TeklaPayloadParseResult<TeklaCreateHolePayload> ParseCreateHole(TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            var target = ParseTopologyRef(command, "target", diagnostics);
            var center = ParseVector(command, "center", diagnostics);
            var axis = ParseVector(command, "axis", diagnostics);

            var diameterMm = 0d;
            if (!command.Payload.TryGetValue("diameterMm", out var diameterValue) ||
                diameterValue.ValueKind != JsonValueKind.Number ||
                !diameterValue.TryGetDouble(out diameterMm) ||
                !IsFinite(diameterMm) ||
                diameterMm <= 0)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_HOLE_DIAMETER_INVALID",
                    "Hole diameterMm must be a positive finite number.");
            }

            double? depthMm = null;
            if (command.Payload.TryGetValue("depthMm", out var depthValue))
            {
                if (depthValue.ValueKind != JsonValueKind.Number ||
                    !depthValue.TryGetDouble(out var parsedDepth) ||
                    !IsFinite(parsedDepth) ||
                    parsedDepth <= 0)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_HOLE_DEPTH_INVALID",
                        "Hole depthMm must be a positive finite number when supplied.");
                }
                else
                {
                    depthMm = parsedDepth;
                }
            }

            var holeType = string.Empty;
            if (!command.Payload.TryGetValue("holeType", out var holeTypeValue) ||
                holeTypeValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(holeType = holeTypeValue.GetString() ?? string.Empty) ||
                (holeType != "round" && holeType != "slotted"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_HOLE_TYPE_INVALID",
                    "Hole holeType must be round or slotted.");
            }

            if (axis is not null)
            {
                var lengthSquared = (axis.X * axis.X) + (axis.Y * axis.Y) + (axis.Z * axis.Z);
                if (lengthSquared <= 1e-12)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_HOLE_AXIS_INVALID",
                        "Hole axis must be a non-zero finite direction.");
                }
            }

            double? slotLengthMm = null;
            TeklaVector3? slotDirection = null;
            if (string.Equals(holeType, "slotted", StringComparison.Ordinal))
            {
                if (!command.Payload.TryGetValue("slotLengthMm", out var slotLengthValue) ||
                    slotLengthValue.ValueKind != JsonValueKind.Number ||
                    !slotLengthValue.TryGetDouble(out var parsedSlotLength) ||
                    !IsFinite(parsedSlotLength) ||
                    parsedSlotLength <= diameterMm)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_HOLE_SLOT_LENGTH_INVALID",
                        "Slotted hole slotLengthMm is the total end-to-end length and must be greater than diameterMm.");
                }
                else
                {
                    slotLengthMm = parsedSlotLength;
                }

                if (!command.Payload.TryGetValue("slotDirection", out var slotDirectionValue) ||
                    (slotDirection = ParseVector3(slotDirectionValue)) is null ||
                    SquaredLength(slotDirection) <= 1e-12)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_HOLE_SLOT_DIRECTION_INVALID",
                        "Slotted hole slotDirection must be a non-zero finite direction.");
                }
                else if (axis is not null)
                {
                    var denominator = Math.Sqrt(SquaredLength(axis) * SquaredLength(slotDirection));
                    var normalizedDot = denominator > 1e-12
                        ? Math.Abs(Dot(axis, slotDirection)) / denominator
                        : 1d;
                    if (normalizedDot > 1e-7)
                    {
                        Add(
                            diagnostics,
                            command,
                            "TEKLA_PLAN_HOLE_SLOT_DIRECTION_NOT_IN_PLANE",
                            "Slotted hole slotDirection must be perpendicular to axis.");
                    }
                }
            }
            else if (command.Payload.ContainsKey("slotLengthMm") || command.Payload.ContainsKey("slotDirection"))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_HOLE_SLOT_PARAMETERS_INVALID",
                    "A round hole cannot contain slotLengthMm or slotDirection.");
            }

            if (diagnostics.Count > 0 || target is null || center is null || axis is null)
                return new TeklaPayloadParseResult<TeklaCreateHolePayload>(null, diagnostics);

            return new TeklaPayloadParseResult<TeklaCreateHolePayload>(
                new TeklaCreateHolePayload
                {
                    Target = target,
                    Center = center,
                    Axis = axis,
                    DiameterMm = diameterMm,
                    DepthMm = depthMm,
                    HoleType = holeType,
                    SlotLengthMm = slotLengthMm,
                    SlotDirection = slotDirection,
                },
                diagnostics);
        }

        private static TeklaTopologyRef? ParseTopologyRef(
            TeklaPlanCommand command,
            string propertyName,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, propertyName, diagnostics, out var value)) return null;
            return ParseTopologyRefValue(command, value, propertyName, diagnostics);
        }

        private static TeklaTopologyRef? ParseTopologyRefValue(
            TeklaPlanCommand command,
            JsonElement value,
            string propertyName,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_TOPOLOGY_REF_INVALID",
                    $"Payload '{propertyName}' must be an object.");
                return null;
            }
            if (!TryGetRequiredString(value, "elementId", out var elementId) ||
                !TryGetRequiredString(value, "role", out var role))
            {
                Add(
                    diagnostics,
                    command,
                    "TEKLA_PLAN_TOPOLOGY_REF_INVALID",
                    $"Payload '{propertyName}' requires non-empty elementId and role.");
                return null;
            }

            var path = new List<string>();
            if (value.TryGetProperty("path", out var pathValue))
            {
                if (pathValue.ValueKind != JsonValueKind.Array)
                {
                    Add(
                        diagnostics,
                        command,
                        "TEKLA_PLAN_TOPOLOGY_REF_INVALID",
                        $"Payload '{propertyName}.path' must be an array of non-empty strings.");
                    return null;
                }
                foreach (var pathItem in pathValue.EnumerateArray())
                {
                    if (pathItem.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(pathItem.GetString()))
                    {
                        Add(
                            diagnostics,
                            command,
                            "TEKLA_PLAN_TOPOLOGY_REF_INVALID",
                            $"Payload '{propertyName}.path' must contain only non-empty strings.");
                        return null;
                    }
                    path.Add(pathItem.GetString()!);
                }
            }

            return new TeklaTopologyRef { ElementId = elementId, Role = role, Path = path };
        }

        private static TeklaBoltPatternSpec? ParseBoltPattern(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, "pattern", diagnostics, out var value)) return null;
            if (!TryGetRequiredString(value, "kind", out var kind) ||
                (kind != "single" && kind != "linear" && kind != "grid" && kind != "points"))
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_PATTERN_INVALID", "Bolt pattern.kind must be single, linear, grid, or points.");
                return null;
            }

            if (kind == "single") return new TeklaBoltPatternSpec { Kind = kind };

            if (kind == "linear")
            {
                if (!TryGetInteger(value, "count", 2, out var count) ||
                    !TryGetPositiveNumber(value, "spacingMm", out var spacingMm))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BOLT_PATTERN_LINEAR_INVALID", "Linear bolt pattern requires integer count >= 2 and positive spacingMm.");
                    return null;
                }
                return new TeklaBoltPatternSpec { Kind = kind, Count = count, SpacingMm = spacingMm };
            }

            if (kind == "grid")
            {
                if (!TryGetInteger(value, "countX", 2, out var countX) ||
                    !TryGetInteger(value, "countY", 2, out var countY) ||
                    !TryGetPositiveNumber(value, "spacingXmm", out var spacingXmm) ||
                    !TryGetPositiveNumber(value, "spacingYmm", out var spacingYmm))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BOLT_PATTERN_GRID_INVALID", "Grid bolt pattern requires countX/countY >= 2 and positive spacingXmm/spacingYmm.");
                    return null;
                }
                return new TeklaBoltPatternSpec
                {
                    Kind = kind,
                    CountX = countX,
                    CountY = countY,
                    SpacingXmm = spacingXmm,
                    SpacingYmm = spacingYmm,
                };
            }

            if (!value.TryGetProperty("points", out var pointsValue) ||
                pointsValue.ValueKind != JsonValueKind.Array ||
                pointsValue.GetArrayLength() == 0)
            {
                Add(diagnostics, command, "TEKLA_PLAN_BOLT_PATTERN_POINTS_INVALID", "Points bolt pattern requires at least one finite local point.");
                return null;
            }
            var points = new List<TeklaVector2>();
            foreach (var pointValue in pointsValue.EnumerateArray())
            {
                var point = ParseVector2(pointValue);
                if (point is null || points.Any(existing => Math.Abs(existing.X - point.X) <= 1e-9 && Math.Abs(existing.Y - point.Y) <= 1e-9))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BOLT_PATTERN_POINTS_INVALID", "Points bolt pattern requires unique finite local points.");
                    return null;
                }
                points.Add(point);
            }
            return new TeklaBoltPatternSpec { Kind = kind, Points = points };
        }

        private static TeklaPath3? ParsePath(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, "path", diagnostics, out var value)) return null;
            if (!TryGetRequiredString(value, "id", out var id))
            {
                Add(diagnostics, command, "TEKLA_PLAN_PATH_INVALID", "PolyBeam path.id is required.");
                return null;
            }

            if (!value.TryGetProperty("nodes", out var nodesValue) || nodesValue.ValueKind != JsonValueKind.Array || nodesValue.GetArrayLength() < 2)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PATH_INVALID", "PolyBeam path.nodes must contain at least two nodes.");
                return null;
            }
            if (!value.TryGetProperty("segments", out var segmentsValue) || segmentsValue.ValueKind != JsonValueKind.Array || segmentsValue.GetArrayLength() < 1)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PATH_INVALID", "PolyBeam path.segments must contain at least one segment.");
                return null;
            }

            var closed = value.TryGetProperty("closed", out var closedValue) && closedValue.ValueKind == JsonValueKind.True;
            if (closed)
                Add(diagnostics, command, "TEKLA_PLAN_PATH_CLOSED", "A native PolyBeam path must be open.");

            var nodes = new List<TeklaPathNode>();
            var nodeById = new Dictionary<string, TeklaPathNode>(StringComparer.Ordinal);
            foreach (var nodeValue in nodesValue.EnumerateArray())
            {
                if (nodeValue.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(nodeValue, "id", out var nodeId) ||
                    !nodeValue.TryGetProperty("point", out var pointValue))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_NODE_INVALID", "Every PolyBeam path node requires a non-empty id and point.");
                    continue;
                }
                var point = ParseVector3(pointValue);
                if (point is null)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_NODE_INVALID", $"PolyBeam path node '{nodeId}' point must contain three finite numbers.");
                    continue;
                }
                if (nodeById.ContainsKey(nodeId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_NODE_DUPLICATE", $"PolyBeam path node id '{nodeId}' is duplicated.");
                    continue;
                }
                var node = new TeklaPathNode { Id = nodeId, Point = point };
                nodes.Add(node);
                nodeById.Add(nodeId, node);
            }

            var segments = new List<TeklaPathSegment>();
            var segmentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segmentValue in segmentsValue.EnumerateArray())
            {
                if (segmentValue.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(segmentValue, "id", out var segmentId) ||
                    !TryGetRequiredString(segmentValue, "kind", out var kind) ||
                    !TryGetRequiredString(segmentValue, "startNodeId", out var startNodeId) ||
                    !TryGetRequiredString(segmentValue, "endNodeId", out var endNodeId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_SEGMENT_INVALID", "Every PolyBeam path segment requires id, kind, startNodeId, and endNodeId.");
                    continue;
                }
                if (!segmentIds.Add(segmentId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_SEGMENT_DUPLICATE", $"PolyBeam path segment id '{segmentId}' is duplicated.");
                    continue;
                }
                if (kind != "line" && kind != "arc")
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_SEGMENT_INVALID", $"PolyBeam path segment '{segmentId}' has unsupported kind '{kind}'.");
                    continue;
                }
                if (!nodeById.TryGetValue(startNodeId, out var startNode) || !nodeById.TryGetValue(endNodeId, out var endNode))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_NODE_REFERENCE_INVALID", $"PolyBeam path segment '{segmentId}' references an unknown node.");
                    continue;
                }
                if (startNodeId == endNodeId || SquaredDistance(startNode.Point, endNode.Point) <= 1e-12)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_SEGMENT_ZERO_LENGTH", $"PolyBeam path segment '{segmentId}' must have distinct endpoints.");
                    continue;
                }

                TeklaVector3? pointOnArc = null;
                if (kind == "arc")
                {
                    if (!segmentValue.TryGetProperty("pointOnArc", out var arcValue) || (pointOnArc = ParseVector3(arcValue)) is null)
                    {
                        Add(diagnostics, command, "TEKLA_PLAN_PATH_ARC_INVALID", $"PolyBeam arc segment '{segmentId}' requires a finite pointOnArc.");
                        continue;
                    }
                    if (!IsNonCollinear(startNode.Point, pointOnArc, endNode.Point))
                    {
                        Add(diagnostics, command, "TEKLA_PLAN_PATH_ARC_INVALID", $"PolyBeam arc segment '{segmentId}' points must not be collinear.");
                        continue;
                    }
                }
                segments.Add(new TeklaPathSegment
                {
                    Id = segmentId,
                    Kind = kind,
                    StartNodeId = startNodeId,
                    EndNodeId = endNodeId,
                    PointOnArc = pointOnArc,
                });
            }

            if (nodes.Count == nodesValue.GetArrayLength() && segments.Count == segmentsValue.GetArrayLength())
                ValidateOrderedOpenPath(command, nodes, segments, diagnostics);

            return diagnostics.Count == 0
                ? new TeklaPath3 { Id = id, Nodes = nodes, Segments = segments, Closed = closed }
                : null;
        }

        private static void ValidateOrderedOpenPath(
            TeklaPlanCommand command,
            IReadOnlyList<TeklaPathNode> nodes,
            IReadOnlyList<TeklaPathSegment> segments,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (segments.Count != nodes.Count - 1)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PATH_TOPOLOGY_INVALID", "PolyBeam path must contain exactly one more node than segment.");
                return;
            }
            for (var index = 1; index < segments.Count; index++)
            {
                if (!string.Equals(segments[index - 1].EndNodeId, segments[index].StartNodeId, StringComparison.Ordinal))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PATH_TOPOLOGY_INVALID", "PolyBeam path segments must form one ordered, consistently directed chain.");
                    return;
                }
            }
            var visited = new HashSet<string>(StringComparer.Ordinal) { segments[0].StartNodeId };
            foreach (var segment in segments) visited.Add(segment.EndNodeId);
            if (visited.Count != nodes.Count)
                Add(diagnostics, command, "TEKLA_PLAN_PATH_TOPOLOGY_INVALID", "PolyBeam path contains disconnected or unused nodes.");
        }

        private static IReadOnlyList<TeklaBendSpec>? ParseBends(
            TeklaPlanCommand command,
            TeklaPath3? path,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!command.Payload.TryGetValue("bends", out var value)) return Array.Empty<TeklaBendSpec>();
            if (value.ValueKind != JsonValueKind.Array)
            {
                Add(diagnostics, command, "TEKLA_PLAN_BENDS_INVALID", "PolyBeam bends must be an array.");
                return null;
            }

            var result = new List<TeklaBendSpec>();
            var nodeIds = path is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(GetInternalNodeIds(path), StringComparer.Ordinal);
            var usedNodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bendValue in value.EnumerateArray())
            {
                if (bendValue.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(bendValue, "pathNodeId", out var nodeId) ||
                    !TryGetPositiveNumber(bendValue, "radiusMm", out var radius))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BENDS_INVALID", "Every PolyBeam bend requires pathNodeId and a positive radiusMm.");
                    continue;
                }
                if (path is not null && !nodeIds.Contains(nodeId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BEND_NODE_INVALID", $"PolyBeam bend node '{nodeId}' must be an internal path node.");
                    continue;
                }
                if (!usedNodes.Add(nodeId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BEND_NODE_DUPLICATE", $"PolyBeam bend node '{nodeId}' is duplicated.");
                    continue;
                }
                if (!TryGetOptionalFiniteNumber(bendValue, "neutralAxisFactor", out var neutralAxisFactor) ||
                    (neutralAxisFactor.HasValue && (neutralAxisFactor.Value < 0 || neutralAxisFactor.Value > 1)))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_BENDS_INVALID", "PolyBeam bend neutralAxisFactor must be between 0 and 1.");
                    continue;
                }
                result.Add(new TeklaBendSpec { PathNodeId = nodeId, RadiusMm = radius, NeutralAxisFactor = neutralAxisFactor });
            }
            return result;
        }

        private static IEnumerable<string> GetInternalNodeIds(TeklaPath3 path)
        {
            if (path.Segments.Count < 2) yield break;
            for (var index = 0; index < path.Segments.Count - 1; index++)
                yield return path.Segments[index].EndNodeId;
        }

        private static TeklaDevelopedPlateSpec? ParseDevelopedPlate(
            TeklaPlanCommand command,
            TeklaPath3? path,
            TeklaProfileSpec? profile,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!command.Payload.TryGetValue("developedPlate", out var value)) return null;
            if (value.ValueKind != JsonValueKind.Object ||
                !TryGetPositiveNumber(value, "thicknessMm", out var thickness) ||
                !TryGetPositiveNumber(value, "stockWidthMm", out var stockWidth) ||
                !TryGetOptionalFiniteNumber(value, "transverseOffsetMm", out var transverseOffset) ||
                !transverseOffset.HasValue)
            {
                Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_PLATE_INVALID", "Developed plate dimensions must be finite; thicknessMm and stockWidthMm must be positive.");
                return null;
            }
            if (profile is null || profile.Kind != "plate" ||
                !NearlyEqual(profile.ThicknessMm!.Value, thickness) ||
                !NearlyEqual(profile.WidthMm!.Value, stockWidth))
            {
                Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_PLATE_PROFILE_MISMATCH", "Developed plate dimensions must match its plate profile.");
                return null;
            }
            if (path is null || path.Closed || path.Segments.Any(segment => segment.Kind != "line"))
            {
                Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_SPINE_UNSUPPORTED", "Developed stationFrame v1 requires an ordered open line-only spine.");
                return null;
            }
            if (!value.TryGetProperty("stationFrame", out var stationFrameValue) ||
                stationFrameValue.ValueKind != JsonValueKind.Object ||
                !stationFrameValue.TryGetProperty("version", out var versionValue) ||
                versionValue.ValueKind != JsonValueKind.Number ||
                !versionValue.TryGetInt32(out var version) ||
                version != 1 ||
                !stationFrameValue.TryGetProperty("stations", out var stationsValue) ||
                stationsValue.ValueKind != JsonValueKind.Array ||
                stationsValue.GetArrayLength() != path.Nodes.Count)
            {
                Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_INVALID", "Developed plate requires stationFrame version 1 with exactly one station per spine node.");
                return null;
            }

            var segmentTangents = new List<TeklaVector3>();
            var accumulatedStations = new List<double> { 0d };
            for (var index = 0; index < path.Segments.Count; index++)
            {
                var segment = path.Segments[index];
                if (!string.Equals(segment.StartNodeId, path.Nodes[index].Id, StringComparison.Ordinal) ||
                    !string.Equals(segment.EndNodeId, path.Nodes[index + 1].Id, StringComparison.Ordinal))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_SPINE_ORDER_INVALID", "Developed plate spine nodes and segments must share one explicit direction.");
                    return null;
                }
                var delta = Subtract(path.Nodes[index + 1].Point, path.Nodes[index].Point);
                var tangent = Normalize(delta);
                if (tangent is null)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_SPINE_INVALID", "Developed plate spine contains a zero-length segment.");
                    return null;
                }
                segmentTangents.Add(tangent);
                accumulatedStations.Add(accumulatedStations[index] + Length(delta));
            }
            var nodeTangents = new List<TeklaVector3>();
            for (var index = 0; index < path.Nodes.Count; index++)
            {
                if (index == 0)
                {
                    nodeTangents.Add(segmentTangents[0]);
                    continue;
                }
                if (index == path.Nodes.Count - 1)
                {
                    nodeTangents.Add(segmentTangents[segmentTangents.Count - 1]);
                    continue;
                }
                var tangent = Normalize(Add(segmentTangents[index - 1], segmentTangents[index]));
                if (tangent is null)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_TANGENT_REVERSAL", "Developed plate spine contains a 180 degree tangent reversal.");
                    return null;
                }
                nodeTangents.Add(tangent);
            }

            var parsedStations = new List<TeklaDevelopedStationSpec>();
            var stationIndex = 0;
            var seenStationIds = new HashSet<string>(StringComparer.Ordinal);
            var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stationValue in stationsValue.EnumerateArray())
            {
                if (stationValue.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(stationValue, "id", out var stationId) ||
                    !TryGetRequiredString(stationValue, "spineNodeId", out var spineNodeId) ||
                    !stationValue.TryGetProperty("stationMm", out var stationNumber) ||
                    stationNumber.ValueKind != JsonValueKind.Number ||
                    !stationNumber.TryGetDouble(out var stationMm) ||
                    !IsFinite(stationMm) || stationMm < 0 ||
                    !stationValue.TryGetProperty("frame", out var frameValue) ||
                    frameValue.ValueKind != JsonValueKind.Object)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_INVALID", "Every developed station requires id, spineNodeId, finite stationMm, and a frame.");
                    stationIndex++;
                    continue;
                }
                var frame = ParsePlaneValue(command, frameValue, diagnostics);
                var node = path.Nodes[stationIndex];
                var tangent = nodeTangents[stationIndex];
                if (!seenStationIds.Add(stationId))
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_ID_DUPLICATE", "Every developed station requires a unique id.");
                if (!string.Equals(spineNodeId, node.Id, StringComparison.Ordinal) || !seenNodeIds.Add(spineNodeId))
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_NODE_MISMATCH", "Developed station must address the spine node at the same ordered index exactly once.");
                if (Math.Abs(stationMm - accumulatedStations[stationIndex]) > 1e-6)
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_DISTANCE_MISMATCH", "Developed stationMm must equal accumulated spine length.");
                if (frame is not null)
                {
                    if (!VectorNear(frame.Origin, node.Point))
                        Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_ORIGIN_MISMATCH", "Developed station frame origin must match its spine node.");
                    if (!VectorNear(frame.AxisZ, tangent))
                        Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_TANGENT_MISMATCH", "Developed station frame axisZ must match the directed spine tangent.");
                    var expectedAxisX = Normalize(Cross(frame.AxisY, tangent));
                    if (expectedAxisX is null || !VectorNear(frame.AxisX, expectedAxisX))
                        Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_BASIS_MISMATCH", "Developed station frame axisX must equal signed axisY cross axisZ.");
                    if (stationIndex > 0 && parsedStations.Count == stationIndex)
                    {
                        var transportedAxisY = RotateShortest(parsedStations[stationIndex - 1].Frame.AxisY, nodeTangents[stationIndex - 1], tangent);
                        if (transportedAxisY is null || !VectorNear(frame.AxisY, transportedAxisY))
                            Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_STATION_FRAME_TRANSPORT_MISMATCH", "Developed station thickness normal must use shortest signed transport without a hidden flip.");
                    }
                    parsedStations.Add(new TeklaDevelopedStationSpec
                    {
                        Id = stationId,
                        SpineNodeId = spineNodeId,
                        StationMm = stationMm,
                        Frame = frame,
                    });
                }
                stationIndex++;
            }
            if (parsedStations.Count != path.Nodes.Count) return null;
            TeklaContour2? developedContour = null;
            if (value.TryGetProperty("developedContour", out var contourValue))
            {
                if (contourValue.ValueKind != JsonValueKind.Object)
                    Add(diagnostics, command, "TEKLA_PLAN_DEVELOPED_READBACK_CONTOUR_INVALID", "developedContour must be an explicit contour object.");
                else developedContour = ParseContourValue(command, contourValue, diagnostics);
            }
            var developed = new TeklaDevelopedPlateSpec
            {
                ThicknessMm = thickness,
                StockWidthMm = stockWidth,
                TransverseOffsetMm = transverseOffset.Value,
                StationFrame = new TeklaDevelopedStationFrameSpec
                {
                    Version = version,
                    Stations = parsedStations,
                },
                DevelopedContour = developedContour,
            };
            foreach (var diagnostic in TeklaDevelopedPlateReadback.ValidateContour(command, developed))
                diagnostics.Add(diagnostic);
            return developed;
        }

        private static TeklaPlane3? ParsePlane(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, "plane", diagnostics, out var value)) return null;
            return ParsePlaneValue(command, value, diagnostics);
        }

        private static TeklaPlane3? ParseNestedPlane(
            TeklaPlanCommand command,
            JsonElement parent,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!parent.TryGetProperty("plane", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PAYLOAD_OBJECT_REQUIRED", "Payload 'cutter.plane' must be an object.");
                return null;
            }
            return ParsePlaneValue(command, value, diagnostics);
        }

        private static TeklaPlane3? ParsePlaneValue(
            TeklaPlanCommand command,
            JsonElement value,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            var origin = ParseObjectVector3(value, "origin");
            var axisX = ParseObjectVector3(value, "axisX");
            var axisY = ParseObjectVector3(value, "axisY");
            var axisZ = ParseObjectVector3(value, "axisZ");
            if (origin is null || axisX is null || axisY is null || axisZ is null)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLANE_INVALID", "Native plane requires finite origin, axisX, axisY, and axisZ vectors.");
                return null;
            }

            var lengthX = Length(axisX);
            var lengthY = Length(axisY);
            var lengthZ = Length(axisZ);
            if (lengthX <= 1e-12 || lengthY <= 1e-12 || lengthZ <= 1e-12)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLANE_INVALID", "Native plane axes must be non-zero.");
                return null;
            }

            var normalizedX = Scale(axisX, 1 / lengthX);
            var normalizedY = Scale(axisY, 1 / lengthY);
            var normalizedZ = Scale(axisZ, 1 / lengthZ);
            if (Math.Abs(Dot(normalizedX, normalizedY)) > 1e-6 ||
                Math.Abs(Dot(normalizedX, normalizedZ)) > 1e-6 ||
                Math.Abs(Dot(normalizedY, normalizedZ)) > 1e-6 ||
                Dot(Cross(normalizedX, normalizedY), normalizedZ) < 1 - 1e-6)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLANE_INVALID", "Native plane axes must form a right-handed orthogonal basis.");
                return null;
            }
            return new TeklaPlane3 { Origin = origin, AxisX = normalizedX, AxisY = normalizedY, AxisZ = normalizedZ };
        }

        private static TeklaContour2? ParseContour(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, "contour", diagnostics, out var value)) return null;
            return ParseContourValue(command, value, diagnostics);
        }

        private static TeklaContour2? ParseNestedContour(
            TeklaPlanCommand command,
            JsonElement parent,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!parent.TryGetProperty("contour", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PAYLOAD_OBJECT_REQUIRED", "Payload 'cutter.contour' must be an object.");
                return null;
            }
            return ParseContourValue(command, value, diagnostics);
        }

        private static TeklaContour2? ParseContourValue(
            TeklaPlanCommand command,
            JsonElement value,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetRequiredString(value, "id", out var id))
            {
                Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_INVALID", "Contour plate contour.id is required.");
                return null;
            }
            if (!value.TryGetProperty("vertices", out var verticesValue) || verticesValue.ValueKind != JsonValueKind.Array || verticesValue.GetArrayLength() < 3 ||
                !value.TryGetProperty("edges", out var edgesValue) || edgesValue.ValueKind != JsonValueKind.Array || edgesValue.GetArrayLength() < 3)
            {
                Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_INVALID", "Contour plate contour requires at least three vertices and three edges.");
                return null;
            }

            var vertices = new List<TeklaContourVertex2>();
            var vertexById = new Dictionary<string, TeklaContourVertex2>(StringComparer.Ordinal);
            foreach (var vertexValue in verticesValue.EnumerateArray())
            {
                if (vertexValue.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(vertexValue, "id", out var vertexId) ||
                    !vertexValue.TryGetProperty("point", out var pointValue))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_VERTEX_INVALID", "Every contour vertex requires a non-empty id and point.");
                    continue;
                }
                var point = ParseVector2(pointValue);
                if (point is null)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_VERTEX_INVALID", $"Contour vertex '{vertexId}' point must contain two finite numbers.");
                    continue;
                }
                if (vertexById.ContainsKey(vertexId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_VERTEX_DUPLICATE", $"Contour vertex id '{vertexId}' is duplicated.");
                    continue;
                }
                var vertex = new TeklaContourVertex2 { Id = vertexId, Point = point };
                vertices.Add(vertex);
                vertexById.Add(vertexId, vertex);
            }

            var edges = new List<TeklaContourEdge2>();
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edgeValue in edgesValue.EnumerateArray())
            {
                if (edgeValue.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(edgeValue, "id", out var edgeId) ||
                    !TryGetRequiredString(edgeValue, "kind", out var kind) ||
                    !TryGetRequiredString(edgeValue, "startVertexId", out var startVertexId) ||
                    !TryGetRequiredString(edgeValue, "endVertexId", out var endVertexId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_EDGE_INVALID", "Every contour edge requires id, kind, startVertexId, and endVertexId.");
                    continue;
                }
                if (!edgeIds.Add(edgeId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_EDGE_DUPLICATE", $"Contour edge id '{edgeId}' is duplicated.");
                    continue;
                }
                if (kind != "line" && kind != "arc")
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_EDGE_INVALID", $"Contour edge '{edgeId}' has unsupported kind '{kind}'.");
                    continue;
                }
                if (!vertexById.TryGetValue(startVertexId, out var startVertex) || !vertexById.TryGetValue(endVertexId, out var endVertex))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_VERTEX_REFERENCE_INVALID", $"Contour edge '{edgeId}' references an unknown vertex.");
                    continue;
                }
                if (startVertexId == endVertexId || SquaredDistance(startVertex.Point, endVertex.Point) <= 1e-12)
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_EDGE_ZERO_LENGTH", $"Contour edge '{edgeId}' must have distinct endpoints.");
                    continue;
                }

                double? bulge = null;
                if (kind == "arc")
                {
                    if (!edgeValue.TryGetProperty("bulge", out var bulgeValue) ||
                        bulgeValue.ValueKind != JsonValueKind.Number ||
                        !bulgeValue.TryGetDouble(out var parsedBulge) ||
                        !IsFinite(parsedBulge) || Math.Abs(parsedBulge) <= 1e-12)
                    {
                        Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_ARC_INVALID", $"Contour arc edge '{edgeId}' requires a finite non-zero bulge.");
                        continue;
                    }
                    bulge = parsedBulge;
                }
                edges.Add(new TeklaContourEdge2
                {
                    Id = edgeId,
                    Kind = kind,
                    StartVertexId = startVertexId,
                    EndVertexId = endVertexId,
                    Bulge = bulge,
                });
            }

            if (vertices.Count == verticesValue.GetArrayLength() && edges.Count == edgesValue.GetArrayLength())
                ValidateOrderedClosedContour(command, vertices, edges, diagnostics);

            return diagnostics.Count == 0 ? new TeklaContour2 { Id = id, Vertices = vertices, Edges = edges } : null;
        }

        private static void ValidateOrderedClosedContour(
            TeklaPlanCommand command,
            IReadOnlyList<TeklaContourVertex2> vertices,
            IReadOnlyList<TeklaContourEdge2> edges,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (edges.Count != vertices.Count)
            {
                Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_TOPOLOGY_INVALID", "A contour must contain the same number of vertices and edges.");
                return;
            }
            for (var index = 1; index < edges.Count; index++)
            {
                if (!string.Equals(edges[index - 1].EndVertexId, edges[index].StartVertexId, StringComparison.Ordinal))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_TOPOLOGY_INVALID", "Contour edges must form one ordered, consistently directed cycle.");
                    return;
                }
            }
            if (!string.Equals(edges[edges.Count - 1].EndVertexId, edges[0].StartVertexId, StringComparison.Ordinal))
            {
                Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_NOT_CLOSED", "Contour edges must close the cycle.");
                return;
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges) visited.Add(edge.StartVertexId);
            if (visited.Count != vertices.Count)
            {
                Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_TOPOLOGY_INVALID", "Contour contains disconnected or unused vertices.");
                return;
            }

            for (var leftIndex = 0; leftIndex < vertices.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < vertices.Count; rightIndex++)
                {
                    if (SquaredDistance(vertices[leftIndex].Point, vertices[rightIndex].Point) <= 1e-12)
                    {
                        Add(
                            diagnostics,
                            command,
                            "TEKLA_PLAN_CONTOUR_VERTEX_POSITION_DUPLICATE",
                            $"Contour vertices '{vertices[leftIndex].Id}' and '{vertices[rightIndex].Id}' occupy the same point.");
                        return;
                    }
                }
            }

            if (edges.All(static edge => edge.Kind == "line"))
            {
                for (var leftIndex = 0; leftIndex < edges.Count; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1; rightIndex < edges.Count; rightIndex++)
                    {
                        var adjacent = rightIndex == leftIndex + 1 || (leftIndex == 0 && rightIndex == edges.Count - 1);
                        if (adjacent) continue;

                        var leftStart = FindVertex(vertices, edges[leftIndex].StartVertexId).Point;
                        var leftEnd = FindVertex(vertices, edges[leftIndex].EndVertexId).Point;
                        var rightStart = FindVertex(vertices, edges[rightIndex].StartVertexId).Point;
                        var rightEnd = FindVertex(vertices, edges[rightIndex].EndVertexId).Point;
                        if (!SegmentsIntersect(leftStart, leftEnd, rightStart, rightEnd)) continue;

                        Add(
                            diagnostics,
                            command,
                            "TEKLA_PLAN_CONTOUR_SELF_INTERSECTION",
                            $"Contour edges '{edges[leftIndex].Id}' and '{edges[rightIndex].Id}' intersect.");
                        return;
                    }
                }
            }

            var signedArea2 = 0d;
            foreach (var edge in edges)
            {
                var start = FindVertex(vertices, edge.StartVertexId).Point;
                var end = FindVertex(vertices, edge.EndVertexId).Point;
                signedArea2 += (start.X * end.Y) - (end.X * start.Y);
            }
            if (Math.Abs(signedArea2) <= 1e-9)
                Add(diagnostics, command, "TEKLA_PLAN_CONTOUR_AREA_INVALID", "Contour must enclose a non-zero area.");
        }

        private static bool SegmentsIntersect(
            TeklaVector2 firstStart,
            TeklaVector2 firstEnd,
            TeklaVector2 secondStart,
            TeklaVector2 secondEnd)
        {
            const double tolerance = 1e-9;
            var firstOrientation = Orientation(firstStart, firstEnd, secondStart);
            var secondOrientation = Orientation(firstStart, firstEnd, secondEnd);
            var thirdOrientation = Orientation(secondStart, secondEnd, firstStart);
            var fourthOrientation = Orientation(secondStart, secondEnd, firstEnd);

            if (((firstOrientation > tolerance && secondOrientation < -tolerance) ||
                 (firstOrientation < -tolerance && secondOrientation > tolerance)) &&
                ((thirdOrientation > tolerance && fourthOrientation < -tolerance) ||
                 (thirdOrientation < -tolerance && fourthOrientation > tolerance)))
            {
                return true;
            }

            return (Math.Abs(firstOrientation) <= tolerance && OnSegment(firstStart, firstEnd, secondStart, tolerance)) ||
                   (Math.Abs(secondOrientation) <= tolerance && OnSegment(firstStart, firstEnd, secondEnd, tolerance)) ||
                   (Math.Abs(thirdOrientation) <= tolerance && OnSegment(secondStart, secondEnd, firstStart, tolerance)) ||
                   (Math.Abs(fourthOrientation) <= tolerance && OnSegment(secondStart, secondEnd, firstEnd, tolerance));
        }

        private static double Orientation(TeklaVector2 start, TeklaVector2 end, TeklaVector2 point)
            => ((end.X - start.X) * (point.Y - start.Y)) - ((end.Y - start.Y) * (point.X - start.X));

        private static bool OnSegment(TeklaVector2 start, TeklaVector2 end, TeklaVector2 point, double tolerance)
            => point.X >= Math.Min(start.X, end.X) - tolerance &&
               point.X <= Math.Max(start.X, end.X) + tolerance &&
               point.Y >= Math.Min(start.Y, end.Y) - tolerance &&
               point.Y <= Math.Max(start.Y, end.Y) + tolerance;

        private static TeklaContourVertex2 FindVertex(IReadOnlyList<TeklaContourVertex2> vertices, string id)
        {
            foreach (var vertex in vertices)
                if (string.Equals(vertex.Id, id, StringComparison.Ordinal)) return vertex;
            throw new InvalidOperationException($"Validated contour vertex '{id}' was not found.");
        }

        private static TeklaVector3? ParseVector(
            TeklaPlanCommand command,
            string propertyName,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!command.Payload.TryGetValue(propertyName, out var value))
            {
                Add(diagnostics, command, "TEKLA_PLAN_VECTOR_INVALID", $"Payload '{propertyName}' must be an array of three finite numbers.");
                return null;
            }
            var result = ParseVector3(value);
            if (result is null)
                Add(diagnostics, command, "TEKLA_PLAN_VECTOR_INVALID", $"Payload '{propertyName}' must be an array of three finite numbers.");
            return result;
        }

        private static TeklaVector3? ParseObjectVector3(JsonElement value, string propertyName)
            => value.TryGetProperty(propertyName, out var property) ? ParseVector3(property) : null;

        private static TeklaVector3? ParseVector3(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return null;
            var coordinates = new double[3];
            var index = 0;
            foreach (var coordinate in value.EnumerateArray())
            {
                if (coordinate.ValueKind != JsonValueKind.Number ||
                    !coordinate.TryGetDouble(out var number) ||
                    !IsFinite(number)) return null;
                coordinates[index++] = number;
            }
            return new TeklaVector3(coordinates[0], coordinates[1], coordinates[2]);
        }

        private static TeklaVector2? ParseVector2(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2) return null;
            var coordinates = new double[2];
            var index = 0;
            foreach (var coordinate in value.EnumerateArray())
            {
                if (coordinate.ValueKind != JsonValueKind.Number ||
                    !coordinate.TryGetDouble(out var number) ||
                    !IsFinite(number)) return null;
                coordinates[index++] = number;
            }
            return new TeklaVector2(coordinates[0], coordinates[1]);
        }

        private static TeklaProfileSpec? ParseProfile(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, "profile", diagnostics, out var value)) return null;
            if (!TryGetRequiredString(value, "kind", out var kind))
            {
                Add(diagnostics, command, "TEKLA_PLAN_PROFILE_INVALID", "Beam profile.kind is required.");
                return null;
            }

            if (string.Equals(kind, "catalog", StringComparison.Ordinal))
            {
                if (!TryGetRequiredString(value, "name", out var name))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PROFILE_INVALID", "Catalog profile.name is required.");
                    return null;
                }
                if (!TryGetOptionalString(value, "catalogId", out var catalogId))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PROFILE_INVALID", "Catalog profile.catalogId must be a string.");
                    return null;
                }
                return new TeklaProfileSpec { Kind = kind, Name = name, CatalogId = catalogId };
            }

            if (string.Equals(kind, "plate", StringComparison.Ordinal))
            {
                if (!TryGetPositiveNumber(value, "thicknessMm", out var thickness) ||
                    !TryGetPositiveNumber(value, "widthMm", out var width))
                {
                    Add(diagnostics, command, "TEKLA_PLAN_PROFILE_INVALID", "Plate profile thicknessMm and widthMm must be positive finite numbers.");
                    return null;
                }
                return new TeklaProfileSpec
                {
                    Kind = kind,
                    ThicknessMm = thickness,
                    WidthMm = width,
                };
            }

            Add(diagnostics, command, "TEKLA_PLAN_PROFILE_INVALID", $"Unsupported beam profile kind '{kind}'.");
            return null;
        }

        private static TeklaMaterialSpec? ParseMaterial(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TryGetObject(command, "material", diagnostics, out var value)) return null;
            if (!TryGetRequiredString(value, "id", out var id))
            {
                Add(diagnostics, command, "TEKLA_PLAN_MATERIAL_INVALID", "Beam material.id is required.");
                return null;
            }
            if (!TryGetOptionalString(value, "name", out var name) ||
                !TryGetOptionalString(value, "grade", out var grade) ||
                !TryGetOptionalString(value, "standard", out var standard))
            {
                Add(diagnostics, command, "TEKLA_PLAN_MATERIAL_INVALID", "Optional beam material fields must be strings.");
                return null;
            }
            return new TeklaMaterialSpec { Id = id, Name = name, Grade = grade, Standard = standard };
        }

        private static TeklaSectionPlacementSpec? ParsePlacement(
            TeklaPlanCommand command,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!command.Payload.TryGetValue("placement", out var value)) return null;
            if (value.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLACEMENT_INVALID", "Beam placement must be an object.");
                return null;
            }
            if (!TryGetRequiredString(value, "anchor", out var anchor) || !PlacementAnchors.Contains(anchor))
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLACEMENT_INVALID", "Beam placement.anchor is not supported.");
                return null;
            }
            if (!TryGetOptionalFiniteNumber(value, "offsetXmm", out var offsetX) ||
                !TryGetOptionalFiniteNumber(value, "offsetYmm", out var offsetY) ||
                !TryGetOptionalFiniteNumber(value, "rotationDeg", out var rotation))
            {
                Add(diagnostics, command, "TEKLA_PLAN_PLACEMENT_INVALID", "Beam placement offsets and rotation must be finite numbers.");
                return null;
            }
            return new TeklaSectionPlacementSpec
            {
                Anchor = anchor,
                OffsetXmm = offsetX,
                OffsetYmm = offsetY,
                RotationDeg = rotation,
            };
        }

        private static bool TryGetObject(
            TeklaPlanCommand command,
            string propertyName,
            ICollection<TeklaPlanValidationDiagnostic> diagnostics,
            out JsonElement value)
        {
            if (command.Payload.TryGetValue(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
                return true;
            Add(diagnostics, command, "TEKLA_PLAN_PAYLOAD_OBJECT_REQUIRED", $"Payload '{propertyName}' must be an object.");
            return false;
        }

        private static bool TryGetRequiredString(JsonElement value, string propertyName, out string result)
        {
            result = string.Empty;
            return value.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(result = property.GetString() ?? string.Empty);
        }

        private static bool TryGetOptionalString(JsonElement value, string propertyName, out string? result)
        {
            result = null;
            if (!value.TryGetProperty(propertyName, out var property)) return true;
            if (property.ValueKind != JsonValueKind.String) return false;
            result = property.GetString();
            return true;
        }

        private static bool TryGetPositiveNumber(JsonElement value, string propertyName, out double result)
        {
            result = 0;
            return value.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetDouble(out result) &&
                   IsFinite(result) &&
                   result > 0;
        }

        private static bool TryGetInteger(JsonElement value, string propertyName, int minimum, out int result)
        {
            result = 0;
            return value.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetInt32(out result) &&
                   result >= minimum;
        }

        private static bool TryGetOptionalFiniteNumber(JsonElement value, string propertyName, out double? result)
        {
            result = null;
            if (!value.TryGetProperty(propertyName, out var property)) return true;
            if (property.ValueKind != JsonValueKind.Number ||
                !property.TryGetDouble(out var number) ||
                !IsFinite(number)) return false;
            result = number;
            return true;
        }

        private static double SquaredDistance(TeklaVector3 left, TeklaVector3 right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            var dz = left.Z - right.Z;
            return (dx * dx) + (dy * dy) + (dz * dz);
        }

        private static double SquaredDistance(TeklaVector2 left, TeklaVector2 right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return (dx * dx) + (dy * dy);
        }

        private static bool IsNonCollinear(TeklaVector3 start, TeklaVector3 pointOnArc, TeklaVector3 end)
        {
            var first = new TeklaVector3(pointOnArc.X - start.X, pointOnArc.Y - start.Y, pointOnArc.Z - start.Z);
            var second = new TeklaVector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
            var cross = Cross(first, second);
            var scale = Math.Max(Length(first) * Length(second), 1d);
            return Length(cross) > 1e-9 * scale;
        }

        private static double Length(TeklaVector3 value)
            => Math.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));

        private static double SquaredLength(TeklaVector3 value)
            => (value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z);

        private static double Dot(TeklaVector3 left, TeklaVector3 right)
            => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

        private static TeklaVector3 Cross(TeklaVector3 left, TeklaVector3 right)
            => new(
                (left.Y * right.Z) - (left.Z * right.Y),
                (left.Z * right.X) - (left.X * right.Z),
                (left.X * right.Y) - (left.Y * right.X));

        private static TeklaVector3 Add(TeklaVector3 left, TeklaVector3 right)
            => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        private static TeklaVector3 Subtract(TeklaVector3 left, TeklaVector3 right)
            => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        private static TeklaVector3? Normalize(TeklaVector3 value)
        {
            var length = Length(value);
            return length > 1e-9 ? Scale(value, 1d / length) : null;
        }

        private static TeklaVector3? RotateShortest(TeklaVector3 value, TeklaVector3 from, TeklaVector3 to)
        {
            var cosine = Math.Max(-1d, Math.Min(1d, Dot(from, to)));
            if (cosine < -1d + 1e-9) return null;
            var rawAxis = Cross(from, to);
            var sine = Length(rawAxis);
            if (sine <= 1e-9) return value;
            var axis = Scale(rawAxis, 1d / sine);
            return Add(
                Add(Scale(value, cosine), Scale(Cross(axis, value), sine)),
                Scale(axis, Dot(axis, value) * (1d - cosine)));
        }

        private static bool VectorNear(TeklaVector3 left, TeklaVector3 right)
            => Math.Sqrt(SquaredDistance(left, right)) <= 1e-6;

        private static TeklaVector3 Scale(TeklaVector3 value, double factor)
            => new(value.X * factor, value.Y * factor, value.Z * factor);

        private static bool NearlyEqual(double left, double right)
            => Math.Abs(left - right) <= 1e-9 * Math.Max(1d, Math.Max(Math.Abs(left), Math.Abs(right)));

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void Add(
            ICollection<TeklaPlanValidationDiagnostic> diagnostics,
            TeklaPlanCommand command,
            string code,
            string message)
            => diagnostics.Add(new TeklaPlanValidationDiagnostic(code, message, command.CommandId));
    }
}
