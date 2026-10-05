#nullable disable

using System;
using System.Collections.Generic;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace Structura.Tekla.Fachwerk;

internal sealed class FachwerkRigelFacePlane
{
    internal FachwerkRigelFacePlane(
        Point origin,
        Vector axisX,
        Vector axisY,
        Vector normal,
        IReadOnlyList<Point> boundary)
    {
        Origin = origin;
        AxisX = axisX;
        AxisY = axisY;
        Normal = normal;
        Boundary = boundary;
    }

    public Point Origin { get; }
    public Vector AxisX { get; }
    public Vector AxisY { get; }
    public Vector Normal { get; }
    public IReadOnlyList<Point> Boundary { get; }

    internal FachwerkRigelFacePlane Offset(double distance)
    {
        return new FachwerkRigelFacePlane(
            FachwerkColumnRigelConnectionGeometry.Add(
                Origin,
                Normal,
                distance),
            AxisX,
            AxisY,
            Normal,
            Boundary);
    }

    internal FachwerkRigelFacePlane OrientedToward(Point keepPoint)
    {
        if (keepPoint == null)
            throw new ArgumentNullException(nameof(keepPoint));

        if (FachwerkColumnRigelConnectionGeometry.Dot(
                keepPoint,
                Origin,
                Normal) >= 0)
        {
            return this;
        }

        return new FachwerkRigelFacePlane(
            Origin,
            AxisX,
            new Vector(-AxisY.X, -AxisY.Y, -AxisY.Z),
            new Vector(-Normal.X, -Normal.Y, -Normal.Z),
            Boundary);
    }

    internal Plane ToTeklaPlane()
    {
        return new Plane
        {
            Origin = new Point(Origin),
            AxisX = new Vector(AxisX),
            AxisY = new Vector(AxisY),
        };
    }
}

internal sealed class FachwerkRigelFacePair
{
    internal FachwerkRigelFacePair(
        FachwerkRigelFacePlane top,
        FachwerkRigelFacePlane bottom)
    {
        Top = top;
        Bottom = bottom;
    }

    public FachwerkRigelFacePlane Top { get; }
    public FachwerkRigelFacePlane Bottom { get; }
}

internal sealed class FachwerkBevelSection
{
    internal FachwerkBevelSection(
        string role,
        string semanticRole,
        Part part,
        Point anchor,
        Vector intoLowerSection,
        Vector outward,
        Vector crossAxis,
        double thickness,
        double crossSpan,
        double crossOverrun = 100.0,
        Vector angleReferenceNormal = null)
    {
        Role = role;
        SemanticRole = string.IsNullOrWhiteSpace(semanticRole)
            ? string.Empty
            : semanticRole.Trim().ToLowerInvariant();
        Part = part;
        Anchor = anchor;
        IntoLowerSection = intoLowerSection;
        Outward = outward;
        CrossAxis = crossAxis;
        Thickness = thickness;
        CrossSpan = crossSpan;
        CrossOverrun = crossOverrun;
        AngleReferenceNormal = angleReferenceNormal == null
            ? null
            : new Vector(angleReferenceNormal);
    }

    public string Role { get; }
    public string SemanticRole { get; }
    public Part Part { get; }
    public Point Anchor { get; }
    public Vector IntoLowerSection { get; }
    public Vector Outward { get; }
    public Vector CrossAxis { get; }
    public double Thickness { get; }
    public double CrossSpan { get; }
    public double CrossOverrun { get; }
    public Vector AngleReferenceNormal { get; }
}

internal static class FachwerkColumnRigelConnectionGeometry
{
    private const double Epsilon = 1e-7;
    private const double EndBandTolerance = 3.0;

    internal static FachwerkRigelFacePair ReadRigelFaces(Part rigel)
    {
        if (rigel == null) throw new ArgumentNullException(nameof(rigel));
        Solid solid = rigel.GetSolid();
        if (solid == null || !solid.IsValid())
        {
            throw new InvalidOperationException(
                "Не удалось получить валидный Solid выбранного ригеля.");
        }

        var faces = new List<FaceCandidate>();
        FaceEnumerator faceEnumerator = solid.GetFaceEnumerator();
        while (faceEnumerator.MoveNext())
        {
            Face face = faceEnumerator.Current;
            var normal = Normalize(face.Normal, "нормаль грани ригеля");
            IReadOnlyList<Point> boundary = ReadLargestLoop(face);
            if (boundary.Count < 3)
            {
                continue;
            }
            faces.Add(new FaceCandidate(
                normal,
                boundary,
                PolygonArea(boundary)));
        }
        if (faces.Count < 2)
        {
            throw new InvalidOperationException(
                "У Solid ригеля не найдены верхняя и нижняя грани.");
        }

        FaceCandidate top = SelectExtremeFace(faces, true);
        FaceCandidate bottom = SelectExtremeFace(faces, false);
        if (top.Normal.Z <= 0.05 || bottom.Normal.Z >= -0.05)
        {
            throw new InvalidOperationException(
                "Верхняя или нижняя грань ригеля не определена достоверно. " +
                "Проверьте ориентацию выбранной детали.");
        }

        return new FachwerkRigelFacePair(
            BuildPlane(top),
            BuildPlane(bottom));
    }

