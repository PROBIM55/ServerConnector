#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Solid;

namespace Structura.Tekla.Fachwerk;

internal static class FachwerkRigelClearanceVisualization
{
    private const double LongitudinalEdgeAlignment = 0.75;
    private const double Epsilon = 1e-7;

    public static void DrawLayout(
        FachwerkRigelCatalog catalog,
        FachwerkRigelLayout layout,
        IReadOnlyList<Beam> beams)
    {
        if (catalog == null || layout == null || beams == null || beams.Count == 0) return;

        foreach (var profile in catalog.Profiles)
        {
            var beam = ResolveBeam(profile, beams);
            if (beam == null) continue;

            var solid = beam.GetSolid();
            if (solid == null || !solid.IsValid()) continue;
            if (!TryResolveTopAndBottomFaces(solid, out var top, out var bottom)) continue;

            DrawFaceMeasurements(profile, beam, top, true);
            DrawFaceMeasurements(profile, beam, bottom, false);
        }
    }

    private static Beam ResolveBeam(
        FachwerkRigelSupportProfile profile,
        IReadOnlyList<Beam> beams)
    {
        var origin = ToPoint(profile.Origin);
        var profileNormal = Normalize(Cross(ToVector(profile.AxisX), ToVector(profile.AxisY)));
        Beam best = null;
        var bestScore = double.PositiveInfinity;

        foreach (var beam in beams)
        {
            var direction = Subtract(beam.EndPoint, beam.StartPoint);
            var denominator = Dot(direction, profileNormal);
            if (Math.Abs(denominator) < Epsilon) continue;

            var parameter = Dot(Subtract(origin, beam.StartPoint), profileNormal) / denominator;
            if (parameter < -1e-5 || parameter > 1 + 1e-5) continue;

            var score = Math.Abs(parameter - 0.5);
            if (score >= bestScore) continue;
            best = beam;
            bestScore = score;
        }
        return best;
    }

    private static void DrawFaceMeasurements(
        FachwerkRigelSupportProfile profile,
        Beam beam,
        FacePlane face,
        bool top)
    {
        DrawPathMeasurements(
            profile,
            profile.OuterPath,
            beam,
            face,
            top);
        DrawPathMeasurements(
            profile,
            profile.InnerPath,
            beam,
            face,
            top);
    }

    private static void DrawPathMeasurements(
        FachwerkRigelSupportProfile profile,
        IReadOnlyList<FachwerkRigelPoint> path,
        Beam beam,
        FacePlane face,
        bool top)
    {
        if (!TryIntersectPath(profile, path, face, out var axisPoint)) return;

        var longitudinalDirection = ProjectOnPlane(
            Subtract(beam.EndPoint, beam.StartPoint),
            face.Normal);
        if (Length(longitudinalDirection) < Epsilon) return;
        longitudinalDirection = Normalize(longitudinalDirection);

        var measureDirection = Cross(face.Normal, longitudinalDirection);
        if (Length(measureDirection) < Epsilon) return;
        measureDirection = Normalize(measureDirection);

        DrawMeasurement(
            axisPoint,
            measureDirection,
            beam,
            face,
            top);
    }

    private static void DrawMeasurement(
        Point reference,
        Vector measureDirection,
        Beam beam,
        FacePlane face,
        bool top)
    {
        if (!TryFindNearestLongitudinalEdgePoint(
                reference,
                measureDirection,
                beam,
                face,
                out var target))
            return;
        var distanceVector = Subtract(target, reference);
        var distance = Length(distanceVector);
        if (distance < Epsilon) return;
        var direction = Normalize(distanceVector);
        var color = top
            ? new Color(0.1, 0.65, 0.95)
            : new Color(0.95, 0.55, 0.1);
        var drawer = new GraphicsDrawer();
        drawer.DrawLineSegment(reference, target, color);

        var tick = Normalize(Cross(face.Normal, direction));
        DrawTick(drawer, reference, tick, color);
        DrawTick(drawer, target, tick, color);

        var midpoint = Lerp(reference, target, 0.5);
        var textPoint = Add(midpoint, Scale(tick, 35));
        drawer.DrawText(
            textPoint,
            distance.ToString("0", CultureInfo.InvariantCulture) + " мм",
            color);
    }

    private static bool TryFindNearestLongitudinalEdgePoint(
        Point reference,
        Vector measureDirection,
        Beam beam,
        FacePlane face,
        out Point target)
    {
        target = null;
        var beamDirection = ProjectOnPlane(
            Subtract(beam.EndPoint, beam.StartPoint),
            face.Normal);
        if (Length(beamDirection) < Epsilon) return false;
        beamDirection = Normalize(beamDirection);

        var transverseDirection = Cross(face.Normal, measureDirection);
        if (Length(transverseDirection) < Epsilon) return false;
        transverseDirection = Normalize(transverseDirection);

        var nearestDistance = double.PositiveInfinity;
        for (var index = 0; index < face.Boundary.Count; index++)
        {
            var start = face.Boundary[index];
            var end = face.Boundary[(index + 1) % face.Boundary.Count];
            var edge = Subtract(end, start);
            var edgeLength = Length(edge);
            if (edgeLength < Epsilon) continue;

            var edgeDirection = Scale(edge, 1 / edgeLength);
            if (Math.Abs(Dot(edgeDirection, beamDirection)) < LongitudinalEdgeAlignment)
                continue;

            var startTransverse = Dot(Subtract(start, reference), transverseDirection);
            var endTransverse = Dot(Subtract(end, reference), transverseDirection);
            var transverseDelta = endTransverse - startTransverse;
            if (Math.Abs(transverseDelta) < Epsilon) continue;

            var parameter = -startTransverse / transverseDelta;
            if (parameter < -Epsilon || parameter > 1 + Epsilon) continue;
            var candidate = Add(start, Scale(edge, parameter));
            var distance = Math.Abs(Dot(
                Subtract(candidate, reference),
                measureDirection));
            if (distance >= nearestDistance) continue;

            nearestDistance = distance;
            target = candidate;
        }

        return target != null;
    }

