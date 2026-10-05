#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal sealed class FachwerkLowerRigelPlane
{
    internal FachwerkLowerRigelPlane(
        Point origin,
        Vector normal,
        Vector preferredAxis)
    {
        Origin = new Point(origin);
        Normal = FachwerkLowerRigelNodeGeometry.Normalize(
            normal,
            "нормаль плоскости");
        Vector projected = FachwerkLowerRigelNodeGeometry.Subtract(
            preferredAxis,
            FachwerkLowerRigelNodeGeometry.Scale(
                Normal,
                FachwerkLowerRigelNodeGeometry.Dot(
                    preferredAxis,
                    Normal)));
        if (FachwerkLowerRigelNodeGeometry.Length(projected) < 1e-7)
        {
            Vector fallback =
                Math.Abs(Normal.Z) < 0.9
                    ? new Vector(0, 0, 1)
                    : new Vector(1, 0, 0);
            projected = FachwerkLowerRigelNodeGeometry.Subtract(
                fallback,
                FachwerkLowerRigelNodeGeometry.Scale(
                    Normal,
                    FachwerkLowerRigelNodeGeometry.Dot(
                        fallback,
                        Normal)));
        }

        AxisX = FachwerkLowerRigelNodeGeometry.Normalize(
            projected,
            "ось X плоскости");
        AxisY = FachwerkLowerRigelNodeGeometry.Normalize(
            FachwerkLowerRigelNodeGeometry.Cross(Normal, AxisX),
            "ось Y плоскости");
    }

    public Point Origin { get; }
    public Vector Normal { get; }
    public Vector AxisX { get; }
    public Vector AxisY { get; }

    internal Plane ToTeklaPlane()
    {
        return new Plane
        {
            Origin = new Point(Origin),
            AxisX = new Vector(AxisX),
            AxisY = new Vector(AxisY),
        };
    }

    internal FachwerkLowerRigelPlane OrientedToward(Point keepPoint)
    {
        if (FachwerkLowerRigelNodeGeometry.SignedDistance(
                keepPoint,
                this) >= 0)
        {
            return this;
        }

        return new FachwerkLowerRigelPlane(
            Origin,
            FachwerkLowerRigelNodeGeometry.Scale(Normal, -1),
            AxisX);
    }

    internal FachwerkLowerRigelPlane OrientedAwayFrom(Point keepPoint)
    {
        FachwerkLowerRigelPlane toward = OrientedToward(keepPoint);
        return new FachwerkLowerRigelPlane(
            toward.Origin,
            FachwerkLowerRigelNodeGeometry.Scale(toward.Normal, -1),
            toward.AxisX);
    }
}

internal sealed class FachwerkLowerRigelTransition
{
    internal FachwerkLowerRigelTransition(
        string role,
        IReadOnlyList<Point> boundary,
        IReadOnlyList<Point> firstEdge,
        IReadOnlyList<Point> secondEdge,
        FachwerkLowerRigelPlane referencePlane,
        Vector inwardDirection,
        FachwerkLowerRigelPlane farPlane)
    {
        Role = role;
        Boundary = boundary;
        FirstEdge = firstEdge;
        SecondEdge = secondEdge;
        ReferencePlane = referencePlane;
        InwardDirection = new Vector(inwardDirection);
        FarPlane = farPlane;
    }

    public string Role { get; }
    public IReadOnlyList<Point> Boundary { get; }
    public IReadOnlyList<Point> FirstEdge { get; }
    public IReadOnlyList<Point> SecondEdge { get; }
    public FachwerkLowerRigelPlane ReferencePlane { get; }
    public Vector InwardDirection { get; }
    public FachwerkLowerRigelPlane FarPlane { get; }
}

internal sealed class FachwerkLowerRigelBottomClosure
{
    internal FachwerkLowerRigelBottomClosure(
        IReadOnlyList<Point> boundary,
        Vector extrusionDirection)
    {
        Boundary = boundary ??
            throw new ArgumentNullException(nameof(boundary));
        ExtrusionDirection = new Vector(
            extrusionDirection ??
            throw new ArgumentNullException(nameof(extrusionDirection)));
    }

    public IReadOnlyList<Point> Boundary { get; }
    public Vector ExtrusionDirection { get; }
    public bool IsTriangular => Boundary.Count == 3;
}

internal sealed class FachwerkLowerRigelLayout
{
    internal FachwerkLowerRigelLayout(
        Point leftAnchor,
        Point rightAnchor,
        Vector spanDirection,
        Point leftTubeIntersection,
        Point rightTubeIntersection,
        Vector leftTubeDirection,
        Vector rightTubeDirection,
        Point leftCentralTubePoint,
        Point rightCentralTubePoint,
        FachwerkLowerRigelPlane leftOuterPlane,
        FachwerkLowerRigelPlane leftInnerPlane,
        FachwerkLowerRigelPlane rightOuterPlane,
        FachwerkLowerRigelPlane rightInnerPlane,
        IReadOnlyList<Point> leftEndPlateBoundary,
        IReadOnlyList<Point> rightEndPlateBoundary,
        FachwerkLowerRigelTransition leftTransition,
        FachwerkLowerRigelTransition rightTransition)
    {
        LeftAnchor = leftAnchor;
        RightAnchor = rightAnchor;
        SpanDirection = spanDirection;
        LeftTubeIntersection = leftTubeIntersection;
        RightTubeIntersection = rightTubeIntersection;
        LeftTubeDirection = new Vector(leftTubeDirection);
        RightTubeDirection = new Vector(rightTubeDirection);
        LeftCentralTubePoint = leftCentralTubePoint;
        RightCentralTubePoint = rightCentralTubePoint;
        LeftOuterPlane = leftOuterPlane;
        LeftInnerPlane = leftInnerPlane;
        RightOuterPlane = rightOuterPlane;
        RightInnerPlane = rightInnerPlane;
        LeftEndPlateBoundary = leftEndPlateBoundary;
        RightEndPlateBoundary = rightEndPlateBoundary;
        LeftTransition = leftTransition;
        RightTransition = rightTransition;
    }

    public Point LeftAnchor { get; }
    public Point RightAnchor { get; }
    public Vector SpanDirection { get; }
    public Point LeftTubeIntersection { get; }
    public Point RightTubeIntersection { get; }
    public Vector LeftTubeDirection { get; }
    public Vector RightTubeDirection { get; }
    public Point LeftCentralTubePoint { get; }
    public Point RightCentralTubePoint { get; }
    public FachwerkLowerRigelPlane LeftOuterPlane { get; }
    public FachwerkLowerRigelPlane LeftInnerPlane { get; }
    public FachwerkLowerRigelPlane RightOuterPlane { get; }
    public FachwerkLowerRigelPlane RightInnerPlane { get; }
    public IReadOnlyList<Point> LeftEndPlateBoundary { get; }
    public IReadOnlyList<Point> RightEndPlateBoundary { get; }
    public FachwerkLowerRigelTransition LeftTransition { get; }
    public FachwerkLowerRigelTransition RightTransition { get; }
}

