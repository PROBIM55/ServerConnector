#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Structura.Tekla.Fachwerk;

internal enum FachwerkColumnErectionBevelTarget
{
    PhysicalPart,
    FlangeInsert,
}

internal sealed class FachwerkColumnErectionInsertSpec
{
    internal FachwerkColumnErectionInsertSpec(
        int breakIndex,
        string flangeRole,
        FachwerkColumnLocalPoint start,
        FachwerkColumnLocalPoint end,
        FachwerkVector upwardTangent,
        FachwerkVector outwardNormal)
    {
        BreakIndex = breakIndex;
        FlangeRole = flangeRole;
        Start = start;
        End = end;
        UpwardTangent = upwardTangent;
        OutwardNormal = outwardNormal;
    }

    public int BreakIndex { get; }
    public string FlangeRole { get; }
    public FachwerkColumnLocalPoint Start { get; }
    public FachwerkColumnLocalPoint End { get; }
    public FachwerkVector UpwardTangent { get; }
    public FachwerkVector OutwardNormal { get; }
    public string SemanticRole => "erection-insert-" + FlangeRole;
}

internal sealed class FachwerkColumnErectionBevelSpec
{
    internal FachwerkColumnErectionBevelSpec(
        int breakIndex,
        string semanticRole,
        FachwerkColumnErectionBevelTarget target,
        int physicalPartIndex,
        string flangeInsertRole,
        FachwerkColumnLocalPoint anchor,
        double anchorZ,
        FachwerkVector upwardTangent,
        FachwerkVector outwardNormal,
        double thickness,
        double crossSpan)
    {
        BreakIndex = breakIndex;
        SemanticRole = semanticRole;
        Target = target;
        PhysicalPartIndex = physicalPartIndex;
        FlangeInsertRole = flangeInsertRole;
        Anchor = anchor;
        AnchorZ = anchorZ;
        UpwardTangent = upwardTangent;
        OutwardNormal = outwardNormal;
        Thickness = thickness;
        CrossSpan = crossSpan;
    }

    public int BreakIndex { get; }
    public string SemanticRole { get; }
    public FachwerkColumnErectionBevelTarget Target { get; }
    public int PhysicalPartIndex { get; }
    public string FlangeInsertRole { get; }
    public FachwerkColumnLocalPoint Anchor { get; }
    public double AnchorZ { get; }
    public FachwerkVector UpwardTangent { get; }
    public FachwerkVector OutwardNormal { get; }
    public double Thickness { get; }
    public double CrossSpan { get; }
}

internal sealed class FachwerkColumnErectionJointSpec
{
    internal FachwerkColumnErectionJointSpec(
        int breakIndex,
        IReadOnlyList<FachwerkColumnErectionInsertSpec> inserts,
        IReadOnlyList<FachwerkColumnErectionBevelSpec> bevels)
    {
        BreakIndex = breakIndex;
        Inserts = inserts;
        Bevels = bevels;
    }

    public int BreakIndex { get; }
    public IReadOnlyList<FachwerkColumnErectionInsertSpec> Inserts { get; }
    public IReadOnlyList<FachwerkColumnErectionBevelSpec> Bevels { get; }
}

internal static partial class FachwerkColumnGeometry
{
    internal const double ErectionInsertUpperPenetration = 30.0;
    internal const double ErectionBevelAngleDeg = 40.0;
    internal const double ErectionBevelRootFace = 2.0;

    private const double ErectionJointTolerance = 0.25;

    internal static IReadOnlyList<FachwerkColumnErectionJointSpec>
        BuildErectionJointSpecs(
            FachwerkColumnProfileDefinition profile,
            FachwerkColumnFrame frame,
            IReadOnlyList<FachwerkColumnBreak> breaks,
            IReadOnlyList<FachwerkColumnPartSpec> physicalParts)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (physicalParts == null) throw new ArgumentNullException(nameof(physicalParts));

        var erectionBreaks = (breaks ?? Array.Empty<FachwerkColumnBreak>())
            .Where(item => item != null && item.IsErectionSplice())
            .ToArray();
        if (erectionBreaks.Length == 0)
        {
            return Array.Empty<FachwerkColumnErectionJointSpec>();
        }