    internal static IReadOnlyList<FachwerkBevelSection> BuildLowerBevelSections(
        FachwerkRigelFacePlane fittingPlane,
        IReadOnlyList<FachwerkConnectionPart> lowerParts)
    {
        if (lowerParts == null || lowerParts.Count != 4)
        {
            throw new ArgumentException(
                "Для разделок нужны четыре детали нижней секции.",
                nameof(lowerParts));
        }
        return BuildBevelSections(fittingPlane, lowerParts);
    }

    internal static IReadOnlyList<FachwerkBevelSection> BuildBevelSections(
        FachwerkRigelFacePlane fittingPlane,
        IReadOnlyList<FachwerkConnectionPart> parts)
    {
        if (fittingPlane == null)
            throw new ArgumentNullException(nameof(fittingPlane));
        if (parts == null || parts.Count < 2)
        {
            throw new ArgumentException(
                "Для разделок нужны минимум две детали сечения.",
                nameof(parts));
        }

        var samples = new List<PartEndSample>(parts.Count);
        foreach (FachwerkConnectionPart item in parts)
        {
            IReadOnlyList<Point> points =
                ReadPointsNearestPlane(item.Part, fittingPlane);
            samples.Add(new PartEndSample(
                item.Role,
                item.SemanticRole,
                item.Part,
                points,
                Average(points)));
        }

        var sectionCenter = new Point();
        foreach (PartEndSample sample in samples)
        {
            sectionCenter.X += sample.Center.X;
            sectionCenter.Y += sample.Center.Y;
            sectionCenter.Z += sample.Center.Z;
        }
        sectionCenter.X /= samples.Count;
        sectionCenter.Y /= samples.Count;
        sectionCenter.Z /= samples.Count;

        var result = new List<FachwerkBevelSection>(samples.Count);
        foreach (PartEndSample sample in samples)
        {
            Vector outward = ResolveOutwardNormal(
                sample,
                sectionCenter,
                fittingPlane);
            Vector crossAxis = Normalize(
                Cross(fittingPlane.Normal, outward),
                "поперечное направление детали " + sample.Role);

            ProjectionBounds bounds = Measure(
                sample.Points,
                fittingPlane.Origin,
                outward,
                crossAxis);
            if (bounds.OutwardSpan <= Epsilon ||
                bounds.CrossSpan <= Epsilon)
            {
                throw new InvalidOperationException(
                    "Не удалось определить фактическое сечение детали '" +
                    sample.Role + "' у ригеля.");
            }

            Point anchor = Add(
                Add(
                    fittingPlane.Origin,
                    outward,
                    bounds.OutwardMid),
                crossAxis,
                bounds.CrossMid);
            result.Add(new FachwerkBevelSection(
                sample.Role,
                sample.SemanticRole,
                sample.Part,
                anchor,
                fittingPlane.Normal,
                outward,
                crossAxis,
                bounds.OutwardSpan,
                bounds.CrossSpan));
        }
        return result;
    }

    internal static Point ReadPartCenter(Part part)
    {
        if (part == null) throw new ArgumentNullException(nameof(part));
        Solid solid = part.GetSolid();
        if (solid == null || !solid.IsValid())
        {
            throw new InvalidOperationException(
                "Не удалось получить Solid выбранной детали.");
        }

        return new Point(
            0.5 * (solid.MinimumPoint.X + solid.MaximumPoint.X),
            0.5 * (solid.MinimumPoint.Y + solid.MaximumPoint.Y),
            0.5 * (solid.MinimumPoint.Z + solid.MaximumPoint.Z));
    }