internal sealed class FachwerkLowerRigelMontageLayout
{
    internal FachwerkLowerRigelMontageLayout(
        FachwerkLowerRigelPlane referencePlane,
        FachwerkLowerRigelPlane leftUpperWebPlane,
        FachwerkLowerRigelPlane rightUpperWebPlane,
        FachwerkLowerRigelPlane lowerFlangePlane,
        FachwerkLowerRigelPlane upperFlangePlane,
        FachwerkLowerRigelPlane insertEndPlane)
    {
        ReferencePlane = referencePlane;
        LeftUpperWebPlane = leftUpperWebPlane;
        RightUpperWebPlane = rightUpperWebPlane;
        LowerFlangePlane = lowerFlangePlane;
        UpperFlangePlane = upperFlangePlane;
        InsertEndPlane = insertEndPlane;
    }

    public FachwerkLowerRigelPlane ReferencePlane { get; }
    public FachwerkLowerRigelPlane LeftUpperWebPlane { get; }
    public FachwerkLowerRigelPlane RightUpperWebPlane { get; }
    public FachwerkLowerRigelPlane LowerFlangePlane { get; }
    public FachwerkLowerRigelPlane UpperFlangePlane { get; }
    public FachwerkLowerRigelPlane InsertEndPlane { get; }
    public Vector UpwardDirection => ReferencePlane.Normal;
}

internal sealed class FachwerkLowerRigelFlangeSplice
{
    internal FachwerkLowerRigelFlangeSplice(
        string role,
        Point lowerAxisPoint,
        Point upperAxisPoint,
        Point insertEndAxisPoint,
        Point lowerFragmentEndPoint,
        Vector upwardTangent,
        Vector outwardNormal,
        IReadOnlyList<Point> insertBoundary,
        IReadOnlyList<Point> lowerFragmentBoundary,
        FachwerkLowerRigelPlane tubeCutPlane,
        double thickness,
        double width)
    {
        Role = role;
        LowerAxisPoint = lowerAxisPoint;
        UpperAxisPoint = upperAxisPoint;
        InsertEndAxisPoint = insertEndAxisPoint;
        LowerFragmentEndPoint = lowerFragmentEndPoint;
        UpwardTangent = upwardTangent;
        OutwardNormal = outwardNormal;
        InsertBoundary = insertBoundary;
        LowerFragmentBoundary = lowerFragmentBoundary;
        TubeCutPlane = tubeCutPlane;
        Thickness = thickness;
        Width = width;
    }

    public string Role { get; }
    public Point LowerAxisPoint { get; }
    public Point UpperAxisPoint { get; }
    public Point InsertEndAxisPoint { get; }
    public Point LowerFragmentEndPoint { get; }
    public Vector UpwardTangent { get; }
    public Vector OutwardNormal { get; }
    public IReadOnlyList<Point> InsertBoundary { get; }
    public IReadOnlyList<Point> LowerFragmentBoundary { get; }
    public FachwerkLowerRigelPlane TubeCutPlane { get; }
    public double Thickness { get; }
    public double Width { get; }
}

internal static class FachwerkLowerRigelNodeGeometry
{
    private const double Epsilon = 1e-7;
    private const int SectionSegments = 96;
    private const double EndPlateOverbuild = 20.0;

    private sealed class CylinderPlaneContact
    {
        internal CylinderPlaneContact(
            double distance,
            double widthOffset)
        {
            Distance = distance;
            WidthOffset = widthOffset;
        }

        internal double Distance { get; }
        internal double WidthOffset { get; }
    }

    internal static FachwerkLowerRigelLayout BuildLayout(
        IReadOnlyList<Point> leftTubeAxis,
        IReadOnlyList<Point> rightTubeAxis,
        IReadOnlyList<Point> leftWebPath,
        IReadOnlyList<Point> rightWebPath,
        double gap,
        double diameter,
        double transitionPlateWidth,
        double leftTransitionLength,
        double rightTransitionLength)
    {
        RequirePath(leftTubeAxis, "ось левой трубы");
        RequirePath(rightTubeAxis, "ось правой трубы");
        RequirePath(leftWebPath, "ось левой стенки");
        RequirePath(rightWebPath, "ось правой стенки");
        RequirePositive(gap, "Зазор");
        RequirePositive(diameter, "Диаметр трубы");
        RequirePositive(transitionPlateWidth, "Ширина переходной пластины");
        RequirePositive(leftTransitionLength, "Длина левой переходной пластины");
        RequirePositive(rightTransitionLength, "Длина правой переходной пластины");

        JointEndpoints endpoints = SelectJointEndpoints(
            leftTubeAxis,
            rightTubeAxis,
            leftWebPath,
            rightWebPath);
        IReadOnlyList<Point> orientedLeftWeb = OrientFrom(
            leftWebPath,
            endpoints.LeftAnchor);
        IReadOnlyList<Point> orientedRightWeb = OrientFrom(
            rightWebPath,
            endpoints.RightAnchor);

        Vector spanDirection = Normalize(
            VectorBetween(endpoints.LeftAnchor, endpoints.RightAnchor),
            "направление нижнего ригеля");
        Vector leftOutward = Scale(spanDirection, -1);
        Vector rightOutward = spanDirection;
        double halfGap = gap * 0.5;
        var leftOuterPlane = new FachwerkLowerRigelPlane(
            Add(endpoints.LeftAnchor, leftOutward, halfGap),
            spanDirection,
            new Vector(0, 0, 1));
        var leftInnerPlane = new FachwerkLowerRigelPlane(
            Add(endpoints.LeftAnchor, spanDirection, halfGap),
            spanDirection,
            new Vector(0, 0, 1));
        var rightOuterPlane = new FachwerkLowerRigelPlane(
            Add(endpoints.RightAnchor, rightOutward, halfGap),
            spanDirection,
            new Vector(0, 0, 1));
        var rightInnerPlane = new FachwerkLowerRigelPlane(
            Add(endpoints.RightAnchor, leftOutward, halfGap),
            spanDirection,
            new Vector(0, 0, 1));

        Point leftIntersection = IntersectPolylineWithPlane(
            leftTubeAxis,
            leftOuterPlane,
            "левой трубы");
        Point rightIntersection = IntersectPolylineWithPlane(
            rightTubeAxis,
            rightOuterPlane,
            "правой трубы");
        Point leftCentralPoint = new Point(leftIntersection);
        Point rightCentralPoint = new Point(rightIntersection);
        Vector centralDirection = Normalize(
            VectorBetween(leftCentralPoint, rightCentralPoint),
            "ось вставки ригеля");
        Vector leftTubeDirection = DirectionAtPlane(
            leftTubeAxis,
            leftOuterPlane);
        Vector rightTubeDirection = DirectionAtPlane(
            rightTubeAxis,
            rightOuterPlane);

        IReadOnlyList<Point> leftEndPlate = BuildEndPlateBoundary(
            leftIntersection,
            leftTubeDirection,
            leftOuterPlane,
            diameter);
        IReadOnlyList<Point> rightEndPlate = BuildEndPlateBoundary(
            rightIntersection,
            rightTubeDirection,
            rightOuterPlane,
            diameter);

        FachwerkLowerRigelTransition leftTransition = BuildTransition(
            "left-transition",
            orientedLeftWeb,
            leftOutward,
            spanDirection,
            halfGap,
            transitionPlateWidth,
            leftTransitionLength);
        FachwerkLowerRigelTransition rightTransition = BuildTransition(
            "right-transition",
            orientedRightWeb,
            rightOutward,
            spanDirection,
            halfGap,
            transitionPlateWidth,
            rightTransitionLength);

        return new FachwerkLowerRigelLayout(
            endpoints.LeftAnchor,
            endpoints.RightAnchor,
            spanDirection,
            leftIntersection,
            rightIntersection,
            leftTubeDirection,
            rightTubeDirection,
            leftCentralPoint,
            rightCentralPoint,
            leftOuterPlane,
            leftInnerPlane,
            rightOuterPlane,
            rightInnerPlane,
            leftEndPlate,
            rightEndPlate,
            leftTransition,
            rightTransition);
    }

