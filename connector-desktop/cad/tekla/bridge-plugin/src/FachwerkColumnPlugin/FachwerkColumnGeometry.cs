#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

internal static partial class FachwerkColumnGeometry
{
    internal const string DirectOffsetSchema = "DIRECT_V1";
    private const double Epsilon = 1e-7;
    private const double PointTolerance = 1e-4;
    private const double TangencyTolerance = 2.0;

    public static FachwerkColumnFrame ResolveFrame(List<PluginBase.InputDefinition> input, double rotationDeg)
    {
        if (input == null || input.Count == 0)
            throw new InvalidOperationException("Компоненту нужны точка вставки и направление локальной оси X.");
        var values = input[0].GetInput() as ArrayList;
        if (values == null || values.Count < 2 || !(values[0] is Point insertion) || !(values[1] is Point orientation))
            throw new InvalidOperationException("Не удалось восстановить точку вставки или направление компонента.");

        return ResolveFrame(insertion, orientation, rotationDeg);
    }

    internal static FachwerkColumnFrame ResolveFrame(Point insertion, Point orientation, double rotationDeg)
    {
        var dx = orientation.X - insertion.X;
        var dy = orientation.Y - insertion.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < Epsilon)
            throw new InvalidOperationException("Точка направления должна отличаться от точки вставки.");