    internal static void ValidateSectionSide(
        IReadOnlyList<FachwerkConnectionPart> parts,
        FachwerkRigelFacePlane plane,
        string sectionLabel)
    {
        if (parts == null || parts.Count != 4)
        {
            throw new ArgumentException(
                "Для проверки секции нужны четыре детали.",
                nameof(parts));
        }

        foreach (FachwerkConnectionPart part in parts)
        {
            Point center = ReadPartCenter(part.Part);
            double signedDistance = Dot(center, plane.Origin, plane.Normal);
            if (signedDistance <= Epsilon)
            {
                throw new InvalidOperationException(
                    "Деталь ID " + part.Part.Identifier.ID +
                    " не относится к " + sectionLabel +
                    " секции относительно выбранного ригеля.");
            }
        }
    }

    internal static double MeasureGapToPlane(
        Part part,
        FachwerkRigelFacePlane plane)
    {
        if (part == null) throw new ArgumentNullException(nameof(part));
        if (plane == null) throw new ArgumentNullException(nameof(plane));

        Solid solid = part.GetSolid();
        if (solid == null || !solid.IsValid())
        {
            throw new InvalidOperationException(
                "Не удалось проверить Solid детали после подгонки.");
        }

        double minimum = double.PositiveInfinity;
        foreach (Point point in ReadAllVertices(solid))
        {
            minimum = Math.Min(
                minimum,
                Math.Abs(Dot(point, plane.Origin, plane.Normal)));
        }
        return minimum;
    }

    internal static Point Add(Point point, Vector vector, double scale)
    {
        return new Point(
            point.X + vector.X * scale,
            point.Y + vector.Y * scale,
            point.Z + vector.Z * scale);
    }