    internal static FachwerkLowerRigelMontageLayout BuildMontageLayout(
        FachwerkLowerRigelLayout layout)
    {
        if (layout == null) throw new ArgumentNullException(nameof(layout));

        FachwerkLowerRigelPlane left =
            layout.LeftTransition.FarPlane;
        FachwerkLowerRigelPlane right =
            layout.RightTransition.FarPlane;
        Vector rightNormal = Dot(left.Normal, right.Normal) >= 0
            ? right.Normal
            : Scale(right.Normal, -1);
        Vector across = Normalize(
            VectorBetween(left.Origin, right.Origin),
            "поперечное направление монтажного стыка");
        Vector averaged = Add(left.Normal, rightNormal);
        if (Length(averaged) < Epsilon)
        {
            averaged = left.Normal;
        }
        Vector projected = Subtract(
            averaged,
            Scale(across, Dot(averaged, across)));
        if (Length(projected) < Epsilon)
        {
            projected = Subtract(
                left.Normal,
                Scale(across, Dot(left.Normal, across)));
        }
        Vector upward = Normalize(
            projected,
            "направление монтажного стыка вдоль стойки");
        if (Dot(upward, left.Normal) + Dot(upward, rightNormal) < 0)
        {
            upward = Scale(upward, -1);
        }

        var reference = new FachwerkLowerRigelPlane(
            Lerp(left.Origin, right.Origin, 0.5),
            upward,
            across);
        double webShift =
            FachwerkColumnBreak.ErectionWebUpperOffset -
            FachwerkColumnBreak.ErectionWebLowerOffset;
        double lowerFlangeShift =
            FachwerkColumnBreak.ErectionFlangeLowerOffset -
            FachwerkColumnBreak.ErectionWebLowerOffset;
        double upperFlangeShift =
            FachwerkColumnBreak.ErectionFlangeUpperOffset -
            FachwerkColumnBreak.ErectionWebLowerOffset;
        double insertEndShift =
            upperFlangeShift +
            FachwerkColumnGeometry.ErectionInsertUpperPenetration;

        return new FachwerkLowerRigelMontageLayout(
            reference,
            OffsetPlane(left, webShift),
            OffsetPlane(right, webShift),
            OffsetPlane(reference, lowerFlangeShift),
            OffsetPlane(reference, upperFlangeShift),
            OffsetPlane(reference, insertEndShift));
    }

    internal static FachwerkLowerRigelBottomClosure BuildBottomClosure(
        FachwerkLowerRigelLayout layout)
    {
        if (layout == null) throw new ArgumentNullException(nameof(layout));

        Point leftFirst = layout.LeftTransition.FirstEdge[
            layout.LeftTransition.FirstEdge.Count - 1];
        Point rightFirst = layout.RightTransition.FirstEdge[
            layout.RightTransition.FirstEdge.Count - 1];
        Point rightSecond = layout.RightTransition.SecondEdge[
            layout.RightTransition.SecondEdge.Count - 1];
        Point leftSecond = layout.LeftTransition.SecondEdge[
            layout.LeftTransition.SecondEdge.Count - 1];

        bool firstSideProtrudes =
            !IsInsideBoundary(
                leftFirst,
                layout.LeftEndPlateBoundary,
                layout.LeftTransition.ReferencePlane) ||
            !IsInsideBoundary(
                rightFirst,
                layout.RightEndPlateBoundary,
                layout.RightTransition.ReferencePlane);
        bool secondSideProtrudes =
            !IsInsideBoundary(
                leftSecond,
                layout.LeftEndPlateBoundary,
                layout.LeftTransition.ReferencePlane) ||
            !IsInsideBoundary(
                rightSecond,
                layout.RightEndPlateBoundary,
                layout.RightTransition.ReferencePlane);

        Vector leftDown = layout.LeftTransition.FarPlane.Normal;
        Vector rightDown = layout.RightTransition.FarPlane.Normal;
        if (Dot(leftDown, rightDown) < 0)
        {
            rightDown = Scale(rightDown, -1);
        }
        Vector down = Add(leftDown, rightDown);
        if (Length(down) < Epsilon)
        {
            down = new Vector(leftDown);
        }

        return BuildBottomClosure(
            leftFirst,
            rightFirst,
            rightSecond,
            leftSecond,
            firstSideProtrudes,
            secondSideProtrudes,
            down);
    }

    internal static FachwerkLowerRigelBottomClosure BuildBottomClosure(
        Point leftFirst,
        Point rightFirst,
        Point rightSecond,
        Point leftSecond,
        bool firstSideProtrudes,
        bool secondSideProtrudes,
        Vector extrusionDirection)
    {
        if (!firstSideProtrudes && !secondSideProtrudes)
        {
            return null;
        }
        if (leftFirst == null || rightFirst == null ||
            rightSecond == null || leftSecond == null)
        {
            throw new InvalidOperationException(
                "Нижняя заглушка не получила четыре опорные точки.");
        }

        IReadOnlyList<Point> boundary;
        if (firstSideProtrudes && secondSideProtrudes)
        {
            boundary = new[]
            {
                new Point(leftFirst),
                new Point(rightFirst),
                new Point(rightSecond),
                new Point(leftSecond),
            };
        }
        else if (firstSideProtrudes)
        {
            boundary = new[]
            {
                new Point(leftFirst),
                new Point(rightFirst),
                Lerp(rightSecond, leftSecond, 0.5),
            };
        }
        else
        {
            boundary = new[]
            {
                new Point(leftSecond),
                new Point(rightSecond),
                Lerp(rightFirst, leftFirst, 0.5),
            };
        }

        return new FachwerkLowerRigelBottomClosure(
            RemoveConsecutiveDuplicates(boundary),
            Normalize(extrusionDirection, "направление выдавливания заглушки"));
    }