        if (double.IsNaN(rotationDeg) || double.IsInfinity(rotationDeg) || rotationDeg <= -1_000_000)
            rotationDeg = 0;
        var angle = rotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var axisX = new FachwerkVector(
            (dx / length) * cos - (dy / length) * sin,
            (dx / length) * sin + (dy / length) * cos,
            0);
        return new FachwerkColumnFrame(insertion, axisX);
    }

    public static IReadOnlyList<FachwerkColumnBreak> ReadBreaks(FachwerkColumnPluginData data)
    {
        if (data == null)
            return Array.Empty<FachwerkColumnBreak>();

        var raw = new[]
        {
            new FachwerkColumnBreak(data.Break1Elevation, data.Break1Mode, 1),
            new FachwerkColumnBreak(data.Break2Elevation, data.Break2Mode, 2),
            new FachwerkColumnBreak(data.Break3Elevation, data.Break3Mode, 3),
            new FachwerkColumnBreak(data.Break4Elevation, data.Break4Mode, 4),
            new FachwerkColumnBreak(data.Break5Elevation, data.Break5Mode, 5),
            new FachwerkColumnBreak(data.Break6Elevation, data.Break6Mode, 6),
            new FachwerkColumnBreak(data.Break7Elevation, data.Break7Mode, 7),
            new FachwerkColumnBreak(data.Break8Elevation, data.Break8Mode, 8),
        };
        var useDirectOffsets = string.Equals(
            data.OffsetSchema,
            DirectOffsetSchema,
            StringComparison.Ordinal);
        var flangeLowerOffsets = useDirectOffsets
            ? NormalizeOffsetValues(new[]
            {
                data.Break1FlangeLowerOffset,
                data.Break2FlangeLowerOffset,
                data.Break3FlangeLowerOffset,
                data.Break4FlangeLowerOffset,
                data.Break5FlangeLowerOffset,
                data.Break6FlangeLowerOffset,
                data.Break7FlangeLowerOffset,
                data.Break8FlangeLowerOffset,
            })
            : ParseOffsetValues(data.FlangeLowerOffsets);
        var flangeUpperOffsets = useDirectOffsets
            ? NormalizeOffsetValues(new[]
            {
                data.Break1FlangeUpperOffset,
                data.Break2FlangeUpperOffset,
                data.Break3FlangeUpperOffset,
                data.Break4FlangeUpperOffset,
                data.Break5FlangeUpperOffset,
                data.Break6FlangeUpperOffset,
                data.Break7FlangeUpperOffset,
                data.Break8FlangeUpperOffset,
            })
            : ParseOffsetValues(data.FlangeUpperOffsets);
        var webLowerOffsets = useDirectOffsets
            ? NormalizeOffsetValues(new[]
            {
                data.Break1WebLowerOffset,
                data.Break2WebLowerOffset,
                data.Break3WebLowerOffset,
                data.Break4WebLowerOffset,
                data.Break5WebLowerOffset,
                data.Break6WebLowerOffset,
                data.Break7WebLowerOffset,
                data.Break8WebLowerOffset,
            })
            : ParseOffsetValues(data.WebLowerOffsets);
        var webUpperOffsets = useDirectOffsets
            ? NormalizeOffsetValues(new[]
            {
                data.Break1WebUpperOffset,
                data.Break2WebUpperOffset,
                data.Break3WebUpperOffset,
                data.Break4WebUpperOffset,
                data.Break5WebUpperOffset,
                data.Break6WebUpperOffset,
                data.Break7WebUpperOffset,
                data.Break8WebUpperOffset,
            })
            : ParseOffsetValues(data.WebUpperOffsets);
        var result = new List<FachwerkColumnBreak>();
        for (var index = 0; index < raw.Length; index++)
        {
            var item = raw[index];
            if (double.IsNaN(item.Elevation) ||
                double.IsInfinity(item.Elevation) ||
                item.Elevation <= -1_000_000 ||
                Math.Abs(item.Elevation) <= Epsilon)
            {
                continue;
            }
            result.Add(item.WithOffsets(
                flangeLowerOffsets[index],
                flangeUpperOffsets[index],
                webLowerOffsets[index],
                webUpperOffsets[index]));
        }
        SortBreaksByElevation(result);
        return result;
    }

    /// <summary>
    /// Produces physical path pieces. Source line/arc primitives stay together
    /// until an explicit break elevation cuts the complete path.
    /// </summary>
    public static IReadOnlyList<FachwerkColumnPathPiece> BuildPieces(
        FachwerkColumnPathDefinition path,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks)
    {
        var primitiveCount = path.Primitives.Count;
        var locations = new List<FachwerkColumnSplitLocation>
        {
            new FachwerkColumnSplitLocation(0, 0),
            new FachwerkColumnSplitLocation(primitiveCount, 0),
        };

        var role = path.NormalizedRole();
        foreach (var item in breaks)
        {
            if (item.IsStiffenerReference()) continue;
            if (role == "outer-flange" && item.IsSectionTransition()) continue;
            var localY = item.Elevation - frame.GlobalInsertionZ;
            for (var primitiveIndex = 0; primitiveIndex < primitiveCount; primitiveIndex++)
            {
                var primitive = path.Primitives[primitiveIndex];
                if (!TryGetParameterAtY(primitive, localY, out var parameter)) continue;
                parameter = Math.Max(0, Math.Min(1, parameter));
                var location = primitiveIndex + parameter;
                if (location <= Epsilon || location >= primitiveCount - Epsilon) continue;
                locations.Add(new FachwerkColumnSplitLocation(location, item.Index));
            }
        }

        SortLocations(locations);
        var ordered = MergeLocations(locations);
        var result = new List<FachwerkColumnPathPiece>();
        for (var index = 1; index < ordered.Count; index++)
        {
            var from = ordered[index - 1].Location;
            var to = ordered[index].Location;
            if (to - from < Epsilon) continue;
            var primitives = SlicePrimitives(path.Primitives, from, to);
            if (primitives.Count == 0) continue;
            result.Add(new FachwerkColumnPathPiece(
                path,
                primitives,
                ordered[index].BreakIndex,
                0));
        }
        return result;
    }

    internal static double[] ParseOffsetValues(string serialized)
    {
        var result = new double[8];
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return result;
        }
        var values = serialized.Split(new[] { ';' }, StringSplitOptions.None);
        for (var index = 0; index < result.Length && index < values.Length; index++)
        {
            if (!double.TryParse(
                    values[index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) ||
                double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= -1_000_000)
            {
                value = 0;
            }
            result[index] = value;
        }
        return result;
    }

    private static double[] NormalizeOffsetValues(
        IReadOnlyList<double> values)
    {
        var result = new double[8];
        for (var index = 0; index < result.Length; index++)
        {
            var value = values != null && index < values.Count
                ? values[index]
                : 0;
            result[index] =
                double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= -1_000_000
                    ? 0
                    : value;
        }
        return result;
    }

    internal static string SerializeOffsetValues(
        IReadOnlyList<double> values)
    {
        var normalized = new double[8];
        for (var index = 0; index < normalized.Length; index++)
        {
            var value = values != null && index < values.Count
                ? values[index]
                : 0;
            normalized[index] =
                double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= -1_000_000
                    ? 0
                    : value;
        }
        if (Array.TrueForAll(normalized, value => Math.Abs(value) <= Epsilon))
        {
            return string.Empty;
        }
        return string.Join(
            ";",
            Array.ConvertAll(
                normalized,
                value => value.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    public static Point ToWorld(FachwerkColumnFrame frame, FachwerkColumnLocalPoint point, double normalOffset) => new(
        frame.Insertion.X + frame.AxisX.X * point.X - frame.AxisX.Y * normalOffset,
        frame.Insertion.Y + frame.AxisX.Y * point.X + frame.AxisX.X * normalOffset,
        frame.Insertion.Z + point.Y);

    internal static IReadOnlyList<FachwerkColumnContourVertex> BuildContourVertices(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives)
    {
        if (primitives == null || primitives.Count == 0)
            return Array.Empty<FachwerkColumnContourVertex>();

        var vertices = new List<FachwerkColumnContourVertex>();
        AddVertex(vertices, primitives[0].Start, FachwerkColumnChamferKind.None, 0);

        var index = 0;
        while (index < primitives.Count)
        {
            if (index + 2 < primitives.Count &&
                primitives[index].NormalizedKind() == "line" &&
                primitives[index + 1].NormalizedKind() == "arc" &&
                primitives[index + 2].NormalizedKind() == "line" &&
                TryCreateRoundedCorner(
                    primitives[index],
                    primitives[index + 1],
                    primitives[index + 2],
                    out var corner,
                    out var radius))
            {
                AddVertex(vertices, corner, FachwerkColumnChamferKind.Rounding, radius);
                index += 2;
                continue;
            }

            var primitive = primitives[index];
            if (primitive.NormalizedKind() == "line")
            {
                AddVertex(vertices, primitive.End, FachwerkColumnChamferKind.None, 0);
            }
            else
            {
                // A piece can start or end inside an arc after an explicit
                // elevation break. Three exact points retain that partial arc.
                AddVertex(vertices, PointAt(primitive, 0.5), FachwerkColumnChamferKind.ArcPoint, 0);
                AddVertex(vertices, primitive.End, FachwerkColumnChamferKind.None, 0);
            }
            index++;
        }

        return vertices;
    }

    private static Chamfer CreateChamfer(FachwerkColumnContourVertex vertex)
    {
        switch (vertex.ChamferKind)
        {
            case FachwerkColumnChamferKind.Rounding:
                return new Chamfer(vertex.Radius, 0, Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING);
            case FachwerkColumnChamferKind.ArcPoint:
                return new Chamfer(0, 0, Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT);
            default:
                return new Chamfer(0, 0, Chamfer.ChamferTypeEnum.CHAMFER_NONE);
        }
    }

    private static IReadOnlyList<FachwerkColumnPrimitiveDefinition> SlicePrimitives(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double from,
        double to)
    {
        var result = new List<FachwerkColumnPrimitiveDefinition>();
        for (var index = 0; index < primitives.Count; index++)
        {
            var intervalStart = Math.Max(from, index);
            var intervalEnd = Math.Min(to, index + 1.0);
            if (intervalEnd - intervalStart < Epsilon) continue;
            var startParameter = intervalStart - index;
            var endParameter = intervalEnd - index;
            result.Add(SlicePrimitive(primitives[index], startParameter, endParameter));
        }
        return result;
    }

    private static FachwerkColumnPrimitiveDefinition SlicePrimitive(
        FachwerkColumnPrimitiveDefinition primitive,
        double startParameter,
        double endParameter)
    {
        var result = new FachwerkColumnPrimitiveDefinition
        {
            Kind = primitive.NormalizedKind(),
            Start = PointAt(primitive, startParameter),
            End = PointAt(primitive, endParameter),
        };
        if (primitive.NormalizedKind() == "arc")
        {
            result.Center = primitive.Center.Clone();
            result.SweepDeg = primitive.SweepDeg * (endParameter - startParameter);
        }
        return result;
    }

    private static bool TryCreateRoundedCorner(
        FachwerkColumnPrimitiveDefinition incoming,
        FachwerkColumnPrimitiveDefinition arc,
        FachwerkColumnPrimitiveDefinition outgoing,
        out FachwerkColumnLocalPoint corner,
        out double radius)
    {
        corner = null;
        radius = Distance(arc.Start, arc.Center);
        if (radius < Epsilon) return false;
        if (!AreClose(incoming.End, arc.Start) || !AreClose(arc.End, outgoing.Start)) return false;
        if (!TryIntersectLines(incoming.Start, incoming.End, outgoing.Start, outgoing.End, out corner)) return false;

        var incomingDistance = DistancePointToLine(arc.Center, incoming.Start, incoming.End);
        var outgoingDistance = DistancePointToLine(arc.Center, outgoing.Start, outgoing.End);
        var tolerance = Math.Max(TangencyTolerance, radius * 1e-5);
        if (Math.Abs(incomingDistance - radius) > tolerance ||
            Math.Abs(outgoingDistance - radius) > tolerance)
        {
            corner = null;
            return false;
        }
        return true;
    }

    private static bool TryIntersectLines(
        FachwerkColumnLocalPoint firstStart,
        FachwerkColumnLocalPoint firstEnd,
        FachwerkColumnLocalPoint secondStart,
        FachwerkColumnLocalPoint secondEnd,
        out FachwerkColumnLocalPoint intersection)
    {
        intersection = null;
        var firstX = firstEnd.X - firstStart.X;
        var firstY = firstEnd.Y - firstStart.Y;
        var secondX = secondEnd.X - secondStart.X;
        var secondY = secondEnd.Y - secondStart.Y;
        var determinant = Cross(firstX, firstY, secondX, secondY);
        if (Math.Abs(determinant) < Epsilon) return false;
        var offsetX = secondStart.X - firstStart.X;
        var offsetY = secondStart.Y - firstStart.Y;
        var parameter = Cross(offsetX, offsetY, secondX, secondY) / determinant;
        intersection = new FachwerkColumnLocalPoint
        {
            X = firstStart.X + firstX * parameter,
            Y = firstStart.Y + firstY * parameter,
        };
        return intersection.IsFinite();
    }

    private static double DistancePointToLine(
        FachwerkColumnLocalPoint point,
        FachwerkColumnLocalPoint lineStart,
        FachwerkColumnLocalPoint lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < Epsilon) return double.PositiveInfinity;
        return Math.Abs(Cross(point.X - lineStart.X, point.Y - lineStart.Y, dx, dy)) / length;
    }

    private static void AddVertex(
        ICollection<FachwerkColumnContourVertex> vertices,
        FachwerkColumnLocalPoint point,
        FachwerkColumnChamferKind chamferKind,
        double radius)
    {
        if (vertices is List<FachwerkColumnContourVertex> list && list.Count > 0 &&
            AreClose(list[list.Count - 1].Point, point))
        {
            if (chamferKind != FachwerkColumnChamferKind.None)
                list[list.Count - 1] = new FachwerkColumnContourVertex(point.Clone(), chamferKind, radius);
            return;
        }
        vertices.Add(new FachwerkColumnContourVertex(point.Clone(), chamferKind, radius));
    }

    private static bool TryGetParameterAtY(FachwerkColumnPrimitiveDefinition primitive, double localY, out double parameter)
    {
        parameter = 0;
        if (primitive.NormalizedKind() == "line")
        {
            var dy = primitive.End.Y - primitive.Start.Y;
            if (Math.Abs(dy) < Epsilon) return false;
            parameter = (localY - primitive.Start.Y) / dy;
            return parameter >= -Epsilon && parameter <= 1 + Epsilon;
        }

        var radius = Distance(primitive.Start, primitive.Center);
        if (radius < Epsilon) return false;
        var ratio = (localY - primitive.Center.Y) / radius;
        if (ratio < -1 - Epsilon || ratio > 1 + Epsilon) return false;
        ratio = Math.Max(-1, Math.Min(1, ratio));
        var candidateA = Math.Asin(ratio);
        var candidateB = Math.PI - candidateA;
        return TryMapAngleToParameter(primitive, candidateA, out parameter) ||
            TryMapAngleToParameter(primitive, candidateB, out parameter);
    }

    private static bool TryMapAngleToParameter(FachwerkColumnPrimitiveDefinition primitive, double candidate, out double parameter)
    {
        parameter = 0;
        var startAngle = Math.Atan2(primitive.Start.Y - primitive.Center.Y, primitive.Start.X - primitive.Center.X);
        var sweep = primitive.SweepDeg * Math.PI / 180.0;
        if (Math.Abs(sweep) < Epsilon) return false;
        var delta = sweep > 0
            ? PositiveAngle(candidate - startAngle)
            : -PositiveAngle(startAngle - candidate);
        if (Math.Abs(delta) > Math.Abs(sweep) + 1e-6) return false;
        parameter = delta / sweep;
        return parameter >= -Epsilon && parameter <= 1 + Epsilon;
    }

    private static FachwerkColumnLocalPoint PointAt(FachwerkColumnPrimitiveDefinition primitive, double parameter)
    {
        if (primitive.NormalizedKind() == "line")
        {
            return new FachwerkColumnLocalPoint
            {
                X = primitive.Start.X + (primitive.End.X - primitive.Start.X) * parameter,
                Y = primitive.Start.Y + (primitive.End.Y - primitive.Start.Y) * parameter,
            };
        }

        var startAngle = Math.Atan2(primitive.Start.Y - primitive.Center.Y, primitive.Start.X - primitive.Center.X);
        var angle = startAngle + primitive.SweepDeg * Math.PI / 180.0 * parameter;
        var radius = Distance(primitive.Start, primitive.Center);
        return new FachwerkColumnLocalPoint
        {
            X = primitive.Center.X + radius * Math.Cos(angle),
            Y = primitive.Center.Y + radius * Math.Sin(angle),
        };
    }

    private static string BuildName(string mark, string role, int pieceIndex) =>
        (string.IsNullOrWhiteSpace(mark) ? "FACHWERK" : mark.Trim()) + "." + role + "." + pieceIndex.ToString(CultureInfo.InvariantCulture);

    private static bool AreClose(FachwerkColumnLocalPoint left, FachwerkColumnLocalPoint right) =>
        Distance(left, right) <= PointTolerance;

    private static double Distance(FachwerkColumnLocalPoint left, FachwerkColumnLocalPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Cross(double leftX, double leftY, double rightX, double rightY) =>
        leftX * rightY - leftY * rightX;

    private static double PositiveAngle(double value)
    {
        var normalized = value % (2 * Math.PI);
        return normalized < 0 ? normalized + 2 * Math.PI : normalized;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return null;
        for (var index = 0; index < values.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(values[index])) return values[index];
        }
        return null;
    }

    private static void SortBreaksByElevation(IList<FachwerkColumnBreak> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            var current = values[index];
            var target = index - 1;
            while (target >= 0 && values[target].Elevation > current.Elevation)
            {
                values[target + 1] = values[target];
                target--;
            }
            values[target + 1] = current;
        }
    }

    private static void SortLocations(IList<FachwerkColumnSplitLocation> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            var current = values[index];
            var target = index - 1;
            while (target >= 0 && values[target].Location > current.Location)
            {
                values[target + 1] = values[target];
                target--;
            }
            values[target + 1] = current;
        }
    }

    private static IReadOnlyList<FachwerkColumnSplitLocation> MergeLocations(
        IReadOnlyList<FachwerkColumnSplitLocation> sorted)
    {
        var result = new List<FachwerkColumnSplitLocation>();
        var index = 0;
        while (index < sorted.Count)
        {
            var rounded = Math.Round(sorted[index].Location, 8);
            var selected = sorted[index];
            index++;
            while (index < sorted.Count && Math.Round(sorted[index].Location, 8) == rounded)
            {
                if (sorted[index].BreakIndex > selected.BreakIndex) selected = sorted[index];
                index++;
            }
            result.Add(selected);
        }
        return result;
    }
}

