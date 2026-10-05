#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal enum FachwerkRigelInsertBevelKind
{
    InsertFlange,
    PartFlange,
    InsertWeb,
    PartWeb,
    DirectWeb,
    LongitudinalWeb,
}

internal sealed class FachwerkRigelInsertPlateSpec
{
    internal FachwerkRigelInsertPlateSpec(
        string outputRole,
        string semanticRole,
        string name,
        string sectionRole,
        Part template,
        IReadOnlyList<Point> boundary,
        Point contactMinus,
        Point contactPlus,
        Point endMinus,
        Point endPlus,
        Vector tangent,
        Vector outward,
        Vector crossAxis,
        double thickness,
        double width)
    {
        OutputRole = outputRole;
        SemanticRole = semanticRole;
        Name = name;
        SectionRole = sectionRole;
        Template = template;
        Boundary = boundary;
        ContactMinus = contactMinus;
        ContactPlus = contactPlus;
        EndMinus = endMinus;
        EndPlus = endPlus;
        Tangent = tangent;
        Outward = outward;
        CrossAxis = crossAxis;
        Thickness = thickness;
        Width = width;
    }

    public string OutputRole { get; }
    public string SemanticRole { get; }
    public string Name { get; }
    public string SectionRole { get; }
    public Part Template { get; }
    public IReadOnlyList<Point> Boundary { get; }
    public Point ContactMinus { get; }
    public Point ContactPlus { get; }
    public Point EndMinus { get; }
    public Point EndPlus { get; }
    public Vector Tangent { get; }
    public Vector Outward { get; }
    public Vector CrossAxis { get; }
    public double Thickness { get; }
    public double Width { get; }
}

internal sealed class FachwerkRigelInsertPartFrame
{
    internal FachwerkRigelInsertPartFrame(
        FachwerkConnectionPart source,
        Point axisAtRigel,
        Vector tangent,
        Vector outward,
        Vector crossAxis,
        double thickness,
        double width)
    {
        Source = source;
        AxisAtRigel = axisAtRigel;
        Tangent = tangent;
        Outward = outward;
        CrossAxis = crossAxis;
        Thickness = thickness;
        Width = width;
    }

    public FachwerkConnectionPart Source { get; }
    public Point AxisAtRigel { get; }
    public Vector Tangent { get; }
    public Vector Outward { get; }
    public Vector CrossAxis { get; }
    public double Thickness { get; }
    public double Width { get; }
}

internal sealed class FachwerkRigelInsertSectionLayout
{
    internal FachwerkRigelInsertSectionLayout(
        string sectionRole,
        string selectedWebRole,
        string directWebRole,
        FachwerkRigelFacePlane rigelPlane,
        FachwerkRigelFacePlane flangeEndPlane,
        FachwerkRigelFacePlane flangeFitPlane,
        FachwerkRigelFacePlane webEndPlane,
        FachwerkRigelFacePlane webFitPlane,
        IReadOnlyDictionary<string, FachwerkRigelInsertPartFrame> frames,
        IReadOnlyList<FachwerkRigelInsertPlateSpec> inserts)
    {
        SectionRole = sectionRole;
        SelectedWebRole = selectedWebRole;
        DirectWebRole = directWebRole;
        RigelPlane = rigelPlane;
        FlangeEndPlane = flangeEndPlane;
        FlangeFitPlane = flangeFitPlane;
        WebEndPlane = webEndPlane;
        WebFitPlane = webFitPlane;
        Frames = frames;
        Inserts = inserts;
    }

    public string SectionRole { get; }
    public string SelectedWebRole { get; }
    public string DirectWebRole { get; }
    public FachwerkRigelFacePlane RigelPlane { get; }
    public FachwerkRigelFacePlane FlangeEndPlane { get; }
    public FachwerkRigelFacePlane FlangeFitPlane { get; }
    public FachwerkRigelFacePlane WebEndPlane { get; }
    public FachwerkRigelFacePlane WebFitPlane { get; }
    public IReadOnlyDictionary<string, FachwerkRigelInsertPartFrame> Frames { get; }
    public IReadOnlyList<FachwerkRigelInsertPlateSpec> Inserts { get; }
}