    internal static FachwerkLowerRigelFlangeSplice BuildFlangeSplice(
        string role,
        IReadOnlyList<Point> sourceAxis,
        IReadOnlyList<Point> oppositeAxis,
        FachwerkLowerRigelMontageLayout montage,
        Point tubeAxisPoint,
        Vector tubeAxisDirection,
        double tubeRadius)
    {
        RequirePath(sourceAxis, role + " пояс");
        RequirePath(oppositeAxis, role + " противоположный пояс");
        if (montage == null) throw new ArgumentNullException(nameof(montage));
        RequirePositive(tubeRadius, "Радиус трубы");

        IReadOnlyList<Point> axis = OrientToward(
            sourceAxis,
            montage.ReferencePlane,
            montage.UpwardDirection);
        Vector tangent = Normalize(
            VectorBetween(axis[0], axis[1]),
            role + " касательная пояса");
        if (Dot(tangent, montage.UpwardDirection) < 0)
        {
            tangent = Scale(tangent, -1);
        }
        ValidateStraightMontageRegion(
            axis,
            tangent,
            montage,
            role);
        IReadOnlyList<Point> opposite = OrientToward(
            oppositeAxis,
            montage.ReferencePlane,
            montage.UpwardDirection);
        Vector oppositeTangent = Normalize(
            VectorBetween(opposite[0], opposite[1]),
            role + " касательная противоположного пояса");
        if (Dot(oppositeTangent, montage.UpwardDirection) < 0)
        {
            oppositeTangent = Scale(oppositeTangent, -1);
        }
        ValidateStraightMontageRegion(
            opposite,
            oppositeTangent,
            montage,
            role + " противоположный пояс");

        Point lowerAxis = IntersectLineWithPlane(
            axis[0],
            tangent,
            montage.LowerFlangePlane);
        Point upperAxis = IntersectLineWithPlane(
            axis[0],
            tangent,
            montage.UpperFlangePlane);
        Point insertEndAxis = IntersectLineWithPlane(
            axis[0],
            tangent,
            montage.InsertEndPlane);

        Point oppositeLowerAxis = IntersectLineWithPlane(
            opposite[0],
            oppositeTangent,
            montage.LowerFlangePlane);
        Vector innerNormal = Normalize(
            ProjectDirectionToPlane(
                VectorBetween(lowerAxis, oppositeLowerAxis),
                tangent),
            role + " направление к противоположному поясу");
        Vector widthAxis = Normalize(
            Cross(innerNormal, tangent),
            role + " направление ширины пояса");
        double thickness = FachwerkColumnGeometry.FlangeThickness;
        double width = FachwerkColumnGeometry.FlangeWidth;
        double halfWidth = width * 0.5;
        IReadOnlyList<Point> insertBoundary = new[]
        {
            Add(lowerAxis, widthAxis, -halfWidth),
            Add(insertEndAxis, widthAxis, -halfWidth),
            Add(insertEndAxis, widthAxis, halfWidth),
            Add(lowerAxis, widthAxis, halfWidth),
        };

        Point innerFaceCenter = Add(
            lowerAxis,
            innerNormal,
            thickness * 0.5);
        Point sourceEndInnerFace = Add(
            axis[0],
            innerNormal,
            thickness * 0.5);
        var innerPlane = new FachwerkLowerRigelPlane(
            innerFaceCenter,
            innerNormal,
            tangent);
        FachwerkLowerRigelPlane tubeCut = BuildTubeCutPlane(
            innerFaceCenter,
            tangent,
            widthAxis,
            innerPlane,
            tubeAxisPoint,
            tubeAxisDirection,
            tubeRadius,
            role);
        IReadOnlyList<Point> lowerFragmentBoundary =
            string.Equals(
                role,
                "outer-flange",
                StringComparison.OrdinalIgnoreCase)
                ? BuildLowerFragmentToSourceBoundary(
                    innerFaceCenter,
                    sourceEndInnerFace,
                    widthAxis,
                    halfWidth)
                : BuildLowerFragmentToTubeBoundary(
                    innerFaceCenter,
                    tangent,
                    widthAxis,
                    halfWidth,
                    tubeAxisPoint,
                    tubeAxisDirection,
                    tubeRadius,
                    role);

        return new FachwerkLowerRigelFlangeSplice(
            role,
            lowerAxis,
            upperAxis,
            insertEndAxis,
            new Point(axis[0]),
            tangent,
            Scale(innerNormal, -1),
            insertBoundary,
            lowerFragmentBoundary,
            tubeCut,
            thickness,
            width);
    }

    private static IReadOnlyList<Point> BuildLowerFragmentToSourceBoundary(
        Point montageInnerFacePoint,
        Point sourceEndInnerFacePoint,
        Vector widthAxis,
        double halfWidth)
    {
        return new[]
        {
            Add(montageInnerFacePoint, widthAxis, -halfWidth),
            Add(sourceEndInnerFacePoint, widthAxis, -halfWidth),
            Add(sourceEndInnerFacePoint, widthAxis, halfWidth),
            Add(montageInnerFacePoint, widthAxis, halfWidth),
        };
    }

    private static IReadOnlyList<Point> BuildLowerFragmentToTubeBoundary(
        Point montageInnerFacePoint,
        Vector upwardTangent,
        Vector widthAxis,
        double halfWidth,
        Point tubeAxisPoint,
        Vector tubeAxisDirection,
        double tubeRadius,
        string role)
    {
        Vector towardTube = Scale(upwardTangent, -1);
        Point firstMontageCorner = Add(
            montageInnerFacePoint,
            widthAxis,
            -halfWidth);
        Point secondMontageCorner = Add(
            montageInnerFacePoint,
            widthAxis,
            halfWidth);
        Point firstTubeCorner = IntersectRayWithCylinder(
            firstMontageCorner,
            towardTube,
            tubeAxisPoint,
            tubeAxisDirection,
            tubeRadius,
            role + " первая кромка");
        Point secondTubeCorner = IntersectRayWithCylinder(
            secondMontageCorner,
            towardTube,
            tubeAxisPoint,
            tubeAxisDirection,
            tubeRadius,
            role + " вторая кромка");

        return new[]
        {
            firstMontageCorner,
            firstTubeCorner,
            secondTubeCorner,
            secondMontageCorner,
        };
    }

    private static Point IntersectRayWithCylinder(
        Point rayOrigin,
        Vector rayDirection,
        Point cylinderAxisPoint,
        Vector cylinderAxisDirection,
        double radius,
        string label)
    {
        Vector axis = Normalize(
            cylinderAxisDirection,
            label + " ось трубы");
        Vector direction = Normalize(
            rayDirection,
            label + " направление к трубе");
        Vector relativePerpendicular = RejectFromAxis(
            VectorBetween(cylinderAxisPoint, rayOrigin),
            axis);
        Vector directionPerpendicular = RejectFromAxis(direction, axis);
        double[] forwardDistances = RadiusIntersections(
                relativePerpendicular,
                directionPerpendicular,
                radius)
            .Where(distance => distance >= -1e-5)
            .Select(distance => Math.Max(0, distance))
            .OrderBy(distance => distance)
            .ToArray();
        if (forwardDistances.Length == 0)
        {
            throw new InvalidOperationException(
                "Продольная кромка '" + label +
                "' не пересекает центральную трубу ниже монтажного стыка.");
        }
        return Add(rayOrigin, direction, forwardDistances[0]);
    }