internal sealed class FachwerkColumnFrame
{
    public FachwerkColumnFrame(Point insertion, FachwerkVector axisX, double? globalInsertionZ = null)
    {
        Insertion = insertion;
        AxisX = axisX;
        GlobalInsertionZ = globalInsertionZ ?? insertion.Z;
    }

    public Point Insertion { get; }
    public FachwerkVector AxisX { get; }
    public double GlobalInsertionZ { get; }

    public FachwerkColumnFrame WithGlobalInsertionZ(double globalInsertionZ) =>
        new FachwerkColumnFrame(Insertion, AxisX, globalInsertionZ);
}

internal sealed class FachwerkVector
{
    public FachwerkVector(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
}

internal sealed class FachwerkColumnBreak
{
    public const string AllFourMode = "ALL_FOUR";
    public const string ErectionSpliceMode = "ERECTION_SPLICE";
    public const string SectionTransitionMode = "SECTION_TRANSITION";
    public const string StiffenerReferenceMode = "STIFFENER_REFERENCE";

    public const double ErectionFlangeLowerOffset = -75.0;
    public const double ErectionFlangeUpperOffset = 75.0;
    public const double ErectionWebLowerOffset = -45.0;
    public const double ErectionWebUpperOffset = -75.0;

    public FachwerkColumnBreak(double elevation, string mode, int index)
        : this(elevation, mode, index, 0, 0, 0, 0)
    {
    }

