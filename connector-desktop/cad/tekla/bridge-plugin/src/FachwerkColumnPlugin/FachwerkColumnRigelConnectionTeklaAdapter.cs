#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal static class FachwerkColumnRigelConnectionTeklaAdapter
{
    private const double CutterPlaneOverrun = 100.0;
    private const double BevelEdgeLengthFactor = 3.0;
    private const double FittingContactTolerance = 2.0;

    internal static Fitting CreateFitting(
        FachwerkConnectionPart target,
        FachwerkRigelFacePlane plane,
        int componentId)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (plane == null) throw new ArgumentNullException(nameof(plane));

        FachwerkRigelFacePlane orientedPlane = plane.OrientedToward(
            FachwerkColumnRigelConnectionGeometry.ReadPartCenter(target.Part));
        var fitting = new Fitting
        {
            Father = target.Part,
            Plane = orientedPlane.ToTeklaPlane(),
        };
        if (!fitting.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала подгонку детали '" + target.Role + "'.");
        }
        TrySetUserProperty(fitting, "FK_ROLE", "rigel-fit-" + target.Role);
        TrySetUserProperty(
            fitting,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
        fitting.Modify();
        target.Part.Select();
        double gap =
            FachwerkColumnRigelConnectionGeometry.MeasureGapToPlane(
                target.Part,
                orientedPlane);
        if (gap > FittingContactTolerance)
        {
            throw new InvalidOperationException(
                "Подгонка детали '" + target.Role +
                "' не достигла плоскости ригеля: зазор " +
                gap.ToString("0.###", CultureInfo.InvariantCulture) +
                " мм.");
        }
        return fitting;
    }

    internal static BooleanPart CreateBevel(
        FachwerkBevelSection section,
        double angleDeg,
        double rootFace,
        int componentId)
    {
        if (section == null) throw new ArgumentNullException(nameof(section));
        if (!(angleDeg > 0 && angleDeg < 90))
        {
            throw new ArgumentOutOfRangeException(
                nameof(angleDeg),
                angleDeg,
                "Угол разделки должен быть больше 0 и меньше 90 градусов.");
        }
        if (!(rootFace >= 0 && rootFace < section.Thickness))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootFace),
                rootFace,
                "Притупление должно быть меньше фактической толщины детали.");
        }

        ContourPlate cutter = BuildCutter(
            section,
            angleDeg,
            rootFace,
            componentId);
        BooleanPart booleanPart = null;
        try
        {
            if (!cutter.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала режущий клин детали '" +
                    section.Role + "'.");
            }
            booleanPart = new BooleanPart
            {
                Father = section.Part,
                Type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
            };
            booleanPart.SetOperativePart(cutter);
            if (!booleanPart.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала разделку детали '" +
                    section.Role + "'.");
            }
            TrySetUserProperty(
                booleanPart,
                "FK_ROLE",
                "rigel-bevel-" + section.Role);
            TrySetUserProperty(
                booleanPart,
                "FK_OWNER",
                componentId.ToString(CultureInfo.InvariantCulture));
            booleanPart.Modify();
            try { cutter.Delete(); }
            catch { }
            return booleanPart;
        }
        catch
        {
            if (booleanPart != null &&
                booleanPart.Identifier != null &&
                booleanPart.Identifier.ID != 0)
            {
                try { booleanPart.Delete(); }
                catch { }
            }
            if (cutter.Identifier != null &&
                cutter.Identifier.ID != 0)
            {
                try { cutter.Delete(); }
                catch { }
            }
            throw;
        }
    }

    private static ContourPlate BuildCutter(
        FachwerkBevelSection section,
        double angleDeg,
        double rootFace,
        int componentId)
    {
        IReadOnlyList<Point> contour;
        if (section.AngleReferenceNormal != null)
        {
            contour = BuildPlaneReferencedCutterBoundary(
                section,
                angleDeg,
                rootFace);
        }
        else
        {
            IReadOnlyList<FachwerkBevelCutterProfilePoint> profile =
                BuildCutterProfile(
                    section.Thickness,
                    angleDeg,
                    rootFace);
            var legacyContour = new List<Point>(profile.Count);
            foreach (FachwerkBevelCutterProfilePoint point in profile)
            {
                legacyContour.Add(
                    At(
                        section.Anchor,
                        section.IntoLowerSection,
                        point.Along,
                        section.Outward,
                        point.Outward));
            }
            contour = legacyContour;
        }

        var cutter = new ContourPlate
        {
            Name = BuildCutterName(componentId, section.Role),
            Class = BooleanPart.BooleanOperativeClassName,
        };
        cutter.Profile.ProfileString =
            "PL" +
            (section.CrossSpan + 2 * section.CrossOverrun).ToString(
                "0.###",
                CultureInfo.InvariantCulture);
        cutter.Material.MaterialString =
            string.IsNullOrWhiteSpace(section.Part.Material?.MaterialString)
                ? "C355-5"
                : section.Part.Material.MaterialString;
        cutter.Position.Depth = Position.DepthEnum.MIDDLE;

        foreach (Point point in contour)
        {
            if (!cutter.AddContourPoint(
                    new ContourPoint(point, new Chamfer())))
            {
                throw new InvalidOperationException(
                    "Tekla отклонила вершину режущего клина детали '" +
                    section.Role + "'.");
            }
        }
        TrySetUserProperty(
            cutter,
            "FK_ROLE",
            "rigel-bevel-cutter-" + section.Role);
        TrySetUserProperty(
            cutter,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
        return cutter;
    }

    internal static IReadOnlyList<Point> BuildPlaneReferencedCutterBoundary(
        FachwerkBevelSection section,
        double angleDeg,
        double rootFace)
    {
        if (section == null) throw new ArgumentNullException(nameof(section));
        if (section.AngleReferenceNormal == null)
            throw new ArgumentException(
                "Не задана опорная плоскость угла разделки.",
                nameof(section));
        if (!(angleDeg > 0 && angleDeg < 90))
            throw new ArgumentOutOfRangeException(nameof(angleDeg));
        if (!(rootFace >= 0 && rootFace < section.Thickness))
            throw new ArgumentOutOfRangeException(nameof(rootFace));

        Vector edge = Normalize(section.CrossAxis, "контактная кромка");
        Vector outward = Normalize(section.Outward, "направление толщины");
        Vector intoPart = Normalize(
            section.IntoLowerSection,
            "направление внутрь детали");
        Vector referenceNormal = Normalize(
            section.AngleReferenceNormal,
            "нормаль плоскости ригеля");
        Vector reference = Normalize(
            Cross(edge, referenceNormal),
            "направление плоскости ригеля");
        if (Dot(reference, outward) < 0)
            reference = Scale(reference, -1);

        double referenceOutward = Dot(reference, outward);
        if (referenceOutward <= 0.02)
        {
            throw new InvalidOperationException(
                "Плоскость ригеля почти параллельна толщине детали; " +
                "невозможно построить заданное притупление.");
        }

        Point innerContact = Add(
            section.Anchor,
            outward,
            -section.Thickness * 0.5);
        Point rootPoint = Add(
            innerContact,
            reference,
            rootFace / referenceOutward);
        Point referenceOuter = Add(
            innerContact,
            reference,
            section.Thickness / referenceOutward);

        double radians = angleDeg * Math.PI / 180.0;
        Vector first = RotateAroundAxis(reference, edge, radians);
        Vector second = RotateAroundAxis(reference, edge, -radians);
        Point bevelOuter = SelectBevelOuterPoint(
            rootPoint,
            referenceOuter,
            first,
            second,
            outward,
            intoPart,
            section.Thickness - rootFace);
        Vector bevelDirection = Normalize(
            Subtract(bevelOuter, rootPoint),
            "направление плоскости разделки");
        double bevelOutward = Dot(bevelDirection, outward);
        Point extendedOuter = Add(
            bevelOuter,
            bevelDirection,
            CutterPlaneOverrun / bevelOutward);
        Vector outsidePart = Scale(intoPart, -1);
        double outsideSpan = CutterPlaneOverrun * 2.0;

        return new[]
        {
            rootPoint,
            extendedOuter,
            Add(extendedOuter, outsidePart, outsideSpan),
            Add(rootPoint, outsidePart, outsideSpan),
        };
    }

    private static Point SelectBevelOuterPoint(
        Point rootPoint,
        Point referenceOuter,
        Vector first,
        Vector second,
        Vector outward,
        Vector intoPart,
        double remainingThickness)
    {
        Point best = null;
        double bestInside = double.NegativeInfinity;
        foreach (Vector candidate in new[] { first, second })
        {
            double candidateOutward = Dot(candidate, outward);
            if (candidateOutward <= 0.02)
                continue;
            Point outer = Add(
                rootPoint,
                candidate,
                remainingThickness / candidateOutward);
            double inside = Dot(
                Subtract(outer, referenceOuter),
                intoPart);
            if (inside > bestInside)
            {
                best = outer;
                bestInside = inside;
            }
        }
        if (best == null || bestInside <= 0.001)
        {
            throw new InvalidOperationException(
                "Заданный угол разделки не направляет скос внутрь детали.");
        }
        return best;
    }

    private static Vector RotateAroundAxis(
        Vector value,
        Vector axis,
        double radians)
    {
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        Vector cross = Cross(axis, value);
        double projection = Dot(axis, value);
        return Normalize(
            new Vector(
                value.X * cosine +
                cross.X * sine +
                axis.X * projection * (1 - cosine),
                value.Y * cosine +
                cross.Y * sine +
                axis.Y * projection * (1 - cosine),
                value.Z * cosine +
                cross.Z * sine +
                axis.Z * projection * (1 - cosine)),
            "повернутая плоскость разделки");
    }

    internal static IReadOnlyList<FachwerkBevelCutterProfilePoint>
        BuildCutterProfile(
            double thickness,
            double angleDeg,
            double rootFace)
    {
        if (!(thickness > 0))
            throw new ArgumentOutOfRangeException(nameof(thickness));
        if (!(angleDeg > 0 && angleDeg < 90))
            throw new ArgumentOutOfRangeException(nameof(angleDeg));
        if (!(rootFace >= 0 && rootFace < thickness))
            throw new ArgumentOutOfRangeException(nameof(rootFace));

        double halfThickness = thickness * 0.5;
        double rootCoordinate = -halfThickness + rootFace;
        double outerCoordinate = halfThickness;
        double removedThickness = thickness - rootFace;
        // The fabrication angle is measured from the original square end
        // face, so the longitudinal run is thickness * tan(angle).
        double run =
            removedThickness *
            Math.Tan(angleDeg * Math.PI / 180.0);
        double edgeLength = Math.Sqrt(
            run * run +
            removedThickness * removedThickness);
        double normalAlong = -removedThickness / edgeLength;
        double normalOutward = run / edgeLength;

        var rootEnd = new FachwerkBevelCutterProfilePoint(
            -run,
            rootCoordinate - removedThickness);
        var outerEnd = new FachwerkBevelCutterProfilePoint(
            run * (BevelEdgeLengthFactor - 1),
            outerCoordinate +
            removedThickness * (BevelEdgeLengthFactor - 2));
        return new[]
        {
            rootEnd,
            new FachwerkBevelCutterProfilePoint(
                rootEnd.Along + normalAlong * CutterPlaneOverrun,
                rootEnd.Outward + normalOutward * CutterPlaneOverrun),
            new FachwerkBevelCutterProfilePoint(
                outerEnd.Along + normalAlong * CutterPlaneOverrun,
                outerEnd.Outward + normalOutward * CutterPlaneOverrun),
            outerEnd,
        };
    }

    private static Point At(
        Point anchor,
        Vector along,
        double alongDistance,
        Vector outward,
        double outwardDistance)
    {
        return new Point(
            anchor.X +
            along.X * alongDistance +
            outward.X * outwardDistance,
            anchor.Y +
            along.Y * alongDistance +
            outward.Y * outwardDistance,
            anchor.Z +
            along.Z * alongDistance +
            outward.Z * outwardDistance);
    }

    private static Point Add(Point point, Vector direction, double distance)
    {
        return new Point(
            point.X + direction.X * distance,
            point.Y + direction.Y * distance,
            point.Z + direction.Z * distance);
    }

    private static Vector Subtract(Point first, Point second)
    {
        return new Vector(
            first.X - second.X,
            first.Y - second.Y,
            first.Z - second.Z);
    }

    private static Vector Scale(Vector value, double factor)
    {
        return new Vector(
            value.X * factor,
            value.Y * factor,
            value.Z * factor);
    }

    private static Vector Cross(Vector first, Vector second)
    {
        return new Vector(
            first.Y * second.Z - first.Z * second.Y,
            first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);
    }

    private static double Dot(Vector first, Vector second)
    {
        return
            first.X * second.X +
            first.Y * second.Y +
            first.Z * second.Z;
    }

    private static Vector Normalize(Vector value, string label)
    {
        double length = Math.Sqrt(Dot(value, value));
        if (length <= 1e-9)
            throw new InvalidOperationException("Нулевой вектор: " + label + ".");
        return Scale(value, 1.0 / length);
    }

    private static string BuildCutterName(int componentId, string role)
    {
        return
            "FKRC-BEV-" +
            componentId.ToString(CultureInfo.InvariantCulture) +
            "-" +
            (role ?? string.Empty)
                .Replace("lower-", string.Empty)
                .Replace("outer-", "O-")
                .Replace("inner-", "I-")
                .Replace("left-", "L-")
                .Replace("right-", "R-")
                .Replace("flange", "F")
                .Replace("web", "W");
    }

    private static void TrySetUserProperty(
        ModelObject modelObject,
        string name,
        string value)
    {
        try
        {
            modelObject.SetUserProperty(name, value ?? string.Empty);
        }
        catch
        {
            // Optional diagnostics must not block geometry on environments
            // where custom UDAs are unavailable.
        }
    }
}

internal sealed class FachwerkBevelCutterProfilePoint
{
    internal FachwerkBevelCutterProfilePoint(
        double along,
        double outward)
    {
        Along = along;
        Outward = outward;
    }

    public double Along { get; }
    public double Outward { get; }
}