    private static FachwerkLowerRigelPlane BuildTubeCutPlane(
        Point innerFaceCenter,
        Vector upwardTangent,
        Vector widthAxis,
        FachwerkLowerRigelPlane innerPlane,
        Point tubeAxisPoint,
        Vector tubeAxisDirection,
        double tubeRadius,
        string role)
    {
        Vector towardTube = Normalize(
            ProjectDirectionToPlane(
                Scale(upwardTangent, -1),
                innerPlane.Normal),
            role + " направление к центральной трубе");
        Vector acrossFace = Normalize(
            ProjectDirectionToPlane(
                widthAxis,
                innerPlane.Normal),
            role + " направление поперек внутренней грани");
        IReadOnlyList<CylinderPlaneContact> contacts =
            FindCylinderPlaneContacts(
                innerFaceCenter,
                towardTube,
                acrossFace,
                tubeAxisPoint,
                tubeAxisDirection,
                tubeRadius);
        if (contacts.Count == 0)
        {
            throw new InvalidOperationException(
                "Внутренняя плоскость пояса '" + role +
                "' не пересекает центральную трубу по направлению детали.");
        }

        CylinderPlaneContact firstContact = contacts
            .OrderBy(item => item.Distance)
            .First();
        Point contact = Add(
            Add(
                innerFaceCenter,
                towardTube,
                firstContact.Distance),
            acrossFace,
            firstContact.WidthOffset);

        Vector axis = Normalize(
            tubeAxisDirection,
            "ось центральной трубы");
        Vector fromAxis = VectorBetween(
            ProjectPointToLine(contact, tubeAxisPoint, axis),
            contact);
        Vector cutNormal = Subtract(
            fromAxis,
            Scale(
                innerPlane.Normal,
                Dot(fromAxis, innerPlane.Normal)));
        cutNormal = Normalize(
            cutNormal,
            role + " нормаль касательного реза трубы");
        if (Dot(cutNormal, towardTube) < 0)
        {
            cutNormal = Scale(cutNormal, -1);
        }
        return new FachwerkLowerRigelPlane(
            contact,
            cutNormal,
            innerPlane.Normal);
    }

    private static IReadOnlyList<CylinderPlaneContact>
        FindCylinderPlaneContacts(
            Point planeOrigin,
            Vector longitudinalDirection,
            Vector widthDirection,
            Point cylinderAxisPoint,
            Vector cylinderAxisDirection,
            double radius)
    {
        Vector axis = Normalize(
            cylinderAxisDirection,
            "ось центральной трубы");
        Vector relative = VectorBetween(
            cylinderAxisPoint,
            planeOrigin);
        Vector relativePerpendicular = RejectFromAxis(
            relative,
            axis);
        Vector directionPerpendicular = RejectFromAxis(
            longitudinalDirection,
            axis);
        Vector widthPerpendicular = RejectFromAxis(
            widthDirection,
            axis);
        var contacts = new List<CylinderPlaneContact>();

        double widthMagnitudeSquared = Dot(
            widthPerpendicular,
            widthPerpendicular);
        if (widthMagnitudeSquared > Epsilon)
        {
            Vector reducedOrigin = Subtract(
                relativePerpendicular,
                Scale(
                    widthPerpendicular,
                    Dot(
                        widthPerpendicular,
                        relativePerpendicular) /
                    widthMagnitudeSquared));
            Vector reducedDirection = Subtract(
                directionPerpendicular,
                Scale(
                    widthPerpendicular,
                    Dot(
                        widthPerpendicular,
                        directionPerpendicular) /
                    widthMagnitudeSquared));
            foreach (double distance in RadiusIntersections(
                         reducedOrigin,
                         reducedDirection,
                         radius))
            {
                double widthOffset =
                    -Dot(
                        widthPerpendicular,
                        AddScaled(
                            relativePerpendicular,
                            directionPerpendicular,
                            distance)) /
                    widthMagnitudeSquared;
                contacts.Add(
                    new CylinderPlaneContact(
                        distance,
                        widthOffset));
            }
        }
        else
        {
            foreach (double distance in RadiusIntersections(
                         relativePerpendicular,
                         directionPerpendicular,
                         radius))
            {
                contacts.Add(
                    new CylinderPlaneContact(distance, 0));
            }
        }

        return contacts;
    }

    private static IReadOnlyList<double> RadiusIntersections(
        Vector relativePerpendicular,
        Vector directionPerpendicular,
        double radius)
    {
        double a = Dot(
            directionPerpendicular,
            directionPerpendicular);
        if (a < Epsilon)
        {
            return Array.Empty<double>();
        }
        double b = 2 * Dot(
            directionPerpendicular,
            relativePerpendicular);
        double c =
            Dot(
                relativePerpendicular,
                relativePerpendicular) -
            radius * radius;
        double discriminant = b * b - 4 * a * c;
        if (discriminant < -1e-5)
        {
            return Array.Empty<double>();
        }

        double root = Math.Sqrt(Math.Max(0, discriminant));
        double first = (-b - root) / (2 * a);
        double second = (-b + root) / (2 * a);
        var result = new List<double>(2);
        if (first >= -1e-5)
        {
            result.Add(Math.Max(0, first));
        }
        if (second >= -1e-5 &&
            (result.Count == 0 ||
             Math.Abs(second - result[0]) > 1e-5))
        {
            result.Add(Math.Max(0, second));
        }
        result.Sort();
        return result;
    }

    private static Vector RejectFromAxis(
        Vector vector,
        Vector axis)
    {
        return Subtract(
            vector,
            Scale(axis, Dot(vector, axis)));
    }

    private static Vector ProjectDirectionToPlane(
        Vector direction,
        Vector planeNormal)
    {
        return Subtract(
            direction,
            Scale(
                planeNormal,
                Dot(direction, planeNormal)));
    }

    private static Vector AddScaled(
        Vector vector,
        Vector direction,
        double scale)
    {
        return new Vector(
            vector.X + direction.X * scale,
            vector.Y + direction.Y * scale,
            vector.Z + direction.Z * scale);
    }

    private static IReadOnlyList<Point> OrientToward(
        IReadOnlyList<Point> source,
        FachwerkLowerRigelPlane referencePlane,
        Vector upward)
    {
        double firstDistance = Math.Abs(
            SignedDistance(source[0], referencePlane));
        double lastDistance = Math.Abs(
            SignedDistance(source[source.Count - 1], referencePlane));
        IReadOnlyList<Point> oriented = firstDistance <= lastDistance
            ? source.Select(point => new Point(point)).ToArray()
            : source.Reverse().Select(point => new Point(point)).ToArray();
        Vector tangent = Normalize(
            VectorBetween(oriented[0], oriented[1]),
            "касательная выбранного пояса");
        if (Dot(tangent, upward) >= 0)
        {
            return oriented;
        }
        return oriented
            .Reverse()
            .Select(point => new Point(point))
            .ToArray();
    }

    private static void ValidateStraightMontageRegion(
        IReadOnlyList<Point> axis,
        Vector tangent,
        FachwerkLowerRigelMontageLayout montage,
        string role)
    {
        Point lower = IntersectLineWithPlane(
            axis[0],
            tangent,
            montage.LowerFlangePlane);
        Point end = IntersectLineWithPlane(
            axis[0],
            tangent,
            montage.InsertEndPlane);
        Vector delta = VectorBetween(lower, end);
        Vector perpendicular = Subtract(
            delta,
            Scale(tangent, Dot(delta, tangent)));
        if (Dot(delta, tangent) <= Epsilon ||
            Length(perpendicular) > 0.25)
        {
            throw new InvalidOperationException(
                "Монтажный узел пояса '" + role +
                "' должен находиться на одном прямом участке.");
        }
    }

