#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    internal sealed class TeklaCanonicalContourGeometry
    {
        public TeklaCanonicalContourGeometry(
            IReadOnlyList<string> vertexIds,
            IReadOnlyList<Point> points)
        {
            VertexIds = vertexIds;
            Points = points;
        }

        public IReadOnlyList<string> VertexIds { get; }
        public IReadOnlyList<Point> Points { get; }
    }

    internal sealed class TeklaPhysicalContourEdgeGeometry
    {
        public TeklaPhysicalContourEdgeGeometry(Point start, Point end)
        {
            Start = start;
            End = end;
        }

        public Point Start { get; }
        public Point End { get; }
    }

    /// <summary>
    /// The single contour ordering boundary used both by base plate creation
    /// and topology-addressed contour operations. Vertex ids and native points
    /// are reversed together when Tekla's canonical winding requires it.
    /// </summary>
    internal static class TeklaContourPlateGeometry
    {
        public static TeklaCanonicalContourGeometry BuildCanonicalGlobalContour(
            TeklaCreateContourPlatePayload payload)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));

            var vertexById = payload.Contour.Vertices.ToDictionary(
                static vertex => vertex.Id,
                StringComparer.Ordinal);
            var ordered = payload.Contour.Edges
                .Select(edge => vertexById[edge.StartVertexId])
                .ToList();

            var signedArea2 = 0d;
            for (var index = 0; index < ordered.Count; index++)
            {
                var next = ordered[(index + 1) % ordered.Count];
                signedArea2 += (ordered[index].Point.X * next.Point.Y) -
                    (next.Point.X * ordered[index].Point.Y);
            }
            if (signedArea2 < 0) ordered.Reverse();

            var normalOffset = payload.ExtrusionSide switch
            {
                "positive" => payload.ThicknessMm / 2,
                "negative" => -payload.ThicknessMm / 2,
                _ => 0,
            };
            var points = ordered.Select(vertex => new Point(
                payload.Plane.Origin.X + (payload.Plane.AxisX.X * vertex.Point.X) + (payload.Plane.AxisY.X * vertex.Point.Y) + (payload.Plane.AxisZ.X * normalOffset),
                payload.Plane.Origin.Y + (payload.Plane.AxisX.Y * vertex.Point.X) + (payload.Plane.AxisY.Y * vertex.Point.Y) + (payload.Plane.AxisZ.Y * normalOffset),
                payload.Plane.Origin.Z + (payload.Plane.AxisX.Z * vertex.Point.X) + (payload.Plane.AxisY.Z * vertex.Point.Y) + (payload.Plane.AxisZ.Z * normalOffset)))
                .ToArray();

            return new TeklaCanonicalContourGeometry(
                ordered.Select(static vertex => vertex.Id).ToArray(),
                points);
        }

        public static TeklaPhysicalContourEdgeGeometry BuildPhysicalContourEdge(
            TeklaCreateContourPlatePayload payload,
            string contourId,
            string edgeId,
            string side)
        {
            if (!string.Equals(payload.Contour.Id, contourId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Contour '{contourId}' is not materialized by the target ContourPlate.");
            if (side != "positive" && side != "negative")
                throw new InvalidOperationException($"Physical contour edge side '{side}' is invalid.");

            var matches = payload.Contour.Edges
                .Where(edge => string.Equals(edge.Id, edgeId, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Edge '{edgeId}' has {matches.Length} exact matches in contour '{contourId}'.");
            if (!string.Equals(matches[0].Kind, "line", StringComparison.Ordinal))
                throw new InvalidOperationException($"Physical EdgeChamfer target '{edgeId}' is not a line edge.");

            var canonical = BuildCanonicalGlobalContour(payload);
            var pointByVertexId = canonical.VertexIds
                .Select((vertexId, index) => new { vertexId, point = canonical.Points[index] })
                .ToDictionary(static item => item.vertexId, static item => item.point, StringComparer.Ordinal);
            var edge = matches[0];
            if (!pointByVertexId.TryGetValue(edge.StartVertexId, out var start) ||
                !pointByVertexId.TryGetValue(edge.EndVertexId, out var end))
                throw new InvalidOperationException($"Edge '{edgeId}' references vertices outside canonical contour '{contourId}'.");

            var faceOffset = (side == "positive" ? 1d : -1d) * payload.ThicknessMm / 2d;
            return new TeklaPhysicalContourEdgeGeometry(
                Offset(start, payload.Plane.AxisZ, faceOffset),
                Offset(end, payload.Plane.AxisZ, faceOffset));
        }

        private static Point Offset(Point point, TeklaVector3 direction, double distance)
            => new(
                point.X + (direction.X * distance),
                point.Y + (direction.Y * distance),
                point.Z + (direction.Z * distance));
    }
}