        var sourceInner = profile.RequirePath("inner-flange");
        var sourceOuter = profile.RequirePath("outer-flange");
        var physicalInner = OffsetToward(
            sourceInner,
            sourceOuter,
            FlangeThickness * 0.5,
            "inner-flange",
            "PL50*180",
            0);
        var physicalOuter = OffsetToward(
            sourceOuter,
            sourceInner,
            FlangeThickness * 0.5,
            "outer-flange",
            "PL50*180",
            0);
        var webAxis = BuildWebAxis(
            sourceInner,
            sourceOuter,
            profile.SectionTransition);
        var physicalInnerWeb = ClonePathWithRole(
            webAxis,
            "inner-web",
            -WebNormalOffset);
        var physicalOuterWeb = ClonePathWithRole(
            webAxis,
            "outer-web",
            WebNormalOffset);
        var joints = ResolveJointPlanes(
            physicalOuter,
            frame,
            breaks,
            profile.SectionTransition);

        var result = new List<FachwerkColumnErectionJointSpec>();
        foreach (var joint in joints.Where(item => item.Break.IsErectionSplice()))
        {
            if (joint.CoincidesWithSectionTransition)
            {
                throw new InvalidOperationException(
                    "Монтажный стык " + joint.Break.Index +
                    " совпал с переходом высоты сечения 450/380 мм.");
            }

            var innerInsert = BuildFlangeInsert(physicalInner, joint);
            var outerInsert = BuildFlangeInsert(physicalOuter, joint);
            var boxDirection = UnitVector(
                outerInsert.UpperPoint.X - innerInsert.UpperPoint.X,
                outerInsert.UpperPoint.Y - innerInsert.UpperPoint.Y);
            var inserts = new[]
            {
                CreateInsertSpec(
                    joint,
                    "inner-flange",
                    innerInsert,
                    new FachwerkVector(-boxDirection.X, -boxDirection.Y, 0)),
                CreateInsertSpec(
                    joint,
                    "outer-flange",
                    outerInsert,
                    boxDirection),
            };

            var bevels = new List<FachwerkColumnErectionBevelSpec>();
            foreach (var insert in inserts)
            {
                bevels.Add(new FachwerkColumnErectionBevelSpec(
                    joint.Break.Index,
                    insert.SemanticRole + "-lower",
                    FachwerkColumnErectionBevelTarget.FlangeInsert,
                    -1,
                    insert.FlangeRole,
                    insert.Start,
                    0,
                    insert.UpwardTangent,
                    insert.OutwardNormal,
                    FlangeThickness,
                    FlangeWidth));
            }

            AddUpperFlangeBevel(
                bevels,
                physicalParts,
                joint,
                "inner-flange",
                innerInsert,
                new FachwerkVector(-boxDirection.X, -boxDirection.Y, 0));
            AddUpperFlangeBevel(
                bevels,
                physicalParts,
                joint,
                "outer-flange",
                outerInsert,
                boxDirection);
            AddUpperWebBevel(
                bevels,
                physicalParts,
                physicalInnerWeb,
                joint,
                "inner-web",
                1.0);
            AddUpperWebBevel(
                bevels,
                physicalParts,
                physicalOuterWeb,
                joint,
                "outer-web",
                -1.0);

            result.Add(new FachwerkColumnErectionJointSpec(
                joint.Break.Index,
                new ReadOnlyCollection<FachwerkColumnErectionInsertSpec>(
                    inserts.ToList()),
                new ReadOnlyCollection<FachwerkColumnErectionBevelSpec>(
                    bevels)));
        }