    private static FachwerkLowerRigelPlane OffsetPlane(
        FachwerkLowerRigelPlane plane,
        double distance)
    {
        return new FachwerkLowerRigelPlane(
            Add(plane.Origin, plane.Normal, distance),
            plane.Normal,
            plane.AxisX);
    }

    private static Point ProjectToPlane(
        Point point,
        FachwerkLowerRigelPlane plane)
    {
        return Add(
            point,
            plane.Normal,
            -SignedDistance(point, plane));
    }

    private static Point ProjectPointToLine(
        Point point,
        Point linePoint,
        Vector lineDirection)
    {
        double parameter = Dot(
            VectorBetween(linePoint, point),
            lineDirection);
        return Add(linePoint, lineDirection, parameter);
    }

    internal static Point ReplaceNearestEndpoint(
        Point start,
        Point end,
        Point reference,
        Point replacement,
        out bool replacedStart)
    {
        replacedStart = Distance(start, reference) <= Distance(end, reference);
        return replacedStart ? new Point(replacement) : new Point(end);
    }

    internal static Point IntersectPolylineWithPlane(
        IReadOnlyList<Point> points,
        FachwerkLowerRigelPlane plane,
        string label)
    {
        Point best = null;
        double bestScore = double.MaxValue;
        for (int index = 0; index < points.Count - 1; index++)
        {
            Point first = points[index];
            Point second = points[index + 1];
            double firstDistance = SignedDistance(first, plane);
            double secondDistance = SignedDistance(second, plane);
            double denominator = firstDistance - secondDistance;
            if (Math.Abs(denominator) < Epsilon)
            {
                continue;
            }

            double parameter = firstDistance / denominator;
            Point intersection = Lerp(first, second, parameter);
            double outside =
                parameter < 0
                    ? -parameter
                    : parameter > 1
                        ? parameter - 1
                        : 0;
            if (outside < bestScore)
            {
                bestScore = outside;
                best = intersection;
            }
            if (outside <= Epsilon)
            {
                return intersection;
            }
        }

        if (best != null && bestScore < 2)
        {
            return best;
        }

        throw new InvalidOperationException(
            "Ось " + label + " не пересекает расчетную плоскость узла.");
    }

    internal static double SignedDistance(
        Point point,
        FachwerkLowerRigelPlane plane)
    {
        return Dot(VectorBetween(plane.Origin, point), plane.Normal);
    }

    internal static Vector DirectionAtPlane(
        IReadOnlyList<Point> points,
        FachwerkLowerRigelPlane plane)
    {
        int bestIndex = 0;
        double best = double.MaxValue;
        for (int index = 0; index < points.Count - 1; index++)
        {
            Point midpoint = Lerp(points[index], points[index + 1], 0.5);
            double score = Math.Abs(SignedDistance(midpoint, plane));
            if (score < best)
            {
                best = score;
                bestIndex = index;
            }
        }
        return Normalize(
            VectorBetween(points[bestIndex], points[bestIndex + 1]),
            "касательная оси трубы");
    }

    private static JointEndpoints SelectJointEndpoints(
        IReadOnlyList<Point> leftTubeAxis,
        IReadOnlyList<Point> rightTubeAxis,
        IReadOnlyList<Point> leftWebPath,
        IReadOnlyList<Point> rightWebPath)
    {
        Point tubeJointCenter = ClosestPairMidpoint(
            leftTubeAxis,
            rightTubeAxis);
        Point[] leftCandidates =
        {
            leftWebPath[0],
            leftWebPath[leftWebPath.Count - 1],
        };
        Point[] rightCandidates =
        {
            rightWebPath[0],
            rightWebPath[rightWebPath.Count - 1],
        };

        Point bestLeft = null;
        Point bestRight = null;
        double bestScore = double.MaxValue;
        foreach (Point left in leftCandidates)
        {
            foreach (Point right in rightCandidates)
            {
                double width = Distance(left, right);
                Point midpoint = Lerp(left, right, 0.5);
                double score = Distance(midpoint, tubeJointCenter);
                if (width < Epsilon)
                {
                    continue;
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    bestLeft = left;
                    bestRight = right;
                }
            }
        }

        if (bestLeft == null ||
            bestRight == null ||
            Distance(bestLeft, bestRight) < Epsilon)
        {
            throw new InvalidOperationException(
                "Не удалось определить начальные точки двух стенок узла.");
        }
        return new JointEndpoints(
            new Point(bestLeft),
            new Point(bestRight));
    }

    private static Point ClosestPairMidpoint(
        IReadOnlyList<Point> first,
        IReadOnlyList<Point> second)
    {
        Point bestFirst = null;
        Point bestSecond = null;
        double best = double.MaxValue;
        foreach (Point a in first)
        {
            foreach (Point b in second)
            {
                double distance = Distance(a, b);
                if (distance < best)
                {
                    best = distance;
                    bestFirst = a;
                    bestSecond = b;
                }
            }
        }
        return Lerp(bestFirst, bestSecond, 0.5);
    }

    private static IReadOnlyList<Point> OrientFrom(
        IReadOnlyList<Point> points,
        Point anchor)
    {
        if (Distance(points[0], anchor) <=
            Distance(points[points.Count - 1], anchor))
        {
            return points.Select(point => new Point(point)).ToArray();
        }

        return points
            .Reverse()
            .Select(point => new Point(point))
            .ToArray();
    }

    private static FachwerkLowerRigelTransition BuildTransition(
        string role,
        IReadOnlyList<Point> sourcePath,
        Vector outward,
        Vector webPlaneNormal,
        double halfGap,
        double width,
        double requestedLength)
    {
        IReadOnlyList<Point> path = TakePolylineLength(
            sourcePath,
            requestedLength);
        Vector startTangent = Normalize(
            VectorBetween(path[0], path[1]),
            role + " начальная касательная");
        Vector startOffset = Normalize(
            Cross(webPlaneNormal, startTangent),
            role + " направление ширины");
        Vector preferredOffset = startOffset.Z >= 0
            ? startOffset
            : Scale(startOffset, -1);

        var firstSide = new List<Point>(path.Count);
        var secondSide = new List<Point>(path.Count);
        for (int index = 0; index < path.Count; index++)
        {
            Vector tangent = PolylineTangent(path, index);
            Vector offset = Normalize(
                Cross(webPlaneNormal, tangent),
                role + " направление смещения");
            if (Dot(offset, preferredOffset) < 0)
            {
                offset = Scale(offset, -1);
            }
            Point moved = Add(path[index], outward, halfGap);
            firstSide.Add(Add(moved, offset, width * 0.5));
            secondSide.Add(Add(moved, offset, -width * 0.5));
        }

        var boundary = new List<Point>(
            firstSide.Count + secondSide.Count);
        boundary.AddRange(firstSide);
        for (int index = secondSide.Count - 1; index >= 0; index--)
        {
            boundary.Add(secondSide[index]);
        }

        Point endMidpoint = Lerp(
            firstSide[firstSide.Count - 1],
            secondSide[secondSide.Count - 1],
            0.5);
        Vector endTangent = PolylineTangent(
            path,
            path.Count - 1);
        var farPlane = new FachwerkLowerRigelPlane(
            endMidpoint,
            endTangent,
            webPlaneNormal);
        Point referenceOrigin = Add(path[0], outward, halfGap);
        var referencePlane = new FachwerkLowerRigelPlane(
            referenceOrigin,
            webPlaneNormal,
            startTangent);
        return new FachwerkLowerRigelTransition(
            role,
            RemoveConsecutiveDuplicates(boundary),
            RemoveConsecutiveDuplicates(firstSide),
            RemoveConsecutiveDuplicates(secondSide),
            referencePlane,
            Scale(outward, -1),
            farPlane);
    }