    private static void DrawTick(
        GraphicsDrawer drawer,
        Point point,
        Vector tickDirection,
        Color color)
    {
        const double halfLength = 22;
        drawer.DrawLineSegment(
            Add(point, Scale(tickDirection, -halfLength)),
            Add(point, Scale(tickDirection, halfLength)),
            color);
    }

    private static bool TryIntersectPath(
        FachwerkRigelSupportProfile profile,
        IReadOnlyList<FachwerkRigelPoint> path,
        FacePlane face,
        out Point intersection)
    {
        intersection = null;
        if (path == null || path.Count < 2) return false;

        for (var index = 1; index < path.Count; index++)
        {
            var start = ToGlobal(profile, path[index - 1]);
            var end = ToGlobal(profile, path[index]);
            var startDistance = Dot(Subtract(start, face.Origin), face.Normal);
            var endDistance = Dot(Subtract(end, face.Origin), face.Normal);

            if (Math.Abs(startDistance) < Epsilon)
            {
                intersection = start;
                return true;
            }
            if (startDistance * endDistance > 0) continue;

            var denominator = startDistance - endDistance;
            if (Math.Abs(denominator) < Epsilon) continue;
            var parameter = startDistance / denominator;
            if (parameter < -Epsilon || parameter > 1 + Epsilon) continue;
            intersection = Lerp(start, end, parameter);
            return true;
        }
        return false;
    }

    private static bool TryResolveTopAndBottomFaces(
        global::Tekla.Structures.Model.Solid solid,
        out FacePlane top,
        out FacePlane bottom)
    {
        top = null;
        bottom = null;
        var faces = solid.GetFaceEnumerator();
        while (faces.MoveNext())
        {
            if (faces.Current is not Face face) continue;
            var normal = Normalize(face.Normal);
            if (Math.Abs(normal.Z) < 0.65) continue;
            var vertices = ReadVertices(face);
            if (vertices.Count < 3) continue;

            var centroid = Centroid(vertices);
            var candidate = new FacePlane(centroid, normal, vertices);
            if (top == null || centroid.Z > top.Origin.Z) top = candidate;
            if (bottom == null || centroid.Z < bottom.Origin.Z) bottom = candidate;
        }
        return top != null && bottom != null &&
               Math.Abs(top.Origin.Z - bottom.Origin.Z) > Epsilon;
    }

    private static List<Point> ReadVertices(Face face)
    {
        var result = new List<Point>();
        var loops = face.GetLoopEnumerator();
        if (!loops.MoveNext() || loops.Current is not Loop loop) return result;
        var vertices = loop.GetVertexEnumerator();
        while (vertices.MoveNext())
            if (vertices.Current is Point point) result.Add(point);
        return result;
    }

    private static Point ToGlobal(
        FachwerkRigelSupportProfile profile,
        FachwerkRigelPoint point)
    {
        return new Point(
            profile.Origin.X +
            profile.AxisX.X * point.X +
            profile.AxisY.X * point.Y,
            profile.Origin.Y +
            profile.AxisX.Y * point.X +
            profile.AxisY.Y * point.Y,
            profile.Origin.Z +
            profile.AxisX.Z * point.X +
            profile.AxisY.Z * point.Y);
    }

    private static Point ToPoint(FachwerkRigelPoint point) =>
        new(point.X, point.Y, point.Z);

    private static Vector ToVector(FachwerkRigelPoint point) =>
        new(point.X, point.Y, point.Z);

    private static Point Centroid(IReadOnlyList<Point> points)
    {
        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
            z += point.Z;
        }
        return new Point(x / points.Count, y / points.Count, z / points.Count);
    }

    private static Vector ProjectOnPlane(Vector vector, Vector normal) =>
        Subtract(vector, Scale(normal, Dot(vector, normal)));

    private static Vector Cross(Vector left, Vector right) =>
        new(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);

    private static double Dot(Vector left, Vector right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static double Length(Vector vector) => Math.Sqrt(Dot(vector, vector));

    private static Vector Normalize(Vector vector)
    {
        var length = Length(vector);
        return length < Epsilon
            ? new Vector()
            : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static Vector Scale(Vector vector, double scalar) =>
        new(vector.X * scalar, vector.Y * scalar, vector.Z * scalar);

    private static Vector Subtract(Vector left, Vector right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    private static Vector Subtract(Point left, Point right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    private static Point Add(Point point, Vector vector) =>
        new(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);

    private static Point Lerp(Point left, Point right, double parameter) =>
        new(
            left.X + (right.X - left.X) * parameter,
            left.Y + (right.Y - left.Y) * parameter,
            left.Z + (right.Z - left.Z) * parameter);

    private sealed class FacePlane
    {
        public FacePlane(
            Point origin,
            Vector normal,
            IReadOnlyList<Point> boundary)
        {
            Origin = origin;
            Normal = normal;
            Boundary = boundary;
        }

        public Point Origin { get; }
        public Vector Normal { get; }
        public IReadOnlyList<Point> Boundary { get; }
    }
}