internal sealed class FachwerkRigelInsertConnectionLayout
{
    internal FachwerkRigelInsertConnectionLayout(
        FachwerkRigelInsertSectionLayout upper,
        FachwerkRigelInsertSectionLayout lower)
    {
        Upper = upper;
        Lower = lower;
    }

    public FachwerkRigelInsertSectionLayout Upper { get; }
    public FachwerkRigelInsertSectionLayout Lower { get; }
}

internal sealed class FachwerkRigelInsertBevelSpec
{
    internal FachwerkRigelInsertBevelSpec(
        FachwerkRigelInsertBevelKind kind,
        FachwerkBevelSection section)
    {
        Kind = kind;
        Section = section;
    }

    public FachwerkRigelInsertBevelKind Kind { get; }
    public FachwerkBevelSection Section { get; }
}

internal static class FachwerkColumnRigelInsertConnectionGeometry
{
    internal const string LeftWebRole = "outer-web";
    internal const string RightWebRole = "inner-web";
    private const string OuterFlangeRole = "outer-flange";
    private const string InnerFlangeRole = "inner-flange";
    private const double AxisTolerance = 0.75;
    private const double ParallelCosine = 0.9995;
    private const double LongitudinalBevelRootFace = 13.0;
    private const double Epsilon = 1e-7;

    internal static FachwerkRigelInsertConnectionLayout Build(
        FachwerkRigelFacePair rigelFaces,
        IReadOnlyList<FachwerkConnectionPart> upperParts,
        IReadOnlyList<FachwerkConnectionPart> lowerParts,
        string selectedWebRole,
        string upperControlFlangeRole,
        string lowerControlFlangeRole,
        double minimumFlangeHeight,
        double webExtraHeight,
        double overlap)
    {
        if (rigelFaces == null) throw new ArgumentNullException(nameof(rigelFaces));
        string selected = NormalizeSelectedWebRole(selectedWebRole);
        string upperControl = NormalizeControlFlangeRole(upperControlFlangeRole);
        string lowerControl = NormalizeControlFlangeRole(lowerControlFlangeRole);
        double requiredLength = minimumFlangeHeight + webExtraHeight + overlap;

        ValidateRoles(upperParts, "верхней");
        ValidateRoles(lowerParts, "нижней");
        FachwerkColumnRigelConnectionGeometry.ValidateSectionSide(
            upperParts,
            rigelFaces.Top,
            "верхней");
        FachwerkColumnRigelConnectionGeometry.ValidateSectionSide(
            lowerParts,
            rigelFaces.Bottom,
            "нижней");

        return new FachwerkRigelInsertConnectionLayout(
            BuildSection(
                "upper",
                upperParts,
                rigelFaces.Top,
                selected,
                upperControl,
                minimumFlangeHeight,
                webExtraHeight,
                overlap,
                requiredLength),
            BuildSection(
                "lower",
                lowerParts,
                rigelFaces.Bottom,
                selected,
                lowerControl,
                minimumFlangeHeight,
                webExtraHeight,
                overlap,
                requiredLength));
    }

    internal static IReadOnlyList<FachwerkRigelInsertBevelSpec> BuildBevels(
        FachwerkRigelInsertSectionLayout layout,
        IReadOnlyDictionary<string, Part> insertedParts)
    {
        if (layout == null) throw new ArgumentNullException(nameof(layout));
        if (insertedParts == null) throw new ArgumentNullException(nameof(insertedParts));

        var result = new List<FachwerkRigelInsertBevelSpec>();
        foreach (FachwerkRigelInsertPlateSpec insert in layout.Inserts)
        {
            if (!insertedParts.TryGetValue(insert.OutputRole, out Part part))
            {
                throw new InvalidOperationException(
                    "Не найдена созданная вставка '" + insert.OutputRole + "'.");
            }

            FachwerkRigelInsertBevelKind kind = IsFlange(insert.SemanticRole)
                ? FachwerkRigelInsertBevelKind.InsertFlange
                : FachwerkRigelInsertBevelKind.InsertWeb;
            result.Add(new FachwerkRigelInsertBevelSpec(
                kind,
                BuildInsertEndBevel(insert, part, layout.RigelPlane)));
            if (IsWeb(insert.SemanticRole))
            {
                result.AddRange(BuildLongitudinalInsertBevels(insert, part));
            }
        }

        foreach (string flangeRole in new[] { OuterFlangeRole, InnerFlangeRole })
        {
            FachwerkRigelInsertPartFrame frame = layout.Frames[flangeRole];
            result.Add(new FachwerkRigelInsertBevelSpec(
                FachwerkRigelInsertBevelKind.PartFlange,
                BuildPartEndBevel(
                    layout.SectionRole + "-" + flangeRole + "-part",
                    frame,
                    layout.FlangeFitPlane,
                    1.0)));
        }

        FachwerkRigelInsertPartFrame selectedWeb = layout.Frames[layout.SelectedWebRole];
        result.Add(new FachwerkRigelInsertBevelSpec(
            FachwerkRigelInsertBevelKind.PartWeb,
            BuildPartEndBevel(
                layout.SectionRole + "-" + layout.SelectedWebRole + "-part",
                selectedWeb,
                layout.WebFitPlane,
                1.0)));

        FachwerkRigelInsertPartFrame directWeb = layout.Frames[layout.DirectWebRole];
        result.Add(new FachwerkRigelInsertBevelSpec(
            FachwerkRigelInsertBevelKind.DirectWeb,
                BuildPartEndBevel(
                    layout.SectionRole + "-" + layout.DirectWebRole + "-direct",
                    directWeb,
                    layout.RigelPlane,
                    -1.0,
                    layout.RigelPlane)));
        result.AddRange(BuildDirectWebLongitudinalBevels(layout, directWeb));
        return result;
    }