    private static IReadOnlyList<Point> TakePolylineLength(
        IReadOnlyList<Point> points,
        double requestedLength)
    {
        var result = new List<Point> { new Point(points[0]) };
        double remaining = requestedLength;
        for (int index = 0;
             index < points.Count - 1 && remaining > Epsilon;
             index++)
        {
            Point start = points[index];
            Point end = points[index + 1];
            double segmentLength = Distance(start, end);
            if (segmentLength < Epsilon)
            {
                continue;
            }
            if (segmentLength <= remaining + Epsilon)
            {
                result.Add(new Point(end));
                remaining -= segmentLength;
                continue;
            }

            result.Add(Lerp(start, end, remaining / segmentLength));
            remaining = 0;
        }

        if (remaining > 0.01)
        {
            throw new InvalidOperationException(
                "Ось стенки короче требуемой переходной пластины на " +
                remaining.ToString("0.###") + " мм.");
        }
        if (result.Count < 2)
        {
            throw new InvalidOperationException(
                "Переходная пластина не получила рабочую траекторию.");
        }
        return RemoveConsecutiveDuplicates(result);
    }

    private static Vector PolylineTangent(
        IReadOnlyList<Point> points,
        int index)
    {
        if (index <= 0)
        {
            return Normalize(
                VectorBetween(points[0], points[1]),
                "касательная начала полилинии");
        }
        if (index >= points.Count - 1)
        {
            return Normalize(
                VectorBetween(
                    points[points.Count - 2],
                    points[points.Count - 1]),
                "касательная конца полилинии");
        }

        Vector before = Normalize(
            VectorBetween(points[index - 1], points[index]),
            "касательная полилинии");
        Vector after = Normalize(
            VectorBetween(points[index], points[index + 1]),
            "касательная полилинии");
        Vector sum = Add(before, after);
        return Length(sum) < Epsilon ? after : Normalize(sum, "касательная");
    }

    private static IReadOnlyList<Point> BuildEndPlateBoundary(
        Point oldAxisPoint,
        Vector oldAxisDirection,
        FachwerkLowerRigelPlane plane,
        double diameter)
    {
        return BuildCylinderSection(
            oldAxisPoint,
            oldAxisDirection,
            plane,
            diameter * 0.5 + EndPlateOverbuild,
            SectionSegments);
    }

    private static IReadOnlyList<Point> BuildCylinderSection(
        Point axisPoint,
        Vector axisDirection,
        FachwerkLowerRigelPlane plane,
        double radius,
        int segments)
    {
        Vector direction = Normalize(
            axisDirection,
            "ось цилиндра");
        double incidence = Math.Abs(Dot(direction, plane.Normal));
        if (incidence < 0.05)
        {
            throw new InvalidOperationException(
                "Ось трубы почти параллельна плоскости стыка.");
        }
        Point center = IntersectLineWithPlane(
            axisPoint,
            direction,
            plane);
        Vector minorAxis = Cross(direction, plane.Normal);
        if (Length(minorAxis) < Epsilon)
        {
            minorAxis = plane.AxisX;
        }
        minorAxis = Normalize(minorAxis, "малая ось сечения трубы");
        Vector majorAxis = Normalize(
            Cross(plane.Normal, minorAxis),
            "большая ось сечения трубы");
        double majorRadius = radius / incidence;

        var result = new List<Point>(segments);
        for (int index = 0; index < segments; index++)
        {
            double angle = 2 * Math.PI * index / segments;
            Point point = Add(
                Add(
                    center,
                    minorAxis,
                    radius * Math.Cos(angle)),
                majorAxis,
                majorRadius * Math.Sin(angle));
            result.Add(point);
        }
        return result;
    }

    private static Point IntersectLineWithPlane(
        Point linePoint,
        Vector lineDirection,
        FachwerkLowerRigelPlane plane)
    {
        double denominator = Dot(lineDirection, plane.Normal);
        if (Math.Abs(denominator) < Epsilon)
        {
            throw new InvalidOperationException(
                "Линия не пересекает расчетную плоскость.");
        }
        double parameter =
            Dot(VectorBetween(linePoint, plane.Origin), plane.Normal) /
            denominator;
        return Add(linePoint, lineDirection, parameter);
    }