    public FachwerkColumnBreak(
        double elevation,
        string mode,
        int index,
        double flangeLowerOffset,
        double flangeUpperOffset,
        double webLowerOffset,
        double webUpperOffset)
    {
        Elevation = elevation;
        Mode = NormalizeMode(mode);
        Index = index;
        FlangeLowerOffset = NormalizeOffset(flangeLowerOffset);
        FlangeUpperOffset = NormalizeOffset(flangeUpperOffset);
        WebLowerOffset = NormalizeOffset(webLowerOffset);
        WebUpperOffset = NormalizeOffset(webUpperOffset);
    }

    public double Elevation { get; }
    public string Mode { get; }
    public int Index { get; }
    public double FlangeLowerOffset { get; }
    public double FlangeUpperOffset { get; }
    public double WebLowerOffset { get; }
    public double WebUpperOffset { get; }
    public bool IsSectionTransition() => string.Equals(Mode, SectionTransitionMode, StringComparison.OrdinalIgnoreCase);
    public bool IsErectionSplice() => string.Equals(Mode, ErectionSpliceMode, StringComparison.OrdinalIgnoreCase);
    public bool IsStiffenerReference() => string.Equals(Mode, StiffenerReferenceMode, StringComparison.OrdinalIgnoreCase);

    public FachwerkColumnBreak WithOffsets(
        double flangeLowerOffset,
        double flangeUpperOffset,
        double webLowerOffset,
        double webUpperOffset) =>
        new FachwerkColumnBreak(
            Elevation,
            Mode,
            Index,
            flangeLowerOffset,
            flangeUpperOffset,
            webLowerOffset,
            webUpperOffset);