    internal static string NormalizeSelectedWebRole(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "LEFT"
            : value.Trim().ToUpperInvariant();
        if (normalized == "LEFT" || normalized == LeftWebRole.ToUpperInvariant())
            return LeftWebRole;
        if (normalized == "RIGHT" || normalized == RightWebRole.ToUpperInvariant())
            return RightWebRole;
        throw new InvalidOperationException(
            "Сторона вставки стенки должна быть LEFT или RIGHT.");
    }

    internal static string NormalizeControlFlangeRole(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? "INNER"
            : value.Trim().ToUpperInvariant();
        if (normalized == "INNER" ||
            normalized == InnerFlangeRole.ToUpperInvariant())
        {
            return InnerFlangeRole;
        }
        if (normalized == "OUTER" ||
            normalized == OuterFlangeRole.ToUpperInvariant())
        {
            return OuterFlangeRole;
        }
        throw new InvalidOperationException(
            "Определяющий пояс должен быть INNER или OUTER.");
    }

    private static FachwerkRigelInsertSectionLayout BuildSection(
        string sectionRole,
        IReadOnlyList<FachwerkConnectionPart> parts,
        FachwerkRigelFacePlane rigelPlane,
        string selectedWebRole,
        string controlFlangeRole,
        double minimumFlangeHeight,
        double webExtraHeight,
        double overlap,
        double requiredLength)
    {
        IReadOnlyDictionary<string, FachwerkRigelInsertPartFrame> frames =
            BuildFrames(parts, rigelPlane, requiredLength, sectionRole);
        FachwerkRigelInsertPartFrame inner = frames[InnerFlangeRole];
        FachwerkRigelInsertPartFrame outer = frames[OuterFlangeRole];
        FachwerkRigelInsertPartFrame control = frames[controlFlangeRole];
        FachwerkRigelInsertPartFrame selectedWeb = frames[selectedWebRole];

        ValidateParallel(inner, outer, sectionRole + " наружный пояс");
        ValidateParallel(inner, selectedWeb, sectionRole + " выбранная стенка");
        string directWebRole = selectedWebRole == LeftWebRole
            ? RightWebRole
            : LeftWebRole;
        ValidateParallel(
            inner,
            frames[directWebRole],
            sectionRole + " стенка без вставки");

        Point controlMinus = ContactPoint(control, rigelPlane, -1);
        Point controlPlus = ContactPoint(control, rigelPlane, 1);
        double minusParameter = Along(
            controlMinus,
            control.AxisAtRigel,
            control.Tangent);
        double plusParameter = Along(
            controlPlus,
            control.AxisAtRigel,
            control.Tangent);
        double farParameter = Math.Max(minusParameter, plusParameter) +
            minimumFlangeHeight;
        Point flangeEndOrigin = Add(
            control.AxisAtRigel,
            control.Tangent,
            farParameter);
        FachwerkRigelFacePlane flangeEndPlane = BuildPlane(
            flangeEndOrigin,
            control.Tangent,
            control.CrossAxis);
        FachwerkRigelFacePlane flangeFitPlane = flangeEndPlane.Offset(-overlap);
        FachwerkRigelFacePlane webEndPlane = flangeEndPlane.Offset(webExtraHeight);
        FachwerkRigelFacePlane webFitPlane = webEndPlane.Offset(-overlap);

        var inserts = new List<FachwerkRigelInsertPlateSpec>
        {
            BuildInsert(
                sectionRole,
                frames[OuterFlangeRole],
                rigelPlane,
                flangeEndPlane),
            BuildInsert(
                sectionRole,
                frames[InnerFlangeRole],
                rigelPlane,
                flangeEndPlane),
            BuildInsert(
                sectionRole,
                selectedWeb,
                rigelPlane,
                webEndPlane),
        };
        return new FachwerkRigelInsertSectionLayout(
            sectionRole,
            selectedWebRole,
            directWebRole,
            rigelPlane,
            flangeEndPlane,
            flangeFitPlane,
            webEndPlane,
            webFitPlane,
            frames,
            inserts);
    }

