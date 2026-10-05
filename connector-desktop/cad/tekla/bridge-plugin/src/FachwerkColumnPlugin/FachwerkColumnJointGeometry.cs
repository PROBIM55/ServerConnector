#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Structura.Tekla.Fachwerk;

internal static partial class FachwerkColumnGeometry
{
    private const double JointLocationTolerance = 1e-6;

    internal static IReadOnlyList<FachwerkColumnJointPlane> ResolveJointPlanes(
        FachwerkColumnPathDefinition physicalOuterFlange,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks,
        FachwerkColumnSectionTransitionDefinition transition)
    {
        var userBreaks = (breaks ?? Array.Empty<FachwerkColumnBreak>())
            .Where(item =>
                item != null &&
                !item.IsSectionTransition() &&
                !item.IsStiffenerReference())
            .OrderBy(item => item.Elevation)
            .ThenBy(item => item.Index)
            .ToArray();
        var result = new List<FachwerkColumnJointPlane>(userBreaks.Length);
        for (var index = 0; index < userBreaks.Length; index++)
        {
            var item = userBreaks[index];
            var localY = item.Elevation - frame.GlobalInsertionZ;
            var anchorLocation = RequireUniqueLocationAtY(
                physicalOuterFlange.Primitives,
                localY,
                "наружного пояса",
                item.Index);
            var anchor = PointAtPathLocation(physicalOuterFlange.Primitives, anchorLocation);
            var normal = UpwardTangentAtPathLocation(
                physicalOuterFlange.Primitives,
                anchorLocation);
            var coincidesWithTransition =
                Math.Abs(localY - transition.StartY) <= SectionTransitionTolerance ||
                Math.Abs(localY - transition.EndY) <= SectionTransitionTolerance;
            result.Add(new FachwerkColumnJointPlane(
                item,
                index,
                anchor,
                normal,
                coincidesWithTransition));
        }
        return result;
    }

