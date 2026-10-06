#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Fachwerk.Geometry;

public readonly record struct FachwerkPoint3(double X, double Y, double Z)
{
    public static FachwerkPoint3 operator +(FachwerkPoint3 point, FachwerkVector3 vector) =>
        new(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);

    public static FachwerkVector3 operator -(FachwerkPoint3 right, FachwerkPoint3 left) =>
        new(right.X - left.X, right.Y - left.Y, right.Z - left.Z);
}

public readonly record struct FachwerkVector3(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public FachwerkVector3 Normalized()
    {
        var length = Length;
        if (length < 1e-9) throw new ArgumentException("Cannot normalize a zero vector.");
        return new FachwerkVector3(X / length, Y / length, Z / length);
    }

    public static FachwerkVector3 operator *(FachwerkVector3 vector, double scale) =>
        new(vector.X * scale, vector.Y * scale, vector.Z * scale);

    public static FachwerkVector3 operator +(FachwerkVector3 left, FachwerkVector3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static double Dot(FachwerkVector3 left, FachwerkVector3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    public static FachwerkVector3 Cross(FachwerkVector3 left, FachwerkVector3 right) =>
        new(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
}

public sealed class FachwerkNodeFrame
{
    private const double OrthogonalTolerance = 1e-7;

    public FachwerkNodeFrame(FachwerkPoint3 origin, FachwerkVector3 facadeAxis, int sideSign = 1)
    {
        if (sideSign != -1 && sideSign != 1)
            throw new ArgumentOutOfRangeException(nameof(sideSign), "sideSign must be -1 or 1.");

        Origin = origin;
        AxisX = new FachwerkVector3(facadeAxis.X, facadeAxis.Y, 0).Normalized();
        AxisZ = new FachwerkVector3(0, 0, 1);
        AxisN = FachwerkVector3.Cross(AxisZ, AxisX).Normalized() * sideSign;

        if (Math.Abs(FachwerkVector3.Dot(AxisX, AxisN)) > OrthogonalTolerance ||
            Math.Abs(FachwerkVector3.Dot(AxisX, AxisZ)) > OrthogonalTolerance ||
            Math.Abs(FachwerkVector3.Dot(AxisN, AxisZ)) > OrthogonalTolerance)
        {
            throw new ArgumentException("The Fachwerk node frame is not orthogonal.");
        }
    }

    public FachwerkPoint3 Origin { get; }
    public FachwerkVector3 AxisX { get; }
    public FachwerkVector3 AxisN { get; }
    public FachwerkVector3 AxisZ { get; }

    public FachwerkPoint3 ToGlobal(FachwerkLocalPoint point) =>
        Origin + AxisX * point.X + AxisN * point.N + AxisZ * point.Z;
}

public readonly record struct FachwerkLocalPoint(double X, double N, double Z);

public sealed record FachwerkColumnPartRef(
    string ObjectGuid,
    string Role,
    string Geometry,
    int BreakIndex);

public sealed class FachwerkColumnContext
{
    public FachwerkColumnContext(
        string ownerId,
        string mark,
        FachwerkColumnPartRef outerFlange,
        FachwerkColumnPartRef upperInnerFlange,
        FachwerkColumnPartRef lowerInnerFlange,
        FachwerkColumnPartRef upperInnerWeb,
        FachwerkColumnPartRef lowerInnerWeb,
        FachwerkColumnPartRef upperOuterWeb,
        FachwerkColumnPartRef lowerOuterWeb)
    {
        OwnerId = Require(ownerId, nameof(ownerId));
        Mark = Require(mark, nameof(mark));
        OuterFlange = ValidateRole(outerFlange, "outer-flange", nameof(outerFlange));
        UpperInnerFlange = ValidateRole(upperInnerFlange, "inner-flange", nameof(upperInnerFlange));
        LowerInnerFlange = ValidateRole(lowerInnerFlange, "inner-flange", nameof(lowerInnerFlange));
        UpperInnerWeb = ValidateRole(upperInnerWeb, "inner-web", nameof(upperInnerWeb));
        LowerInnerWeb = ValidateRole(lowerInnerWeb, "inner-web", nameof(lowerInnerWeb));
        UpperOuterWeb = ValidateRole(upperOuterWeb, "outer-web", nameof(upperOuterWeb));
        LowerOuterWeb = ValidateRole(lowerOuterWeb, "outer-web", nameof(lowerOuterWeb));

        var parts = Parts.ToArray();
        if (parts.Select(static part => part.ObjectGuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Length)
            throw new ArgumentException("Each required Fachwerk column role must resolve to a distinct object.");
    }

    public string OwnerId { get; }
    public string Mark { get; }
    public FachwerkColumnPartRef OuterFlange { get; }
    public FachwerkColumnPartRef UpperInnerFlange { get; }
    public FachwerkColumnPartRef LowerInnerFlange { get; }
    public FachwerkColumnPartRef UpperInnerWeb { get; }
    public FachwerkColumnPartRef LowerInnerWeb { get; }
    public FachwerkColumnPartRef UpperOuterWeb { get; }
    public FachwerkColumnPartRef LowerOuterWeb { get; }

    public IEnumerable<FachwerkColumnPartRef> Parts
    {
        get
        {
            yield return OuterFlange;
            yield return UpperInnerFlange;
            yield return LowerInnerFlange;
            yield return UpperInnerWeb;
            yield return LowerInnerWeb;
            yield return UpperOuterWeb;
            yield return LowerOuterWeb;
        }
    }

    private static FachwerkColumnPartRef ValidateRole(
        FachwerkColumnPartRef? part,
        string expectedRole,
        string parameterName)
    {
        if (part is null || string.IsNullOrWhiteSpace(part.ObjectGuid) ||
            !string.Equals(part.Role?.Trim(), expectedRole, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{parameterName} must resolve role '{expectedRole}'.", parameterName);
        }
        return part;
    }

    private static string Require(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value!.Trim();
}

public sealed record FachwerkSectionSample(double ElevationMm, double OuterFlangeCenterXmm);

public sealed class FachwerkNodeInput
{
    public string NodeId { get; init; } = string.Empty;
    public double TopElevationMm { get; init; }
    public double HeightMm { get; init; }
    public FachwerkNodeFrame Frame { get; init; } = null!;
    public double TopInnerFlangeCenterXmm { get; init; }
    public double BottomInnerFlangeCenterXmm { get; init; }
    public IReadOnlyList<FachwerkSectionSample> OuterFlangeSamples { get; init; } =
        Array.Empty<FachwerkSectionSample>();
    public double ProjectionFromBottomInnerFlangeMm { get; init; } = 220;
    public double ClosureOffsetFromFreeEdgeMm { get; init; } = 143;
    public double HoleOffsetFromFreeEdgeMm { get; init; } = 42.5;
    public string Material { get; init; } = "C355-5";
    public string ClassName { get; init; } = "3";
}

public sealed record FachwerkContourPartSpec(
    string Role,
    string Profile,
    string Material,
    string ClassName,
    IReadOnlyList<FachwerkLocalPoint> Points);

public sealed record FachwerkBeamPartSpec(
    string Role,
    string Profile,
    string Material,
    string ClassName,
    FachwerkLocalPoint Start,
    FachwerkLocalPoint End);

public sealed record FachwerkHoleSpec(FachwerkLocalPoint Center, double DiameterMm);

public sealed record FachwerkColumnFitSpec(
    string Role,
    string ColumnPartRole,
    double ElevationMm,
    bool KeepAbove);

public sealed class FachwerkNodeSpec
{
    public FachwerkNodeSpec(
        string nodeId,
        double topElevationMm,
        double bottomElevationMm,
        FachwerkNodeFrame frame,
        IReadOnlyList<FachwerkContourPartSpec> contourParts,
        IReadOnlyList<FachwerkBeamPartSpec> beamParts,
        IReadOnlyList<FachwerkHoleSpec> holes,
        IReadOnlyList<FachwerkColumnFitSpec> columnFits,
        string signature)
    {
        NodeId = nodeId;
        TopElevationMm = topElevationMm;
        BottomElevationMm = bottomElevationMm;
        Frame = frame;
        ContourParts = contourParts;
        BeamParts = beamParts;
        Holes = holes;
        ColumnFits = columnFits;
        Signature = signature;
    }

    public string NodeId { get; }
    public double TopElevationMm { get; }
    public double BottomElevationMm { get; }
    public FachwerkNodeFrame Frame { get; }
    public IReadOnlyList<FachwerkContourPartSpec> ContourParts { get; }
    public IReadOnlyList<FachwerkBeamPartSpec> BeamParts { get; }
    public IReadOnlyList<FachwerkHoleSpec> Holes { get; }
    public IReadOnlyList<FachwerkColumnFitSpec> ColumnFits { get; }
    public string Signature { get; }

    public IEnumerable<string> Roles =>
        ContourParts.Select(static part => part.Role).Concat(BeamParts.Select(static part => part.Role));
}

public static class FachwerkNodeGeometry
{
    private const double FlangeHalfThickness = 25;
    private const double WebNormalOffset = 77.5;
    private const double DiagonalNormalOffset = 50;
    private const double PlateOuterNormal = 90;
    private const double MainWebHalfThickness = 10;
    private const double TopInset = 10;
    private const double BottomInset = 30;
    private const double SampleTolerance = 0.1;

    private static readonly IReadOnlyDictionary<int, double[]> HoleOffsetsByHeight =
        new Dictionary<int, double[]>
        {
            [520] = new[] { 90d, 175d, 260d, 345d, 430d },
            [400] = new[] { 90d, 175d, 260d, 345d },
        };

    public static FachwerkNodeSpec Build(FachwerkNodeInput input)
    {
        Validate(input);
        var heightKey = (int)Math.Round(input.HeightMm, MidpointRounding.AwayFromZero);
        var bottomElevation = input.TopElevationMm - input.HeightMm;
        var topZ = input.TopElevationMm - input.Frame.Origin.Z;
        var bottomZ = bottomElevation - input.Frame.Origin.Z;
        var direction = Direction(input);
        var freeEdgeX = input.BottomInnerFlangeCenterXmm -
            direction * input.ProjectionFromBottomInnerFlangeMm;
        var topInnerFaceX = input.TopInnerFlangeCenterXmm + direction * FlangeHalfThickness;
        var bottomInnerFaceX = input.BottomInnerFlangeCenterXmm + direction * FlangeHalfThickness;

        var outerPath = input.OuterFlangeSamples
            .OrderByDescending(static sample => sample.ElevationMm)
            .Select(sample => new FachwerkLocalPoint(
                sample.OuterFlangeCenterXmm - direction * FlangeHalfThickness,
                0,
                sample.ElevationMm - input.Frame.Origin.Z))
            .ToArray();

        var mainContour = new List<FachwerkLocalPoint>
        {
            new(freeEdgeX, 0, topZ - TopInset),
        };
        mainContour.AddRange(outerPath);
        mainContour.Add(new FachwerkLocalPoint(freeEdgeX, 0, bottomZ + BottomInset));

        var sidePositive = BuildSideContour(
            WebNormalOffset,
            topInnerFaceX,
            bottomInnerFaceX,
            topZ,
            bottomZ,
            outerPath);
        var sideNegative = BuildSideContour(
            -WebNormalOffset,
            topInnerFaceX,
            bottomInnerFaceX,
            topZ,
            bottomZ,
            outerPath);
        var closureX = freeEdgeX + direction * input.ClosureOffsetFromFreeEdgeMm;

        var contours = new[]
        {
            Part("main-web", "PL20", input, mainContour),
            Part("side-plate-left", "PL25", input, sidePositive),
            Part("side-plate-right", "PL25", input, sideNegative),
            Part("closure-left", "PL8", input, new[]
            {
                new FachwerkLocalPoint(closureX, MainWebHalfThickness, bottomZ + BottomInset),
                new FachwerkLocalPoint(closureX, PlateOuterNormal, bottomZ + BottomInset),
                new FachwerkLocalPoint(closureX, PlateOuterNormal, topZ - TopInset),
                new FachwerkLocalPoint(closureX, MainWebHalfThickness, topZ - TopInset),
            }),
            Part("closure-right", "PL8", input, new[]
            {
                new FachwerkLocalPoint(closureX, -MainWebHalfThickness, bottomZ + BottomInset),
                new FachwerkLocalPoint(closureX, -PlateOuterNormal, bottomZ + BottomInset),
                new FachwerkLocalPoint(closureX, -PlateOuterNormal, topZ - TopInset),
                new FachwerkLocalPoint(closureX, -MainWebHalfThickness, topZ - TopInset),
            }),
        };

        var outerTopCenterX = SampleAt(input.OuterFlangeSamples, input.TopElevationMm);
        var outerBottomCenterX = SampleAt(input.OuterFlangeSamples, bottomElevation);
        var beams = new[]
        {
            Beam("top-plate", "PL20*180", input,
                new FachwerkLocalPoint(freeEdgeX, 0, topZ - TopInset),
                new FachwerkLocalPoint(outerTopCenterX, 0, topZ - TopInset)),
            Beam("bottom-plate", "PL20*180", input,
                new FachwerkLocalPoint(freeEdgeX, 0, bottomZ + TopInset),
                new FachwerkLocalPoint(outerBottomCenterX, 0, bottomZ + TopInset)),
            Beam("diagonal-left", "PL50*80", input,
                new FachwerkLocalPoint(bottomInnerFaceX, DiagonalNormalOffset, bottomZ + BottomInset),
                new FachwerkLocalPoint(topInnerFaceX, DiagonalNormalOffset, topZ - TopInset)),
            Beam("diagonal-right", "PL50*80", input,
                new FachwerkLocalPoint(bottomInnerFaceX, -DiagonalNormalOffset, bottomZ + BottomInset),
                new FachwerkLocalPoint(topInnerFaceX, -DiagonalNormalOffset, topZ - TopInset)),
        };

        var holes = HoleOffsetsByHeight[heightKey]
            .Select(offset => new FachwerkHoleSpec(
                new FachwerkLocalPoint(
                    freeEdgeX + direction * input.HoleOffsetFromFreeEdgeMm,
                    -MainWebHalfThickness,
                    topZ - offset),
                24))
            .ToArray();
        var columnFits = new[]
        {
            new FachwerkColumnFitSpec("upper-inner-flange", "inner-flange", input.TopElevationMm, true),
            new FachwerkColumnFitSpec("lower-inner-flange", "inner-flange", bottomElevation, false),
            new FachwerkColumnFitSpec("upper-inner-web", "inner-web", input.TopElevationMm, true),
            new FachwerkColumnFitSpec("lower-inner-web", "inner-web", bottomElevation, false),
            new FachwerkColumnFitSpec("upper-outer-web", "outer-web", input.TopElevationMm, true),
            new FachwerkColumnFitSpec("lower-outer-web", "outer-web", bottomElevation, false),
        };
        var signature = Signature(input, bottomElevation, contours, beams, holes, columnFits);
        return new FachwerkNodeSpec(
            input.NodeId.Trim(),
            input.TopElevationMm,
            bottomElevation,
            input.Frame,
            contours,
            beams,
            holes,
            columnFits,
            signature);
    }

    private static FachwerkContourPartSpec Part(
        string role,
        string profile,
        FachwerkNodeInput input,
        IReadOnlyList<FachwerkLocalPoint> points) =>
        new(role, profile, input.Material.Trim(), input.ClassName.Trim(), points);

    private static FachwerkBeamPartSpec Beam(
        string role,
        string profile,
        FachwerkNodeInput input,
        FachwerkLocalPoint start,
        FachwerkLocalPoint end) =>
        new(role, profile, input.Material.Trim(), input.ClassName.Trim(), start, end);

    private static IReadOnlyList<FachwerkLocalPoint> BuildSideContour(
        double normal,
        double topInnerFaceX,
        double bottomInnerFaceX,
        double topZ,
        double bottomZ,
        IReadOnlyList<FachwerkLocalPoint> outerPath)
    {
        var result = new List<FachwerkLocalPoint>
        {
            new(topInnerFaceX, normal, topZ - TopInset),
        };
        result.AddRange(outerPath.Select(point => new FachwerkLocalPoint(point.X, normal, point.Z)));
        result.Add(new FachwerkLocalPoint(bottomInnerFaceX, normal, bottomZ + BottomInset));
        return result;
    }

    private static void Validate(FachwerkNodeInput input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (string.IsNullOrWhiteSpace(input.NodeId))
            throw new ArgumentException("nodeId is required.", nameof(input));
        if (input.Frame is null)
            throw new ArgumentException("frame is required.", nameof(input));
        if (!Finite(input.TopElevationMm) || !Finite(input.HeightMm) || input.HeightMm <= 0)
            throw new ArgumentException("Top elevation and height must be finite positive values.", nameof(input));

        var heightKey = (int)Math.Round(input.HeightMm, MidpointRounding.AwayFromZero);
        if (Math.Abs(input.HeightMm - heightKey) > SampleTolerance || !HoleOffsetsByHeight.ContainsKey(heightKey))
            throw new ArgumentException("The first node revision supports explicit 520 mm and 400 mm presets.", nameof(input));
        if (input.OuterFlangeSamples is null || input.OuterFlangeSamples.Count < 2)
            throw new ArgumentException("At least two outer-flange samples are required.", nameof(input));
        if (input.OuterFlangeSamples.Any(static sample =>
                !Finite(sample.ElevationMm) || !Finite(sample.OuterFlangeCenterXmm)))
            throw new ArgumentException("Outer-flange samples must be finite.", nameof(input));
        if (!Finite(input.TopInnerFlangeCenterXmm) || !Finite(input.BottomInnerFlangeCenterXmm) ||
            !Finite(input.ProjectionFromBottomInnerFlangeMm) || input.ProjectionFromBottomInnerFlangeMm <= 0 ||
            !Finite(input.ClosureOffsetFromFreeEdgeMm) || input.ClosureOffsetFromFreeEdgeMm <= 0 ||
            !Finite(input.HoleOffsetFromFreeEdgeMm) || input.HoleOffsetFromFreeEdgeMm <= 0 ||
            string.IsNullOrWhiteSpace(input.Material) || string.IsNullOrWhiteSpace(input.ClassName))
        {
            throw new ArgumentException("The node section and material parameters are invalid.", nameof(input));
        }

        var bottom = input.TopElevationMm - input.HeightMm;
        var ordered = input.OuterFlangeSamples.OrderBy(static sample => sample.ElevationMm).ToArray();
        if (ordered[0].ElevationMm > bottom + SampleTolerance ||
            ordered[ordered.Length - 1].ElevationMm < input.TopElevationMm - SampleTolerance)
        {
            throw new ArgumentException("Outer-flange samples must cover the complete node height.", nameof(input));
        }
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].ElevationMm - ordered[index - 1].ElevationMm < SampleTolerance)
                throw new ArgumentException("Outer-flange sample elevations must be unique.", nameof(input));
        }
    }

    private static double Direction(FachwerkNodeInput input)
    {
        var outerAtBottom = SampleAt(
            input.OuterFlangeSamples,
            input.TopElevationMm - input.HeightMm);
        var delta = outerAtBottom - input.BottomInnerFlangeCenterXmm;
        if (Math.Abs(delta) < 50)
            throw new ArgumentException("Inner and outer flanges are not separated by a valid section depth.", nameof(input));
        return Math.Sign(delta);
    }

    private static double SampleAt(IReadOnlyList<FachwerkSectionSample> samples, double elevation)
    {
        var ordered = samples.OrderBy(static sample => sample.ElevationMm).ToArray();
        if (elevation <= ordered[0].ElevationMm + SampleTolerance)
            return ordered[0].OuterFlangeCenterXmm;
        if (elevation >= ordered[ordered.Length - 1].ElevationMm - SampleTolerance)
            return ordered[ordered.Length - 1].OuterFlangeCenterXmm;
        for (var index = 1; index < ordered.Length; index++)
        {
            var first = ordered[index - 1];
            var second = ordered[index];
            if (elevation > second.ElevationMm + SampleTolerance) continue;
            var factor = (elevation - first.ElevationMm) /
                (second.ElevationMm - first.ElevationMm);
            return first.OuterFlangeCenterXmm +
                (second.OuterFlangeCenterXmm - first.OuterFlangeCenterXmm) * factor;
        }
        throw new ArgumentOutOfRangeException(nameof(elevation));
    }

    private static string Signature(
        FachwerkNodeInput input,
        double bottomElevation,
        IReadOnlyList<FachwerkContourPartSpec> contours,
        IReadOnlyList<FachwerkBeamPartSpec> beams,
        IReadOnlyList<FachwerkHoleSpec> holes,
        IReadOnlyList<FachwerkColumnFitSpec> columnFits)
    {
        var text = new StringBuilder();
        text.Append(input.NodeId.Trim()).Append('|')
            .Append(F(input.TopElevationMm)).Append('|')
            .Append(F(bottomElevation)).Append('|')
            .Append(F(input.Frame.Origin.X)).Append(',').Append(F(input.Frame.Origin.Y)).Append(',').Append(F(input.Frame.Origin.Z)).Append('|')
            .Append(F(input.Frame.AxisX.X)).Append(',').Append(F(input.Frame.AxisX.Y)).Append('|')
            .Append(F(input.Frame.AxisN.X)).Append(',').Append(F(input.Frame.AxisN.Y)).Append('|');
        foreach (var part in contours)
        {
            text.Append(part.Role).Append(':').Append(part.Profile).Append(':');
            foreach (var point in part.Points) AppendPoint(text, point);
        }
        foreach (var part in beams)
        {
            text.Append(part.Role).Append(':').Append(part.Profile).Append(':');
            AppendPoint(text, part.Start);
            AppendPoint(text, part.End);
        }
        foreach (var hole in holes) AppendPoint(text, hole.Center);
        foreach (var fit in columnFits)
        {
            text.Append(fit.Role).Append(':')
                .Append(fit.ColumnPartRole).Append(':')
                .Append(F(fit.ElevationMm)).Append(':')
                .Append(fit.KeepAbove ? 'U' : 'L').Append(';');
        }

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
        return string.Concat(bytes.Take(16).Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static void AppendPoint(StringBuilder target, FachwerkLocalPoint point) =>
        target.Append(F(point.X)).Append(',').Append(F(point.N)).Append(',').Append(F(point.Z)).Append(';');

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
