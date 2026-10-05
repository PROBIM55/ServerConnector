#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model.UI;

namespace Structura.Tekla.Fachwerk;

internal static class FachwerkRigelVisualization
{
    private const double MarkerHalfSizeMm = 55;

    public static void DrawLayout(
        FachwerkRigelLayout layout,
        IReadOnlyList<Point> points,
        FachwerkRigelPluginData data)
    {
        if (layout == null || points == null || data == null) return;
        if (points.Count != layout.Points.Count) return;

        var drawer = new GraphicsDrawer();
        var color = ResolveColor(layout.Code);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var control = layout.Points[index];
            var elevationDelta = data.ReadElevationDelta(
                layout.Code,
                index,
                control.ElevationDeltaMm);
            var transverseOffset = data.ReadTransverseOffset(
                layout.Code,
                index,
                control.CalculatedTransverseOffsetMm + layout.TransverseOffsetMm);

            DrawMarker(drawer, point, color);
            drawer.DrawText(
                new Point(
                    point.X + MarkerHalfSizeMm * 1.5,
                    point.Y + MarkerHalfSizeMm * 1.5,
                    point.Z + MarkerHalfSizeMm),
                FormatLabel(layout, control, elevationDelta, transverseOffset),
                color);
        }
    }

    private static void DrawMarker(GraphicsDrawer drawer, Point point, Color color)
    {
        drawer.DrawLineSegment(
            new Point(point.X - MarkerHalfSizeMm, point.Y, point.Z),
            new Point(point.X + MarkerHalfSizeMm, point.Y, point.Z),
            color);
        drawer.DrawLineSegment(
            new Point(point.X, point.Y - MarkerHalfSizeMm, point.Z),
            new Point(point.X, point.Y + MarkerHalfSizeMm, point.Z),
            color);
        drawer.DrawLineSegment(
            new Point(point.X, point.Y, point.Z - MarkerHalfSizeMm),
            new Point(point.X, point.Y, point.Z + MarkerHalfSizeMm),
            color);
    }

    private static string FormatLabel(
        FachwerkRigelLayout layout,
        FachwerkRigelControlPoint point,
        double elevationDelta,
        double transverseOffset)
    {
        return (layout.Label ?? ("Ригель " + layout.Code)) +
               " · " + point.Label +
               " | ΔZ " + Signed(elevationDelta) + " мм" +
               " | поперечное " + Signed(transverseOffset) + " мм";
    }

    private static string Signed(double value) =>
        (value > 0 ? "+" : string.Empty) +
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static Color ResolveColor(string code)
    {
        if (string.Equals(code, "101", StringComparison.OrdinalIgnoreCase))
            return new Color(0.05, 0.55, 0.95);
        if (string.Equals(code, "103", StringComparison.OrdinalIgnoreCase))
            return new Color(0.95, 0.45, 0.05);
        return new Color(0.75, 0.15, 0.85);
    }
}