    internal static IReadOnlyList<FachwerkColumnPathPiece> SplitByJointPlanes(
        FachwerkColumnPathDefinition path,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> sourcePrimitives,
        IReadOnlyList<FachwerkColumnJointPlane> joints,
        bool isWeb,
        int inheritedEndBreakIndex)
    {
        if (sourcePrimitives == null || sourcePrimitives.Count == 0)
            return Array.Empty<FachwerkColumnPathPiece>();

        var applicable = new List<FachwerkColumnJointBoundary>();
        foreach (var joint in joints ?? Array.Empty<FachwerkColumnJointPlane>())
        {
            if (joint.CoincidesWithSectionTransition &&
                !string.Equals(path.NormalizedRole(), "outer-flange", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lowerOffset = isWeb
                ? joint.Break.WebLowerOffset
                : joint.Break.FlangeLowerOffset;
            var upperOffset = isWeb
                ? joint.Break.WebUpperOffset
                : joint.Break.FlangeUpperOffset;
            var hasLower = TryFindNearestLocationAtPlane(
                sourcePrimitives,
                joint,
                lowerOffset,
                out var lowerLocation);
            var hasUpper = TryFindNearestLocationAtPlane(
                sourcePrimitives,
                joint,
                upperOffset,
                out var upperLocation);
            if (!hasLower && !hasUpper)
            {
                continue;
            }
            if (!hasLower || !hasUpper)
            {
                throw new InvalidOperationException(
                    "Стык " + joint.Break.Index +
                    " пересёк только одну из двух смещённых плоскостей роли '" +
                    path.NormalizedRole() + "'. Уменьшите смещение стыка.");
            }
            applicable.Add(new FachwerkColumnJointBoundary(
                joint,
                lowerLocation,
                upperLocation));
        }

        if (applicable.Count == 0)
        {
            return new[]
            {
                new FachwerkColumnPathPiece(
                    path,
                    ClonePrimitives(sourcePrimitives),
                    inheritedEndBreakIndex,
                    ResolveAssemblySegmentIndex(sourcePrimitives, joints)),
            };
        }

        var ascending = IsAscendingPath(sourcePrimitives);
        applicable.Sort((left, right) =>
        {
            var comparison = left.Joint.AssemblyBoundaryIndex.CompareTo(
                right.Joint.AssemblyBoundaryIndex);
            return ascending ? comparison : -comparison;
        });

        var result = new List<FachwerkColumnPathPiece>();
        var cursor = 0.0;
        foreach (var boundary in applicable)
        {
            var pieceEnd = ascending
                ? boundary.LowerLocation
                : boundary.UpperLocation;
            var segmentIndex = ascending
                ? boundary.Joint.AssemblyBoundaryIndex
                : boundary.Joint.AssemblyBoundaryIndex + 1;
            AddJointPiece(
                result,
                path,
                sourcePrimitives,
                cursor,
                pieceEnd,
                boundary.Joint.Break.Index,
                segmentIndex);
            cursor = ascending
                ? boundary.UpperLocation
                : boundary.LowerLocation;
        }

        var finalSegmentIndex = ascending
            ? applicable[applicable.Count - 1].Joint.AssemblyBoundaryIndex + 1
            : applicable[applicable.Count - 1].Joint.AssemblyBoundaryIndex;
        AddJointPiece(
            result,
            path,
            sourcePrimitives,
            cursor,
            sourcePrimitives.Count,
            inheritedEndBreakIndex,
            finalSegmentIndex);
        return result;
    }

    private static void AddJointPiece(
        ICollection<FachwerkColumnPathPiece> result,
        FachwerkColumnPathDefinition path,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> sourcePrimitives,
        double from,
        double to,
        int endBreakIndex,
        int assemblySegmentIndex)
    {
        if (from < -JointLocationTolerance ||
            to > sourcePrimitives.Count + JointLocationTolerance ||
            to - from <= JointLocationTolerance)
        {
            if (Math.Abs(to - from) <= JointLocationTolerance)
            {
                return;
            }
            throw new InvalidOperationException(
                "Смещения стыков инвертировали участок роли '" +
                path.NormalizedRole() + "': from=" + from.ToString("0.######") +
                ", to=" + to.ToString("0.######") + ".");
        }

        var primitives = SlicePrimitives(
            sourcePrimitives,
            Math.Max(0, from),
            Math.Min(sourcePrimitives.Count, to));
        if (primitives.Count == 0)
        {
            return;
        }
        result.Add(new FachwerkColumnPathPiece(
            path,
            primitives,
            endBreakIndex,
            assemblySegmentIndex));
    }

    private static int ResolveAssemblySegmentIndex(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        IReadOnlyList<FachwerkColumnJointPlane> joints)
    {
        var first = primitives[0].Start;
        var last = primitives[primitives.Count - 1].End;
        var middleY = (first.Y + last.Y) * 0.5;
        return (joints ?? Array.Empty<FachwerkColumnJointPlane>())
            .Count(item => item.Anchor.Y < middleY - JointLocationTolerance);
    }

    private static bool IsAscendingPath(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives)
    {
        var first = primitives[0].Start;
        var last = primitives[primitives.Count - 1].End;
        if (Math.Abs(last.Y - first.Y) > JointLocationTolerance)
        {
            return last.Y > first.Y;
        }
        return last.X >= first.X;
    }

    private static IReadOnlyList<FachwerkColumnPrimitiveDefinition> ClonePrimitives(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source)
    {
        var result = new List<FachwerkColumnPrimitiveDefinition>(source.Count);
        foreach (var primitive in source)
        {
            result.Add(SlicePrimitive(primitive, 0, 1));
        }
        return result;
    }

    private static double RequireUniqueLocationAtY(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double localY,
        string role,
        int breakIndex)
    {
        var locations = new List<double>();
        for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            if (!TryGetParameterAtY(primitives[primitiveIndex], localY, out var parameter))
            {
                continue;
            }
            AddUniqueLocation(
                locations,
                primitiveIndex + Math.Max(0, Math.Min(1, parameter)));
        }
        if (locations.Count == 0)
        {
            throw new InvalidOperationException(
                "Отметка стыка " + breakIndex +
                " не пересекает физическую ось " + role + ".");
        }
        if (locations.Count > 1)
        {
            throw new InvalidOperationException(
                "Отметка стыка " + breakIndex +
                " неоднозначно пересекает физическую ось " + role +
                " в " + locations.Count + " точках.");
        }
        return locations[0];
    }

    private static bool TryFindNearestLocationAtPlane(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        FachwerkColumnJointPlane plane,
        double offset,
        out double selectedLocation)
    {
        selectedLocation = 0;
        var candidates = new List<double>();
        for (var primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
        {
            foreach (var parameter in IntersectionsAtPlane(
                primitives[primitiveIndex],
                plane,
                offset))
            {
                AddUniqueLocation(
                    candidates,
                    primitiveIndex + Math.Max(0, Math.Min(1, parameter)));
            }
        }
        if (candidates.Count == 0)
        {
            return false;
        }

        var target = new FachwerkColumnLocalPoint
        {
            X = plane.Anchor.X + plane.UpwardNormal.X * offset,
            Y = plane.Anchor.Y + plane.UpwardNormal.Y * offset,
        };
        selectedLocation = candidates
            .OrderBy(location => Distance(
                PointAtPathLocation(primitives, location),
                target))
            .ThenBy(location => location)
            .First();
        return true;
    }

    private static IEnumerable<double> IntersectionsAtPlane(
        FachwerkColumnPrimitiveDefinition primitive,
        FachwerkColumnJointPlane plane,
        double offset)
    {
        if (primitive.NormalizedKind() == "line")
        {
            var dx = primitive.End.X - primitive.Start.X;
            var dy = primitive.End.Y - primitive.Start.Y;
            var denominator =
                plane.UpwardNormal.X * dx +
                plane.UpwardNormal.Y * dy;
            if (Math.Abs(denominator) < Epsilon)
            {
                yield break;
            }
            var numerator = offset -
                plane.UpwardNormal.X * (primitive.Start.X - plane.Anchor.X) -
                plane.UpwardNormal.Y * (primitive.Start.Y - plane.Anchor.Y);
            var parameter = numerator / denominator;
            if (parameter >= -JointLocationTolerance &&
                parameter <= 1 + JointLocationTolerance)
            {
                yield return parameter;
            }
            yield break;
        }

        var radius = Distance(primitive.Start, primitive.Center);
        if (radius < Epsilon)
        {
            yield break;
        }
        var centerProjection =
            plane.UpwardNormal.X * (primitive.Center.X - plane.Anchor.X) +
            plane.UpwardNormal.Y * (primitive.Center.Y - plane.Anchor.Y);
        var ratio = (offset - centerProjection) / radius;
        if (ratio < -1 - JointLocationTolerance ||
            ratio > 1 + JointLocationTolerance)
        {
            yield break;
        }
        ratio = Math.Max(-1, Math.Min(1, ratio));
        var normalAngle = Math.Atan2(
            plane.UpwardNormal.Y,
            plane.UpwardNormal.X);
        var delta = Math.Acos(ratio);
        if (TryMapAngleToParameter(
            primitive,
            normalAngle + delta,
            out var first))
        {
            yield return first;
        }
        if (delta > JointLocationTolerance &&
            TryMapAngleToParameter(
                primitive,
                normalAngle - delta,
                out var second))
        {
            yield return second;
        }
    }

    private static FachwerkColumnLocalPoint PointAtPathLocation(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double location)
    {
        if (location <= 0)
        {
            return primitives[0].Start.Clone();
        }
        if (location >= primitives.Count)
        {
            return primitives[primitives.Count - 1].End.Clone();
        }
        var primitiveIndex = Math.Min(
            primitives.Count - 1,
            (int)Math.Floor(location));
        return PointAt(
            primitives[primitiveIndex],
            location - primitiveIndex);
    }

    private static FachwerkVector UpwardTangentAtPathLocation(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        double location)
    {
        var primitiveIndex = Math.Min(
            primitives.Count - 1,
            Math.Max(0, (int)Math.Floor(
                Math.Min(location, primitives.Count - JointLocationTolerance))));
        var parameter = Math.Max(0, Math.Min(1, location - primitiveIndex));
        var tangent = TangentAt(primitives[primitiveIndex], parameter);
        if (Math.Abs(tangent.Y) <= JointLocationTolerance &&
            Math.Abs(tangent.X) <= JointLocationTolerance)
        {
            throw new InvalidOperationException(
                "Не удалось вычислить касательную внешнего пояса в стыке.");
        }
        if (tangent.Y < -JointLocationTolerance ||
            (Math.Abs(tangent.Y) <= JointLocationTolerance && tangent.X < 0))
        {
            tangent = new FachwerkVector(-tangent.X, -tangent.Y, 0);
        }
        return tangent;
    }

    private static FachwerkVector TangentAt(
        FachwerkColumnPrimitiveDefinition primitive,
        double parameter)
    {
        double x;
        double y;
        if (primitive.NormalizedKind() == "line")
        {
            x = primitive.End.X - primitive.Start.X;
            y = primitive.End.Y - primitive.Start.Y;
        }
        else
        {
            var point = PointAt(primitive, parameter);
            var radialX = point.X - primitive.Center.X;
            var radialY = point.Y - primitive.Center.Y;
            var sign = primitive.SweepDeg >= 0 ? 1.0 : -1.0;
            x = -radialY * sign;
            y = radialX * sign;
        }
        var length = Math.Sqrt(x * x + y * y);
        if (length < Epsilon)
        {
            throw new InvalidOperationException(
                "В траектории внешнего пояса найден вырожденный сегмент.");
        }
        return new FachwerkVector(x / length, y / length, 0);
    }

    private static void AddUniqueLocation(
        ICollection<double> locations,
        double location)
    {
        foreach (var existing in locations)
        {
            if (Math.Abs(existing - location) <= JointLocationTolerance)
            {
                return;
            }
        }
        locations.Add(location);
    }
}

internal sealed class FachwerkColumnJointPlane
{
    internal FachwerkColumnJointPlane(
        FachwerkColumnBreak columnBreak,
        int assemblyBoundaryIndex,
        FachwerkColumnLocalPoint anchor,
        FachwerkVector upwardNormal,
        bool coincidesWithSectionTransition)
    {
        Break = columnBreak;
        AssemblyBoundaryIndex = assemblyBoundaryIndex;
        Anchor = anchor;
        UpwardNormal = upwardNormal;
        CoincidesWithSectionTransition = coincidesWithSectionTransition;
    }

    public FachwerkColumnBreak Break { get; }
    public int AssemblyBoundaryIndex { get; }
    public FachwerkColumnLocalPoint Anchor { get; }
    public FachwerkVector UpwardNormal { get; }
    public bool CoincidesWithSectionTransition { get; }
}

internal sealed class FachwerkColumnJointBoundary
{
    internal FachwerkColumnJointBoundary(
        FachwerkColumnJointPlane joint,
        double lowerLocation,
        double upperLocation)
    {
        Joint = joint;
        LowerLocation = lowerLocation;
        UpperLocation = upperLocation;
    }

    public FachwerkColumnJointPlane Joint { get; }
    public double LowerLocation { get; }
    public double UpperLocation { get; }
}