    internal static Vector Normalize(Vector value, string label)
    {
        double length = Math.Sqrt(
            value.X * value.X +
            value.Y * value.Y +
            value.Z * value.Z);
        if (length <= Epsilon)
        {
            throw new InvalidOperationException(
                "Нулевой вектор: " + label + ".");
        }
        return new Vector(
            value.X / length,
            value.Y / length,
            value.Z / length);
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

    internal static double Dot(Point point, Point origin, Vector direction)
    {
        return
            (point.X - origin.X) * direction.X +
            (point.Y - origin.Y) * direction.Y +
            (point.Z - origin.Z) * direction.Z;
    }

    private static Vector Subtract(Point first, Point second)
    {
        return new Vector(
            first.X - second.X,
            first.Y - second.Y,
            first.Z - second.Z);
    }

    private static Vector ProjectToPlane(Vector value, Vector normal)
    {
        double alongNormal = Dot(value, normal);
        return new Vector(
            value.X - normal.X * alongNormal,
            value.Y - normal.Y * alongNormal,
            value.Z - normal.Z * alongNormal);
    }

    private static Vector ResolveOutwardNormal(
        PartEndSample sample,
        Point sectionCenter,
        FachwerkRigelFacePlane fittingPlane)
    {
        Vector desired = Normalize(
            ProjectToPlane(
                Subtract(sample.Center, sectionCenter),
                fittingPlane.Normal),
            "направление от центра сечения к детали " + sample.Role);

        Solid solid = sample.Part.GetSolid();
        if (solid == null || !solid.IsValid())
        {
            throw new InvalidOperationException(
                "Не удалось получить Solid детали '" + sample.Role + "'.");
        }

        double partEndDistance =
            MinimumDistanceToPlane(sample.Points, fittingPlane);
        Vector selected = null;
        double selectedAlignment = double.NegativeInfinity;
        double selectedArea = 0;
        FaceEnumerator faces = solid.GetFaceEnumerator();
        while (faces.MoveNext())
        {
            Face face = faces.Current;
            IReadOnlyList<Point> boundary = ReadLargestLoop(face);
            if (boundary.Count < 3 ||
                MinimumDistanceToPlane(boundary, fittingPlane) >
                partEndDistance + EndBandTolerance)
            {
                continue;
            }

            Vector projected = ProjectToPlane(
                Normalize(face.Normal, "нормаль грани детали " + sample.Role),
                fittingPlane.Normal);
            double projectedLength = Math.Sqrt(Dot(projected, projected));
            if (projectedLength <= 0.5)
            {
                continue;
            }
            projected = new Vector(
                projected.X / projectedLength,
                projected.Y / projectedLength,
                projected.Z / projectedLength);

            double alignment = Dot(projected, desired);
            if (alignment < 0.5)
            {
                continue;
            }
            double area = PolygonArea(boundary);
            if (selected == null ||
                alignment > selectedAlignment + 1e-6 ||
                (Math.Abs(alignment - selectedAlignment) <= 1e-6 &&
                 area > selectedArea))
            {
                selected = projected;
                selectedAlignment = alignment;
                selectedArea = area;
            }
        }

        if (selected == null)
        {
            throw new InvalidOperationException(
                "Не удалось достоверно определить наружную грань детали '" +
                sample.Role + "' возле ригеля.");
        }
        return selected;
    }

    private static double MinimumDistanceToPlane(
        IReadOnlyList<Point> points,
        FachwerkRigelFacePlane plane)
    {
        double minimum = double.PositiveInfinity;
        foreach (Point point in points)
        {
            minimum = Math.Min(
                minimum,
                Math.Abs(Dot(point, plane.Origin, plane.Normal)));
        }
        return minimum;
    }

    private static FaceCandidate SelectExtremeFace(
        IReadOnlyList<FaceCandidate> faces,
        bool top)
    {
        FaceCandidate selected = null;
        double selectedZ = top
            ? double.NegativeInfinity
            : double.PositiveInfinity;
        foreach (FaceCandidate candidate in faces)
        {
            bool better = top
                ? candidate.Normal.Z > selectedZ + 1e-6
                : candidate.Normal.Z < selectedZ - 1e-6;
            bool sameDirection =
                Math.Abs(candidate.Normal.Z - selectedZ) <= 1e-6;
            if (selected == null ||
                better ||
                (sameDirection && candidate.Area > selected.Area))
            {
                selected = candidate;
                selectedZ = candidate.Normal.Z;
            }
        }
        return selected;
    }

    private static FachwerkRigelFacePlane BuildPlane(
        FaceCandidate candidate)
    {
        Point origin = Average(candidate.Boundary);
        Vector axisX = LongestProjectedEdge(
            candidate.Boundary,
            candidate.Normal);
        Vector axisY = Normalize(
            Cross(candidate.Normal, axisX),
            "вторая ось плоскости грани ригеля");
        return new FachwerkRigelFacePlane(
            origin,
            axisX,
            axisY,
            candidate.Normal,
            candidate.Boundary);
    }

    private static Vector LongestProjectedEdge(
        IReadOnlyList<Point> points,
        Vector normal)
    {
        Vector selected = null;
        double selectedLength = 0;
        for (var index = 0; index < points.Count; index++)
        {
            Point first = points[index];
            Point second = points[(index + 1) % points.Count];
            Vector projected = ProjectToPlane(
                Subtract(second, first),
                normal);
            double length = Math.Sqrt(Dot(projected, projected));
            if (length <= selectedLength)
            {
                continue;
            }
            selected = projected;
            selectedLength = length;
        }
        return Normalize(selected, "первая ось плоскости грани ригеля");
    }

    private static IReadOnlyList<Point> ReadLargestLoop(Face face)
    {
        IReadOnlyList<Point> selected = Array.Empty<Point>();
        double selectedArea = 0;
        LoopEnumerator loopEnumerator = face.GetLoopEnumerator();
        while (loopEnumerator.MoveNext())
        {
            var points = new List<Point>();
            VertexEnumerator vertexEnumerator =
                loopEnumerator.Current.GetVertexEnumerator();
            while (vertexEnumerator.MoveNext())
            {
                points.Add(new Point(vertexEnumerator.Current));
            }
            double area = PolygonArea(points);
            if (points.Count >= 3 && area > selectedArea)
            {
                selected = points;
                selectedArea = area;
            }
        }
        return selected;
    }

    private static double PolygonArea(IReadOnlyList<Point> points)
    {
        if (points == null || points.Count < 3)
        {
            return 0;
        }
        Point origin = points[0];
        var areaVector = new Vector();
        for (var index = 1; index < points.Count - 1; index++)
        {
            Vector first = Subtract(points[index], origin);
            Vector second = Subtract(points[index + 1], origin);
            Vector cross = Cross(first, second);
            areaVector.X += cross.X;
            areaVector.Y += cross.Y;
            areaVector.Z += cross.Z;
        }
        return 0.5 * Math.Sqrt(Dot(areaVector, areaVector));
    }

    private static IReadOnlyList<Point> ReadPointsNearestPlane(
        Part part,
        FachwerkRigelFacePlane plane)
    {
        Solid solid = part.GetSolid();
        if (solid == null || !solid.IsValid())
        {
            throw new InvalidOperationException(
                "Не удалось получить Solid детали нижней секции.");
        }
        IReadOnlyList<Point> all = ReadAllVertices(solid);
        if (all.Count < 3)
        {
            throw new InvalidOperationException(
                "У детали нижней секции недостаточно вершин.");
        }

        double minimum = double.PositiveInfinity;
        foreach (Point point in all)
        {
            minimum = Math.Min(
                minimum,
                Math.Abs(Dot(point, plane.Origin, plane.Normal)));
        }

        var selected = new List<Point>();
        foreach (Point point in all)
        {
            double distance =
                Math.Abs(Dot(point, plane.Origin, plane.Normal));
            if (distance <= minimum + EndBandTolerance)
            {
                AddUnique(selected, point);
            }
        }
        if (selected.Count < 3)
        {
            throw new InvalidOperationException(
                "Не удалось получить торцевое сечение детали нижней секции.");
        }
        return selected;
    }

    private static IReadOnlyList<Point> ReadAllVertices(Solid solid)
    {
        var points = new List<Point>();
        FaceEnumerator faces = solid.GetFaceEnumerator();
        while (faces.MoveNext())
        {
            LoopEnumerator loops = faces.Current.GetLoopEnumerator();
            while (loops.MoveNext())
            {
                VertexEnumerator vertices =
                    loops.Current.GetVertexEnumerator();
                while (vertices.MoveNext())
                {
                    AddUnique(points, vertices.Current);
                }
            }
        }
        return points;
    }

    private static void AddUnique(ICollection<Point> target, Point point)
    {
        foreach (Point existing in target)
        {
            double dx = existing.X - point.X;
            double dy = existing.Y - point.Y;
            double dz = existing.Z - point.Z;
            if (dx * dx + dy * dy + dz * dz <= 1e-8)
            {
                return;
            }
        }
        target.Add(new Point(point));
    }

    private static Point Average(IReadOnlyList<Point> points)
    {
        if (points == null || points.Count == 0)
        {
            throw new InvalidOperationException(
                "Нельзя вычислить центр пустого набора точек.");
        }
        var result = new Point();
        foreach (Point point in points)
        {
            result.X += point.X;
            result.Y += point.Y;
            result.Z += point.Z;
        }
        result.X /= points.Count;
        result.Y /= points.Count;
        result.Z /= points.Count;
        return result;
    }

    private static ProjectionBounds Measure(
        IReadOnlyList<Point> points,
        Point origin,
        Vector outward,
        Vector crossAxis)
    {
        double minimumOutward = double.PositiveInfinity;
        double maximumOutward = double.NegativeInfinity;
        double minimumCross = double.PositiveInfinity;
        double maximumCross = double.NegativeInfinity;
        foreach (Point point in points)
        {
            double outwardValue = Dot(point, origin, outward);
            double crossValue = Dot(point, origin, crossAxis);
            minimumOutward = Math.Min(minimumOutward, outwardValue);
            maximumOutward = Math.Max(maximumOutward, outwardValue);
            minimumCross = Math.Min(minimumCross, crossValue);
            maximumCross = Math.Max(maximumCross, crossValue);
        }
        return new ProjectionBounds(
            minimumOutward,
            maximumOutward,
            minimumCross,
            maximumCross);
    }

    private sealed class FaceCandidate
    {
        internal FaceCandidate(
            Vector normal,
            IReadOnlyList<Point> boundary,
            double area)
        {
            Normal = normal;
            Boundary = boundary;
            Area = area;
        }

        public Vector Normal { get; }
        public IReadOnlyList<Point> Boundary { get; }
        public double Area { get; }
    }

    private sealed class PartEndSample
    {
        internal PartEndSample(
            string role,
            string semanticRole,
            Part part,
            IReadOnlyList<Point> points,
            Point center)
        {
            Role = role;
            SemanticRole = semanticRole;
            Part = part;
            Points = points;
            Center = center;
        }

        public string Role { get; }
        public string SemanticRole { get; }
        public Part Part { get; }
        public IReadOnlyList<Point> Points { get; }
        public Point Center { get; }
    }

    private sealed class ProjectionBounds
    {
        internal ProjectionBounds(
            double minimumOutward,
            double maximumOutward,
            double minimumCross,
            double maximumCross)
        {
            OutwardSpan = maximumOutward - minimumOutward;
            OutwardMid = 0.5 * (minimumOutward + maximumOutward);
            CrossSpan = maximumCross - minimumCross;
            CrossMid = 0.5 * (minimumCross + maximumCross);
        }

        public double OutwardSpan { get; }
        public double OutwardMid { get; }
        public double CrossSpan { get; }
        public double CrossMid { get; }
    }
}

internal sealed class FachwerkConnectionPart
{
    internal FachwerkConnectionPart(
        string role,
        string semanticRole,
        Part part)
    {
        Role = role;
        SemanticRole = semanticRole;
        Part = part;
    }

    public string Role { get; }
    public string SemanticRole { get; }
    public Part Part { get; }
}