    private static string NormalizeMode(string mode)
    {
        var normalized = (mode ?? AllFourMode).Trim();
        if (string.Equals(normalized, SectionTransitionMode, StringComparison.OrdinalIgnoreCase))
            return SectionTransitionMode;
        if (string.Equals(normalized, ErectionSpliceMode, StringComparison.OrdinalIgnoreCase))
            return ErectionSpliceMode;
        if (string.Equals(normalized, StiffenerReferenceMode, StringComparison.OrdinalIgnoreCase))
            return StiffenerReferenceMode;

        // OUTER_WEB_CONTINUOUS was emitted by the broken intermediate version.
        // Existing components must rebuild as regular four-part rigel cuts.
        return AllFourMode;
    }

    private static double NormalizeOffset(double value) =>
        double.IsNaN(value) ||
        double.IsInfinity(value) ||
        value <= -1_000_000
            ? 0
            : value;
}

internal sealed class FachwerkColumnPathPiece
{
    public FachwerkColumnPathPiece(
        FachwerkColumnPathDefinition path,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        int endBreakIndex,
        int assemblySegmentIndex = 0)
    {
        Path = path;
        Primitives = primitives;
        EndBreakIndex = endBreakIndex;
        AssemblySegmentIndex = assemblySegmentIndex;
    }

    public FachwerkColumnPathDefinition Path { get; }
    public IReadOnlyList<FachwerkColumnPrimitiveDefinition> Primitives { get; }
    public int EndBreakIndex { get; }
    public int AssemblySegmentIndex { get; }
}

internal sealed class FachwerkColumnSplitLocation
{
    public FachwerkColumnSplitLocation(double location, int breakIndex)
    {
        Location = location;
        BreakIndex = breakIndex;
    }

    public double Location { get; }
    public int BreakIndex { get; }
}

internal enum FachwerkColumnChamferKind
{
    None,
    Rounding,
    ArcPoint,
}

internal sealed class FachwerkColumnContourVertex
{
    public FachwerkColumnContourVertex(
        FachwerkColumnLocalPoint point,
        FachwerkColumnChamferKind chamferKind,
        double radius)
    {
        Point = point;
        ChamferKind = chamferKind;
        Radius = radius;
    }

    public FachwerkColumnLocalPoint Point { get; }
    public FachwerkColumnChamferKind ChamferKind { get; }
    public double Radius { get; }
}