    private static IReadOnlyDictionary<string, FachwerkRigelInsertPartFrame>
        BuildFrames(
            IReadOnlyList<FachwerkConnectionPart> parts,
            FachwerkRigelFacePlane rigelPlane,
            double requiredLength,
            string sectionRole)
    {
        var seeds = new List<FachwerkRigelInsertAxisFrame>(parts.Count);
        foreach (FachwerkConnectionPart part in parts)
        {
            IReadOnlyList<Point> axis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(part.Part);
            Point axisAtRigel;
            Vector tangent = ResolveAxisAtPlane(
                axis,
                rigelPlane,
                FachwerkColumnRigelConnectionGeometry.ReadPartCenter(part.Part),
                requiredLength,
                sectionRole + " " + part.SemanticRole,
                out axisAtRigel);
            ParsePlateDimensions(
                part.Part.Profile.ProfileString,
                out double thickness,
                out double width);
            seeds.Add(new FachwerkRigelInsertAxisFrame(
                part,
                axisAtRigel,
                tangent,
                thickness,
                width));
        }

        var sectionCenter = new Point();
        foreach (FachwerkRigelInsertAxisFrame seed in seeds)
        {
            sectionCenter.X += seed.AxisAtRigel.X;
            sectionCenter.Y += seed.AxisAtRigel.Y;
            sectionCenter.Z += seed.AxisAtRigel.Z;
        }
        sectionCenter.X /= seeds.Count;
        sectionCenter.Y /= seeds.Count;
        sectionCenter.Z /= seeds.Count;

        var result = new Dictionary<string, FachwerkRigelInsertPartFrame>(
            StringComparer.OrdinalIgnoreCase);
        foreach (FachwerkRigelInsertAxisFrame seed in seeds)
        {
            Vector fromCenter = Subtract(seed.AxisAtRigel, sectionCenter);
            Vector outward = Normalize(Subtract(
                fromCenter,
                Scale(seed.Tangent, Dot(fromCenter, seed.Tangent))));
            Vector crossAxis = Normalize(Cross(seed.Tangent, outward));
            result.Add(
                seed.Source.SemanticRole,
                new FachwerkRigelInsertPartFrame(
                    seed.Source,
                    seed.AxisAtRigel,
                    seed.Tangent,
                    outward,
                    crossAxis,
                    seed.Thickness,
                    seed.Width));
        }
        return result;
    }

