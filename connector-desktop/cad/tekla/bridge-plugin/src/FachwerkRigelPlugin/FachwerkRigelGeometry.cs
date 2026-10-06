#nullable disable

using System;
using System.Collections.Generic;
using Tekla.Structures.Geometry3d;

namespace Structura.Tekla.Fachwerk;

public static class FachwerkRigelGeometry
{
    private const double Epsilon = 1e-6;

    public static IReadOnlyList<Point> ResolveLayout(
        FachwerkRigelCatalog catalog,
        FachwerkRigelLayout layout,
        FachwerkRigelPluginData data)
    {
        var result = new List<Point>(layout.Points.Count);
        for (var index = 0; index < layout.Points.Count; index++)
        {
            var control = layout.Points[index];
            var delta = data.ReadElevationDelta(layout.Code, index, control.ElevationDeltaMm);
            var elevation = layout.BaseElevationMm + delta;
            var leftProfile = catalog.RequireProfile(control.LeftProfileNumber);
            var rightProfile = catalog.RequireProfile(control.RightProfileNumber);
            var left = ResolveSupportPoint(leftProfile, elevation);
            var right = ResolveSupportPoint(rightProfile, elevation);
            var t = Clamp(control.StationT, 0, 1);
            var source = Lerp(left, right, t);
            var direction = ResolveShiftDirection(leftProfile.AxisX, rightProfile.AxisX, t);
            var offset = data.ReadTransverseOffset(
                layout.Code,
                index,
                control.CalculatedTransverseOffsetMm + layout.TransverseOffsetMm);
            result.Add(new Point(
                source.X + direction.X * offset,
                source.Y + direction.Y * offset,
                source.Z));
        }
        return result;
    }

    public static void ValidateDefaultGeometry(FachwerkRigelCatalog catalog, double toleranceMm = 0.5)
    {
        var defaults = new FachwerkRigelPluginData();
        foreach (var layout in catalog.Layouts)
        {
            var resolved = ResolveLayout(catalog, layout, defaults);
            for (var index = 0; index < resolved.Count; index++)
            {
                var expected = layout.Points[index].ResolvedPosition;
                if (expected == null || Distance(resolved[index], expected) > toleranceMm)
                {
                    throw new InvalidOperationException(
                        "Встроенная геометрия ригеля " + layout.Code +
                        " не воспроизводит утвержденную точку " + index + ".");
                }
            }
        }
    }

    private static Point ResolveSupportPoint(FachwerkRigelSupportProfile profile, double elevation)
    {
        var localY = elevation - profile.Origin.Z;
        var innerX = XAtY(profile.InnerPath, localY);
        var outerX = XAtY(profile.OuterPath, localY);
        if (!innerX.HasValue || !outerX.HasValue)
        {
            throw new InvalidOperationException(
                "Отметка " + elevation.ToString("0.###") +
                " находится вне оси стойки СФ" + profile.ProfileNumber + ".");
        }
        var localX = (innerX.Value + outerX.Value) / 2;
        return new Point(
            profile.Origin.X + profile.AxisX.X * localX + profile.AxisY.X * localY,
            profile.Origin.Y + profile.AxisX.Y * localX + profile.AxisY.Y * localY,
            profile.Origin.Z + profile.AxisX.Z * localX + profile.AxisY.Z * localY);
    }

    private static double? XAtY(IReadOnlyList<FachwerkRigelPoint> path, double y)
    {
        double? bestX = null;
        var bestDistance = double.PositiveInfinity;
        for (var index = 1; index < path.Count; index++)
        {
            var left = path[index - 1];
            var right = path[index];
            var minY = Math.Min(left.Y, right.Y) - Epsilon;
            var maxY = Math.Max(left.Y, right.Y) + Epsilon;
            if (y < minY || y > maxY) continue;
            var deltaY = right.Y - left.Y;
            var t = Math.Abs(deltaY) < Epsilon ? 0.5 : (y - left.Y) / deltaY;
            var x = left.X + (right.X - left.X) * t;
            var distance = Math.Abs(y - (left.Y + right.Y) / 2);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestX = x;
            }
        }
        return bestX;
    }

    private static FachwerkRigelPoint ResolveShiftDirection(
        FachwerkRigelPoint left,
        FachwerkRigelPoint right,
        double t)
    {
        var rightX = right.X;
        var rightY = right.Y;
        if (left.X * rightX + left.Y * rightY < 0)
        {
            rightX = -rightX;
            rightY = -rightY;
        }
        var x = left.X * (1 - t) + rightX * t;
        var y = left.Y * (1 - t) + rightY * t;
        var length = Math.Sqrt(x * x + y * y);
        if (length < Epsilon)
            throw new InvalidOperationException("Не удалось определить направление поперечного смещения ригеля.");
        return new FachwerkRigelPoint { X = x / length, Y = y / length, Z = 0 };
    }

    private static Point Lerp(Point left, Point right, double t) => new Point(
        left.X + (right.X - left.X) * t,
        left.Y + (right.Y - left.Y) * t,
        left.Z + (right.Z - left.Z) * t);

    private static double Distance(Point point, FachwerkRigelPoint expected)
    {
        var dx = point.X - expected.X;
        var dy = point.Y - expected.Y;
        var dz = point.Z - expected.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