        return new ReadOnlyCollection<FachwerkColumnErectionJointSpec>(result);
    }

    private static FachwerkColumnPathDefinition ClonePathWithRole(
        FachwerkColumnPathDefinition source,
        string role,
        double normalOffset) =>
        new FachwerkColumnPathDefinition
        {
            Role = role,
            Profile = source.Profile,
            Material = source.Material,
            ClassName = source.ClassName,
            NormalOffset = normalOffset,
            Primitives = source.Primitives
                .Select(ClonePrimitive)
                .ToList(),
        };

    private static FachwerkColumnErectionInsertSpec CreateInsertSpec(
        FachwerkColumnJointPlane joint,
        string role,
        ErectionFlangeGeometry geometry,
        FachwerkVector outwardNormal) =>
        new FachwerkColumnErectionInsertSpec(
            joint.Break.Index,
            role,
            geometry.LowerPoint,
            geometry.ExtendedUpperPoint,
            geometry.UpwardTangent,
            outwardNormal);

    private static ErectionFlangeGeometry BuildFlangeInsert(
        FachwerkColumnPathDefinition path,
        FachwerkColumnJointPlane joint)
    {
        var lowerLocation = RequirePlaneLocation(
            path,
            joint,
            joint.Break.FlangeLowerOffset);
        var upperLocation = RequirePlaneLocation(
            path,
            joint,
            joint.Break.FlangeUpperOffset);
        var lowerPoint = PointAtPathLocation(path.Primitives, lowerLocation);
        var upperPoint = PointAtPathLocation(path.Primitives, upperLocation);
        var tangent = UpwardTangentAtPathLocation(
            path.Primitives,
            upperLocation);
        ValidateStraightJointRegion(
            path,
            lowerLocation,
            upperLocation,
            lowerPoint,
            upperPoint,
            tangent,
            joint.Break.Index);
        var extendedUpper = AdvanceOnStraightPrimitive(
            path,
            upperLocation,
            tangent,
            ErectionInsertUpperPenetration,
            joint.Break.Index);
        return new ErectionFlangeGeometry(
            lowerPoint,
            upperPoint,
            extendedUpper,
            tangent);
    }

    private static void AddUpperFlangeBevel(
        ICollection<FachwerkColumnErectionBevelSpec> result,
        IReadOnlyList<FachwerkColumnPartSpec> physicalParts,
        FachwerkColumnJointPlane joint,
        string role,
        ErectionFlangeGeometry geometry,
        FachwerkVector outwardNormal)
    {
        var partIndex = RequireUpperPartIndex(
            physicalParts,
            role,
            joint.AssemblyBoundaryIndex + 1,
            geometry.UpperPoint);
        result.Add(new FachwerkColumnErectionBevelSpec(
            joint.Break.Index,
            role + "-upper-lower-end",
            FachwerkColumnErectionBevelTarget.PhysicalPart,
            partIndex,
            null,
            geometry.UpperPoint,
            0,
            geometry.UpwardTangent,
            outwardNormal,
            FlangeThickness,
            FlangeWidth));
    }

    private static void AddUpperWebBevel(
        ICollection<FachwerkColumnErectionBevelSpec> result,
        IReadOnlyList<FachwerkColumnPartSpec> physicalParts,
        FachwerkColumnPathDefinition path,
        FachwerkColumnJointPlane joint,
        string role,
        double outwardZ)
    {
        var upperLocation = RequirePlaneLocation(
            path,
            joint,
            joint.Break.WebUpperOffset);
        var upperPoint = PointAtPathLocation(path.Primitives, upperLocation);
        var tangent = UpwardTangentAtPathLocation(
            path.Primitives,
            upperLocation);
        var partIndex = RequireUpperPartIndex(
            physicalParts,
            role,
            joint.AssemblyBoundaryIndex + 1,
            upperPoint);
        var part = physicalParts[partIndex];
        result.Add(new FachwerkColumnErectionBevelSpec(
            joint.Break.Index,
            role + "-upper-lower-end",
            FachwerkColumnErectionBevelTarget.PhysicalPart,
            partIndex,
            null,
            upperPoint,
            -part.NormalOffset,
            tangent,
            new FachwerkVector(0, 0, outwardZ),
            WebThickness,
            ParsePlateHeight(part.Profile)));
    }

    private static double RequirePlaneLocation(
        FachwerkColumnPathDefinition path,
        FachwerkColumnJointPlane joint,
        double offset)
    {
        if (TryFindNearestLocationAtPlane(
            path.Primitives,
            joint,
            offset,
            out var location))
        {
            return location;
        }
        throw new InvalidOperationException(
            "Монтажный стык " + joint.Break.Index +
            " не пересёк физическую ось '" + path.NormalizedRole() +
            "' при смещении " +
            offset.ToString("0.###", CultureInfo.InvariantCulture) + " мм.");
    }

    private static void ValidateStraightJointRegion(
        FachwerkColumnPathDefinition path,
        double lowerLocation,
        double upperLocation,
        FachwerkColumnLocalPoint lowerPoint,
        FachwerkColumnLocalPoint upperPoint,
        FachwerkVector upwardTangent,
        int breakIndex)
    {
        var deltaX = upperPoint.X - lowerPoint.X;
        var deltaY = upperPoint.Y - lowerPoint.Y;
        var along = deltaX * upwardTangent.X + deltaY * upwardTangent.Y;
        var across = Math.Abs(
            deltaX * upwardTangent.Y -
            deltaY * upwardTangent.X);
        var lowerTangent = UpwardTangentAtPathLocation(
            path.Primitives,
            lowerLocation);
        var tangentDot =
            lowerTangent.X * upwardTangent.X +
            lowerTangent.Y * upwardTangent.Y;
        if (along <= ErectionJointTolerance ||
            across > ErectionJointTolerance ||
            tangentDot < 0.9999)
        {
            throw new InvalidOperationException(
                "Монтажный стык " + breakIndex +
                " роли '" + path.NormalizedRole() +
                "' должен находиться на одном прямом участке.");
        }
    }

    private static FachwerkColumnLocalPoint AdvanceOnStraightPrimitive(
        FachwerkColumnPathDefinition path,
        double location,
        FachwerkVector upwardTangent,
        double distance,
        int breakIndex)
    {
        var source = PointAtPathLocation(path.Primitives, location);
        var target = new FachwerkColumnLocalPoint
        {
            X = source.X + upwardTangent.X * distance,
            Y = source.Y + upwardTangent.Y * distance,
        };
        foreach (var primitive in path.Primitives)
        {
            if (primitive.NormalizedKind() != "line")
            {
                continue;
            }
            if (PointOnLineSegment(source, primitive) &&
                PointOnLineSegment(target, primitive))
            {
                return target;
            }
        }
        throw new InvalidOperationException(
            "Монтажный стык " + breakIndex +
            " роли '" + path.NormalizedRole() +
            "' не имеет 30 мм прямого участка для захода вставки.");
    }

    private static bool PointOnLineSegment(
        FachwerkColumnLocalPoint point,
        FachwerkColumnPrimitiveDefinition line)
    {
        var dx = line.End.X - line.Start.X;
        var dy = line.End.Y - line.Start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= Epsilon)
        {
            return false;
        }
        var parameter =
            ((point.X - line.Start.X) * dx +
             (point.Y - line.Start.Y) * dy) /
            lengthSquared;
        if (parameter < -ErectionJointTolerance ||
            parameter > 1 + ErectionJointTolerance)
        {
            return false;
        }
        var projectedX = line.Start.X + parameter * dx;
        var projectedY = line.Start.Y + parameter * dy;
        return Math.Sqrt(
            (point.X - projectedX) * (point.X - projectedX) +
            (point.Y - projectedY) * (point.Y - projectedY)) <=
            ErectionJointTolerance;
    }

    private static int RequireUpperPartIndex(
        IReadOnlyList<FachwerkColumnPartSpec> physicalParts,
        string role,
        int assemblySegmentIndex,
        FachwerkColumnLocalPoint boundary)
    {
        var candidates = physicalParts
            .Select((part, index) => new { Part = part, Index = index })
            .Where(item =>
                string.Equals(
                    item.Part.Role,
                    role,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Part.AssemblySegmentIndex == assemblySegmentIndex)
            .Select(item => new
            {
                item.Index,
                Distance = DistanceToPathEndpoint(
                    boundary,
                    item.Part.FirstBoundary),
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Index)
            .ToArray();
        if (candidates.Length == 0 ||
            candidates[0].Distance > ErectionJointTolerance)
        {
            throw new InvalidOperationException(
                "Для монтажного стыка не найдена верхняя физическая деталь роли '" +
                role + "' в сборочном сегменте " +
                assemblySegmentIndex.ToString(CultureInfo.InvariantCulture) + ".");
        }
        return candidates[0].Index;
    }

    private static double DistanceToPathEndpoint(
        FachwerkColumnLocalPoint point,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> path)
    {
        if (path == null || path.Count == 0)
        {
            return double.PositiveInfinity;
        }
        return Math.Min(
            Distance(point, path[0].Start),
            Distance(point, path[path.Count - 1].End));
    }

    private sealed class ErectionFlangeGeometry
    {
        internal ErectionFlangeGeometry(
            FachwerkColumnLocalPoint lowerPoint,
            FachwerkColumnLocalPoint upperPoint,
            FachwerkColumnLocalPoint extendedUpperPoint,
            FachwerkVector upwardTangent)
        {
            LowerPoint = lowerPoint;
            UpperPoint = upperPoint;
            ExtendedUpperPoint = extendedUpperPoint;
            UpwardTangent = upwardTangent;
        }

        public FachwerkColumnLocalPoint LowerPoint { get; }
        public FachwerkColumnLocalPoint UpperPoint { get; }
        public FachwerkColumnLocalPoint ExtendedUpperPoint { get; }
        public FachwerkVector UpwardTangent { get; }
    }
}