    internal static void ParsePlateDimensions(
        string profile,
        out double thickness,
        out double width)
    {
        string normalized = (profile ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", string.Empty)
            .Replace(',', '.');
        if (!normalized.StartsWith("PL", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Для узла требуется листовой профиль PL, получен '" +
                profile + "'.");
        }

        string[] values = normalized.Substring(2).Split(
            new[] { '*', 'X' },
            StringSplitOptions.RemoveEmptyEntries);
        if (values.Length < 2 ||
            !double.TryParse(
                values[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out thickness) ||
            !double.TryParse(
                values[values.Length - 1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out width) ||
            thickness <= 0 ||
            width <= 0)
        {
            throw new InvalidOperationException(
                "Не удалось определить толщину и ширину листа из профиля '" +
                profile + "'.");
        }
    }

    private static Vector ResolveAxisAtPlane(
        IReadOnlyList<Point> axis,
        FachwerkRigelFacePlane plane,
        Point partCenter,
        double requiredLength,
        string label,
        out Point intersection)
    {
        if (axis == null || axis.Count < 2)
            throw new InvalidOperationException("Ось детали '" + label + "' пуста.");

        int bestIndex = -1;
        double bestScore = double.PositiveInfinity;
        Point bestIntersection = null;
        Vector bestDirection = null;
        for (var index = 0; index < axis.Count - 1; index++)
        {
            Vector direction = Normalize(Subtract(axis[index + 1], axis[index]));
            double denominator = Dot(direction, plane.Normal);
            if (Math.Abs(denominator) < 0.02)
                continue;
            double parameter = Dot(Subtract(plane.Origin, axis[index]), plane.Normal) /
                denominator;
            double segmentLength = Distance(axis[index], axis[index + 1]);
            double outside = parameter < 0
                ? -parameter
                : parameter > segmentLength
                    ? parameter - segmentLength
                    : 0;
            double score = outside + 0.001 * Math.Abs(parameter);
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = index;
                bestDirection = direction;
                bestIntersection = Add(axis[index], direction, parameter);
            }
        }
        if (bestIndex < 0 || bestScore > requiredLength + 50)
        {
            throw new InvalidOperationException(
                "Ось детали '" + label +
                "' не пересекает плоскость ригеля на допустимом продолжении.");
        }

        if (Dot(Subtract(partCenter, bestIntersection), bestDirection) < 0)
            bestDirection = Scale(bestDirection, -1);
        ValidateStraightRegion(axis, bestIntersection, bestDirection, requiredLength, label);
        intersection = bestIntersection;
        return bestDirection;
    }

    private static void ValidateStraightRegion(
        IReadOnlyList<Point> axis,
        Point origin,
        Vector tangent,
        double requiredLength,
        string label)
    {
        double maximumAlong = double.NegativeInfinity;
        foreach (Point point in axis)
        {
            Vector delta = Subtract(point, origin);
            double along = Dot(delta, tangent);
            maximumAlong = Math.Max(maximumAlong, along);
            if (along < -5 || along > requiredLength + 5)
                continue;
            Vector perpendicular = Subtract(delta, Scale(tangent, along));
            if (Length(perpendicular) > AxisTolerance)
            {
                throw new InvalidOperationException(
                    "Узел детали '" + label +
                    "' попадает на радиус или переход сечения. Требуется прямой участок.");
            }
        }
        if (maximumAlong + 5 < requiredLength)
        {
            throw new InvalidOperationException(
                "Прямого участка детали '" + label +
                "' недостаточно для вставки длиной " +
                requiredLength.ToString("0.###") + " мм.");
        }
    }

    private static FachwerkRigelInsertPlateSpec BuildInsert(
        string sectionRole,
        FachwerkRigelInsertPartFrame frame,
        FachwerkRigelFacePlane contactPlane,
        FachwerkRigelFacePlane endPlane)
    {
        Point contactMinus = ContactPoint(frame, contactPlane, -1);
        Point contactPlus = ContactPoint(frame, contactPlane, 1);
        Point endMinus = InnerFaceRailPlaneIntersection(frame, endPlane, -1);
        Point endPlus = InnerFaceRailPlaneIntersection(frame, endPlane, 1);
        string outputRole = sectionRole + "-" + frame.Source.SemanticRole + "-insert";
        string name = InsertName(frame.Source.SemanticRole);
        return new FachwerkRigelInsertPlateSpec(
            outputRole,
            frame.Source.SemanticRole,
            name,
            sectionRole,
            frame.Source.Part,
            new[]
            {
                contactMinus,
                endMinus,
                endPlus,
                contactPlus,
            },
            contactMinus,
            contactPlus,
            endMinus,
            endPlus,
            frame.Tangent,
            frame.Outward,
            frame.CrossAxis,
            frame.Thickness,
            frame.Width);
    }

    private static FachwerkBevelSection BuildInsertEndBevel(
        FachwerkRigelInsertPlateSpec insert,
        Part part,
        FachwerkRigelFacePlane rigelPlane)
    {
        return BuildTransverseBevel(
            insert.OutputRole + "-rigel-bevel",
            insert.SemanticRole,
            part,
            insert.ContactMinus,
            insert.ContactPlus,
            Midpoint(insert.EndMinus, insert.EndPlus),
            insert.Outward,
            insert.Thickness,
            rigelPlane.Normal);
    }

    private static FachwerkBevelSection BuildPartEndBevel(
        string role,
        FachwerkRigelInsertPartFrame frame,
        FachwerkRigelFacePlane plane,
        double outwardSign,
        FachwerkRigelFacePlane angleReferencePlane = null)
    {
        Vector bevelOutward = Scale(frame.Outward, outwardSign);
        Point edgeMinus = BevelRootRailPlaneIntersection(
            frame,
            plane,
            -1,
            bevelOutward);
        Point edgePlus = BevelRootRailPlaneIntersection(
            frame,
            plane,
            1,
            bevelOutward);
        Point anchor = Midpoint(edgeMinus, edgePlus);
        return BuildTransverseBevel(
            role + "-bevel",
            frame.Source.SemanticRole,
            frame.Source.Part,
            edgeMinus,
            edgePlus,
            Add(anchor, frame.Tangent, 100.0),
            bevelOutward,
            frame.Thickness,
            angleReferencePlane?.Normal);
    }

    internal static FachwerkBevelSection BuildTransverseBevel(
        string role,
        string semanticRole,
        Part part,
        Point edgeStart,
        Point edgeEnd,
        Point interiorPoint,
        Vector outward,
        double thickness,
        Vector angleReferenceNormal = null)
    {
        Vector edgeVector = Subtract(edgeEnd, edgeStart);
        double edgeLength = Length(edgeVector);
        if (edgeLength <= 1.0)
        {
            throw new InvalidOperationException(
                "Контактная кромка разделки '" + role + "' имеет нулевую длину.");
        }

        Vector edgeDirection = Scale(edgeVector, 1.0 / edgeLength);
        Vector intoPart = Normalize(Cross(edgeDirection, outward));
        Point edgeAnchor = Midpoint(edgeStart, edgeEnd);
        if (Dot(intoPart, Subtract(interiorPoint, edgeAnchor)) < 0)
            intoPart = Scale(intoPart, -1);
        Vector unitOutward = Normalize(outward);
        Point cutterCenterAnchor = Add(
            edgeAnchor,
            unitOutward,
            thickness * 0.5);

        return new FachwerkBevelSection(
            role,
            semanticRole,
            part,
            cutterCenterAnchor,
            intoPart,
            unitOutward,
            edgeDirection,
            thickness,
            edgeLength,
            100.0,
            angleReferenceNormal);
    }

    private static IEnumerable<FachwerkRigelInsertBevelSpec>
        BuildLongitudinalInsertBevels(
            FachwerkRigelInsertPlateSpec insert,
            Part part)
    {
        return new[]
        {
            BuildLongitudinalBevel(
                insert.OutputRole + "-edge-minus",
                insert.SemanticRole,
                part,
                insert.ContactMinus,
                insert.EndMinus,
                insert.CrossAxis,
                insert.Outward,
                insert.Thickness),
            BuildLongitudinalBevel(
                insert.OutputRole + "-edge-plus",
                insert.SemanticRole,
                part,
                insert.ContactPlus,
                insert.EndPlus,
                Scale(insert.CrossAxis, -1),
                insert.Outward,
                insert.Thickness),
        };
    }

    private static IEnumerable<FachwerkRigelInsertBevelSpec>
        BuildDirectWebLongitudinalBevels(
            FachwerkRigelInsertSectionLayout layout,
            FachwerkRigelInsertPartFrame frame)
    {
        Vector inwardFace = Scale(frame.Outward, -1);
        Point startMinus = BevelRootRailPlaneIntersection(
            frame,
            layout.RigelPlane,
            -1,
            inwardFace);
        Point startPlus = BevelRootRailPlaneIntersection(
            frame,
            layout.RigelPlane,
            1,
            inwardFace);
        Point endMinus = BevelRootRailPlaneIntersection(
            frame,
            layout.FlangeFitPlane,
            -1,
            inwardFace);
        Point endPlus = BevelRootRailPlaneIntersection(
            frame,
            layout.FlangeFitPlane,
            1,
            inwardFace);
        return new[]
        {
            BuildLongitudinalBevel(
                layout.SectionRole + "-direct-web-edge-minus",
                frame.Source.SemanticRole,
                frame.Source.Part,
                startMinus,
                endMinus,
                frame.CrossAxis,
                inwardFace,
                frame.Thickness),
            BuildLongitudinalBevel(
                layout.SectionRole + "-direct-web-edge-plus",
                frame.Source.SemanticRole,
                frame.Source.Part,
                startPlus,
                endPlus,
                Scale(frame.CrossAxis, -1),
                inwardFace,
                frame.Thickness),
        };
    }

    internal static FachwerkRigelInsertBevelSpec BuildLongitudinalBevel(
        string role,
        string semanticRole,
        Part part,
        Point start,
        Point end,
        Vector inwardFromEdge,
        Vector outward,
        double thickness)
    {
        Vector edge = Subtract(end, start);
        double length = Length(edge);
        if (length <= 1)
        {
            throw new InvalidOperationException(
                "Продольная разделка '" + role + "' имеет нулевую длину.");
        }
        Vector edgeDirection = Scale(edge, 1.0 / length);
        Vector unitOutward = Normalize(outward);
        Point cutterCenterAnchor = Add(
            Midpoint(start, end),
            unitOutward,
            thickness * 0.5);
        return new FachwerkRigelInsertBevelSpec(
            FachwerkRigelInsertBevelKind.LongitudinalWeb,
            new FachwerkBevelSection(
                role,
                semanticRole,
                part,
                cutterCenterAnchor,
                Normalize(inwardFromEdge),
                unitOutward,
                edgeDirection,
                thickness,
                length,
                0.0));
    }

    internal static double LongitudinalRootFace(double thickness)
    {
        if (thickness <= LongitudinalBevelRootFace)
        {
            throw new InvalidOperationException(
                "Толщина стенки должна быть больше 13 мм для продольной разделки.");
        }
        return LongitudinalBevelRootFace;
    }

    private static Point ContactPoint(
        FachwerkRigelInsertPartFrame frame,
        FachwerkRigelFacePlane plane,
        double side)
    {
        return InnerFaceRailPlaneIntersection(frame, plane, side);
    }

    internal static Point InnerFaceRailPlaneIntersection(
        FachwerkRigelInsertPartFrame frame,
        FachwerkRigelFacePlane plane,
        double side)
    {
        return FaceRailPlaneIntersection(
            frame,
            plane,
            side,
            frame.Outward,
            -0.5 * frame.Thickness);
    }

    private static Point BevelRootRailPlaneIntersection(
        FachwerkRigelInsertPartFrame frame,
        FachwerkRigelFacePlane plane,
        double side,
        Vector bevelOutward)
    {
        return FaceRailPlaneIntersection(
            frame,
            plane,
            side,
            bevelOutward,
            -0.5 * frame.Thickness);
    }

    private static Point FaceRailPlaneIntersection(
        FachwerkRigelInsertPartFrame frame,
        FachwerkRigelFacePlane plane,
        double side,
        Vector faceDirection,
        double faceOffset)
    {
        Point faceAxis = Add(
            frame.AxisAtRigel,
            faceDirection,
            faceOffset);
        Point railPoint = Add(
            faceAxis,
            frame.CrossAxis,
            side * frame.Width * 0.5);
        return IntersectLineWithPlane(railPoint, frame.Tangent, plane);
    }

    private static Point IntersectLineWithPlane(
        Point linePoint,
        Vector lineDirection,
        FachwerkRigelFacePlane plane)
    {
        double denominator = Dot(lineDirection, plane.Normal);
        if (Math.Abs(denominator) < 0.02)
        {
            throw new InvalidOperationException(
                "Ось детали почти параллельна расчетной плоскости узла.");
        }
        double parameter = Dot(Subtract(plane.Origin, linePoint), plane.Normal) /
            denominator;
        return Add(linePoint, lineDirection, parameter);
    }

    private static FachwerkRigelFacePlane BuildPlane(
        Point origin,
        Vector normal,
        Vector preferredAxis)
    {
        Vector unitNormal = Normalize(normal);
        Vector projected = Subtract(
            preferredAxis,
            Scale(unitNormal, Dot(preferredAxis, unitNormal)));
        Vector axisX = Normalize(projected);
        Vector axisY = Normalize(Cross(unitNormal, axisX));
        return new FachwerkRigelFacePlane(
            new Point(origin),
            axisX,
            axisY,
            unitNormal,
            Array.Empty<Point>());
    }

    private static void ValidateParallel(
        FachwerkRigelInsertPartFrame reference,
        FachwerkRigelInsertPartFrame candidate,
        string label)
    {
        if (Dot(reference.Tangent, candidate.Tangent) < ParallelCosine)
        {
            throw new InvalidOperationException(
                "Оси деталей в зоне узла не параллельны: " + label + ".");
        }
    }

    private static void ValidateRoles(
        IReadOnlyList<FachwerkConnectionPart> parts,
        string sectionLabel)
    {
        if (parts == null || parts.Count != 4)
        {
            throw new InvalidOperationException(
                "Для " + sectionLabel + " секции нужны ровно четыре детали.");
        }
        string[] required =
        {
            OuterFlangeRole,
            InnerFlangeRole,
            LeftWebRole,
            RightWebRole,
        };
        foreach (string role in required)
        {
            int count = parts.Count(part => string.Equals(
                part.SemanticRole,
                role,
                StringComparison.OrdinalIgnoreCase));
            if (count != 1)
            {
                throw new InvalidOperationException(
                    "В " + sectionLabel + " секции FK_ROLE '" + role +
                    "' должен встречаться ровно один раз, найдено: " + count + ".");
            }
        }
    }

    private static string InsertName(string semanticRole)
    {
        switch (semanticRole)
        {
            case OuterFlangeRole:
                return "Наружная вставка";
            case InnerFlangeRole:
                return "Внутренняя вставка";
            case LeftWebRole:
                return "Левая вставка стенки";
            case RightWebRole:
                return "Правая вставка стенки";
            default:
                throw new InvalidOperationException(
                    "Неизвестная роль вставки: " + semanticRole);
        }
    }

    private static bool IsFlange(string role) =>
        role == OuterFlangeRole || role == InnerFlangeRole;

    private static bool IsWeb(string role) =>
        role == LeftWebRole || role == RightWebRole;

    private static double Along(Point point, Point origin, Vector direction) =>
        Dot(Subtract(point, origin), direction);

    private static Point Midpoint(Point first, Point second) => new(
        0.5 * (first.X + second.X),
        0.5 * (first.Y + second.Y),
        0.5 * (first.Z + second.Z));

    private static Point Add(Point point, Vector vector, double factor) => new(
        point.X + vector.X * factor,
        point.Y + vector.Y * factor,
        point.Z + vector.Z * factor);

    private static Vector Subtract(Point first, Point second) => new(
        first.X - second.X,
        first.Y - second.Y,
        first.Z - second.Z);

    private static Vector Subtract(Vector first, Vector second) => new(
        first.X - second.X,
        first.Y - second.Y,
        first.Z - second.Z);

    private static Vector Scale(Vector vector, double factor) => new(
        vector.X * factor,
        vector.Y * factor,
        vector.Z * factor);

    private static Vector Cross(Vector first, Vector second) => new(
        first.Y * second.Z - first.Z * second.Y,
        first.Z * second.X - first.X * second.Z,
        first.X * second.Y - first.Y * second.X);

    private static double Dot(Vector first, Vector second) =>
        first.X * second.X + first.Y * second.Y + first.Z * second.Z;

    private static double Length(Vector vector) => Math.Sqrt(Dot(vector, vector));

    private static double Distance(Point first, Point second) =>
        Length(Subtract(second, first));

    private static Vector Normalize(Vector vector)
    {
        double length = Length(vector);
        if (length <= Epsilon)
            throw new InvalidOperationException("Получен нулевой вектор геометрии узла.");
        return Scale(vector, 1.0 / length);
    }

    private sealed class FachwerkRigelInsertAxisFrame
    {
        internal FachwerkRigelInsertAxisFrame(
            FachwerkConnectionPart source,
            Point axisAtRigel,
            Vector tangent,
            double thickness,
            double width)
        {
            Source = source;
            AxisAtRigel = axisAtRigel;
            Tangent = tangent;
            Thickness = thickness;
            Width = width;
        }

        public FachwerkConnectionPart Source { get; }
        public Point AxisAtRigel { get; }
        public Vector Tangent { get; }
        public double Thickness { get; }
        public double Width { get; }
    }
}