    private static bool IsInsideBoundary(
        Point point,
        IReadOnlyList<Point> boundary,
        FachwerkLowerRigelPlane plane)
    {
        if (point == null) throw new ArgumentNullException(nameof(point));
        if (boundary == null || boundary.Count < 3)
        {
            throw new InvalidOperationException(
                "Овальный контур содержит меньше трёх точек.");
        }
        if (plane == null) throw new ArgumentNullException(nameof(plane));

        Vector delta = VectorBetween(plane.Origin, point);
        var projectedPoint = new Point2(
            Dot(delta, plane.AxisX),
            Dot(delta, plane.AxisY));
        IReadOnlyList<Point2> polygon = Project(boundary, plane);
        bool inside = false;
        for (int index = 0, previous = polygon.Count - 1;
             index < polygon.Count;
             previous = index++)
        {
            Point2 first = polygon[previous];
            Point2 second = polygon[index];
            if (IsOnSegment(projectedPoint, first, second))
            {
                return true;
            }

            bool crosses =
                (first.Y > projectedPoint.Y) !=
                (second.Y > projectedPoint.Y);
            if (!crosses)
            {
                continue;
            }
            double crossingX = first.X +
                (projectedPoint.Y - first.Y) *
                (second.X - first.X) /
                (second.Y - first.Y);
            if (projectedPoint.X < crossingX)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static bool IsOnSegment(
        Point2 point,
        Point2 start,
        Point2 end)
    {
        double cross = Cross2(
            end.X - start.X,
            end.Y - start.Y,
            point.X - start.X,
            point.Y - start.Y);
        if (Math.Abs(cross) > 1e-5)
        {
            return false;
        }
        double minimumX = Math.Min(start.X, end.X) - 1e-5;
        double maximumX = Math.Max(start.X, end.X) + 1e-5;
        double minimumY = Math.Min(start.Y, end.Y) - 1e-5;
        double maximumY = Math.Max(start.Y, end.Y) + 1e-5;
        return point.X >= minimumX && point.X <= maximumX &&
            point.Y >= minimumY && point.Y <= maximumY;
    }

    private static IReadOnlyList<Point2> ClipConvex(
        IReadOnlyList<Point2> subject,
        IReadOnlyList<Point2> clip)
    {
        var output = EnsureCounterClockwise(subject).ToList();
        IReadOnlyList<Point2> orientedClip = EnsureCounterClockwise(clip);
        for (int edgeIndex = 0;
             edgeIndex < orientedClip.Count;
             edgeIndex++)
        {
            Point2 edgeStart = orientedClip[edgeIndex];
            Point2 edgeEnd =
                orientedClip[(edgeIndex + 1) % orientedClip.Count];
            var input = output;
            output = new List<Point2>();
            if (input.Count == 0)
            {
                break;
            }
            Point2 previous = input[input.Count - 1];
            bool previousInside = Inside(previous, edgeStart, edgeEnd);
            foreach (Point2 current in input)
            {
                bool currentInside = Inside(current, edgeStart, edgeEnd);
                if (currentInside != previousInside)
                {
                    output.Add(
                        IntersectLines(
                            previous,
                            current,
                            edgeStart,
                            edgeEnd));
                }
                if (currentInside)
                {
                    output.Add(current);
                }
                previous = current;
                previousInside = currentInside;
            }
        }
        return RemoveConsecutiveDuplicates(output);
    }

    private static bool Inside(
        Point2 point,
        Point2 edgeStart,
        Point2 edgeEnd)
    {
        return Cross2(
                   edgeEnd.X - edgeStart.X,
                   edgeEnd.Y - edgeStart.Y,
                   point.X - edgeStart.X,
                   point.Y - edgeStart.Y) >= -1e-6;
    }

    private static Point2 IntersectLines(
        Point2 first,
        Point2 second,
        Point2 clipFirst,
        Point2 clipSecond)
    {
        double rx = second.X - first.X;
        double ry = second.Y - first.Y;
        double sx = clipSecond.X - clipFirst.X;
        double sy = clipSecond.Y - clipFirst.Y;
        double denominator = Cross2(rx, ry, sx, sy);
        if (Math.Abs(denominator) < Epsilon)
        {
            return second;
        }
        double qpx = clipFirst.X - first.X;
        double qpy = clipFirst.Y - first.Y;
        double parameter = Cross2(qpx, qpy, sx, sy) / denominator;
        return new Point2(
            first.X + parameter * rx,
            first.Y + parameter * ry);
    }

    private static IReadOnlyList<Point2> EnsureCounterClockwise(
        IReadOnlyList<Point2> points)
    {
        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point2 first = points[index];
            Point2 second = points[(index + 1) % points.Count];
            area += first.X * second.Y - second.X * first.Y;
        }
        return area >= 0
            ? points.ToArray()
            : points.Reverse().ToArray();
    }

    private static IReadOnlyList<Point2> Project(
        IReadOnlyList<Point> points,
        FachwerkLowerRigelPlane plane)
    {
        return points
            .Select(point =>
            {
                Vector delta = VectorBetween(plane.Origin, point);
                return new Point2(
                    Dot(delta, plane.AxisX),
                    Dot(delta, plane.AxisY));
            })
            .ToArray();
    }

    private static Point Unproject(
        Point2 point,
        FachwerkLowerRigelPlane plane)
    {
        return Add(
            Add(plane.Origin, plane.AxisX, point.X),
            plane.AxisY,
            point.Y);
    }

    private static IReadOnlyList<Point> RemoveConsecutiveDuplicates(
        IReadOnlyList<Point> points)
    {
        var result = new List<Point>(points.Count);
        foreach (Point point in points)
        {
            if (result.Count == 0 ||
                Distance(result[result.Count - 1], point) > 1e-5)
            {
                result.Add(new Point(point));
            }
        }
        if (result.Count > 1 &&
            Distance(result[0], result[result.Count - 1]) < 1e-5)
        {
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    private static IReadOnlyList<Point2> RemoveConsecutiveDuplicates(
        IReadOnlyList<Point2> points)
    {
        var result = new List<Point2>(points.Count);
        foreach (Point2 point in points)
        {
            if (result.Count == 0 ||
                Distance(result[result.Count - 1], point) > 1e-6)
            {
                result.Add(point);
            }
        }
        if (result.Count > 1 &&
            Distance(result[0], result[result.Count - 1]) < 1e-6)
        {
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    internal static Point Add(
        Point point,
        Vector vector,
        double factor = 1)
    {
        return new Point(
            point.X + vector.X * factor,
            point.Y + vector.Y * factor,
            point.Z + vector.Z * factor);
    }

    internal static Vector Add(Vector first, Vector second)
    {
        return new Vector(
            first.X + second.X,
            first.Y + second.Y,
            first.Z + second.Z);
    }

    internal static Vector Subtract(Vector first, Vector second)
    {
        return new Vector(
            first.X - second.X,
            first.Y - second.Y,
            first.Z - second.Z);
    }

    internal static Vector Scale(Vector vector, double factor)
    {
        return new Vector(
            vector.X * factor,
            vector.Y * factor,
            vector.Z * factor);
    }

    internal static Vector VectorBetween(Point first, Point second)
    {
        return new Vector(
            second.X - first.X,
            second.Y - first.Y,
            second.Z - first.Z);
    }

    internal static Vector Cross(Vector first, Vector second)
    {
        return new Vector(
            first.Y * second.Z - first.Z * second.Y,
            first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);
    }

    internal static double Dot(Vector first, Vector second)
    {
        return
            first.X * second.X +
            first.Y * second.Y +
            first.Z * second.Z;
    }

    internal static double Length(Vector vector)
    {
        return Math.Sqrt(Dot(vector, vector));
    }

    internal static Vector Normalize(Vector vector, string label)
    {
        double length = Length(vector);
        if (length < Epsilon)
        {
            throw new InvalidOperationException(
                "Не удалось определить " + label + ".");
        }
        return Scale(vector, 1.0 / length);
    }

    internal static double Distance(Point first, Point second)
    {
        return Length(VectorBetween(first, second));
    }

    private static double Distance(Point2 first, Point2 second)
    {
        double dx = first.X - second.X;
        double dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static Point Lerp(Point first, Point second, double parameter)
    {
        return new Point(
            first.X + (second.X - first.X) * parameter,
            first.Y + (second.Y - first.Y) * parameter,
            first.Z + (second.Z - first.Z) * parameter);
    }

    private static double Cross2(
        double firstX,
        double firstY,
        double secondX,
        double secondY)
    {
        return firstX * secondY - firstY * secondX;
    }

    private static void RequirePath(
        IReadOnlyList<Point> points,
        string label)
    {
        if (points == null || points.Count < 2)
        {
            throw new ArgumentException(
                label + " должна содержать минимум две точки.");
        }
        if (points.Any(point =>
                point == null ||
                !IsFinite(point.X) ||
                !IsFinite(point.Y) ||
                !IsFinite(point.Z)))
        {
            throw new ArgumentException(
                label + " содержит недопустимую точку.");
        }
    }

    private static void RequirePositive(double value, string label)
    {
        if (!IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                label,
                value,
                label + " должен быть больше нуля.");
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private sealed class JointEndpoints
    {
        internal JointEndpoints(Point leftAnchor, Point rightAnchor)
        {
            LeftAnchor = leftAnchor;
            RightAnchor = rightAnchor;
        }

        public Point LeftAnchor { get; }
        public Point RightAnchor { get; }
    }

    private readonly struct Point2
    {
        internal Point2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }
}
