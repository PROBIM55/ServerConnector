#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal static partial class FachwerkColumnGeometry
{
    private const int SectionTransitionStartBreakIndex = -1001;
    private const int SectionTransitionEndBreakIndex = -1000;
    private const double SectionTransitionTolerance = 0.25;

    internal const double FlangeThickness = 50.0;
    internal const double FlangeWidth = 180.0;
    internal const double WebThickness = 25.0;
    internal const double WebNormalOffset = (FlangeWidth - WebThickness) * 0.5;

    internal static IReadOnlyList<FachwerkColumnPartSpec> BuildSectionParts(FachwerkColumnProfileDefinition profile, FachwerkColumnFrame frame, IReadOnlyList<FachwerkColumnBreak> breaks)
    {
        profile.Validate();
        IReadOnlyList<FachwerkColumnBreak> effectiveBreaks = BuildEffectiveBreaks(profile, frame, breaks);
        FachwerkColumnPathDefinition fachwerkColumnPathDefinition = profile.RequirePath("inner-flange");
        FachwerkColumnPathDefinition fachwerkColumnPathDefinition2 = profile.RequirePath("outer-flange");
        FachwerkColumnPathDefinition path = OffsetToward(fachwerkColumnPathDefinition, fachwerkColumnPathDefinition2, 25.0, "inner-flange", "PL50*180", 0.0);
        FachwerkColumnPathDefinition path2 = OffsetToward(fachwerkColumnPathDefinition2, fachwerkColumnPathDefinition, 25.0, "outer-flange", "PL50*180", 0.0);
        FachwerkColumnPathDefinition webAxis = BuildWebAxis(
            fachwerkColumnPathDefinition,
            fachwerkColumnPathDefinition2,
            profile.SectionTransition);
        IReadOnlyList<FachwerkColumnBreak> transitionBreaks = effectiveBreaks
            .Where(item => item.IsSectionTransition())
            .ToArray();
        IReadOnlyList<FachwerkColumnJointPlane> joints = ResolveJointPlanes(
            path2,
            frame,
            effectiveBreaks,
            profile.SectionTransition);
        List<FachwerkColumnPartSpec> result = new List<FachwerkColumnPartSpec>();
        AddFlangeParts(result, path, profile.SectionTransition, frame, transitionBreaks, joints);
        AddFlangeParts(result, path2, profile.SectionTransition, frame, transitionBreaks, joints);
        AddWebParts(result, "inner-web", -WebNormalOffset, webAxis, profile, frame, transitionBreaks, joints);
        AddWebParts(result, "outer-web", WebNormalOffset, webAxis, profile, frame, transitionBreaks, joints);
        return result;
    }

    private static IReadOnlyList<FachwerkColumnBreak> BuildEffectiveBreaks(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks)
    {
        List<FachwerkColumnBreak> result = new List<FachwerkColumnBreak>();
        if (breaks != null)
        {
            foreach (FachwerkColumnBreak item in breaks)
            {
                double localY = item.Elevation - frame.GlobalInsertionZ;
                if (!IsStrictlyInsideSectionTransition(localY, profile.SectionTransition))
                {
                    result.Add(item);
                }
            }
        }
        result.Add(new FachwerkColumnBreak(
            frame.GlobalInsertionZ + profile.SectionTransition.StartY,
            FachwerkColumnBreak.SectionTransitionMode,
            SectionTransitionStartBreakIndex));
        result.Add(new FachwerkColumnBreak(
            frame.GlobalInsertionZ + profile.SectionTransition.EndY,
            FachwerkColumnBreak.SectionTransitionMode,
            SectionTransitionEndBreakIndex));
        return result;
    }

    private static bool IsStrictlyInsideSectionTransition(
        double localY,
        FachwerkColumnSectionTransitionDefinition transition)
    {
        return localY > transition.StartY + SectionTransitionTolerance &&
            localY < transition.EndY - SectionTransitionTolerance;
    }

    internal static ModelObject CreatePart(FachwerkColumnPartSpec spec, string fallbackMaterial, string fallbackClass, string mark, int componentId, int pieceIndex)
    {
        ModelObject modelObject = null;
        try
        {
            modelObject = ((spec.Kind == FachwerkColumnPartKind.Flange) ? CreateFlange(spec, fallbackMaterial, fallbackClass, mark, pieceIndex) : CreateWeb(spec, fallbackMaterial, fallbackClass, mark, pieceIndex));
            SetPartProperties(modelObject, spec, mark, componentId);
            if (!modelObject.Modify())
            {
                throw new InvalidOperationException("Tekla не сохранила атрибуты детали '" + BuildName(mark, spec.Role, pieceIndex) + "'.");
            }
            return modelObject;
        }
        catch
        {
            if (modelObject != null && modelObject.Identifier != null && modelObject.Identifier.ID != 0)
            {
                modelObject.Delete();
            }
            throw;
        }
    }

    internal static TransformationPlane CreateFacadePlane(FachwerkColumnFrame frame)
    {
        Vector axisX = new Vector(frame.AxisX.X, frame.AxisX.Y, 0.0);
        Vector axisY = new Vector(0.0, 0.0, 1.0);
        return new TransformationPlane(new CoordinateSystem(frame.Insertion, axisX, axisY));
    }

    private static void AddFlangeParts(
        ICollection<FachwerkColumnPartSpec> result,
        FachwerkColumnPathDefinition path,
        FachwerkColumnSectionTransitionDefinition transition,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> transitionBreaks,
        IReadOnlyList<FachwerkColumnJointPlane> joints)
    {
        foreach (FachwerkColumnPathPiece basePiece in BuildTransitionExtendedPieces(
            path,
            transition,
            frame,
            transitionBreaks))
        {
            foreach (FachwerkColumnPathPiece item in SplitByJointPlanes(
                path,
                basePiece.Primitives,
                joints,
                false,
                basePiece.EndBreakIndex))
            {
                result.Add(FachwerkColumnPartSpec.Flange(
                    path.NormalizedRole(),
                    item.Primitives,
                    path,
                    item.EndBreakIndex,
                    item.AssemblySegmentIndex));
            }
        }
    }

    private static void AddWebParts(
        ICollection<FachwerkColumnPartSpec> result,
        string role,
        double normalOffset,
        FachwerkColumnPathDefinition webAxis,
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> transitionBreaks,
        IReadOnlyList<FachwerkColumnJointPlane> joints)
    {
        FachwerkColumnPathDefinition physicalAxis = ClonePath(webAxis, role, "PL25", normalOffset);
        foreach (FachwerkColumnPathPiece basePiece in BuildTransitionExtendedPieces(
            physicalAxis,
            profile.SectionTransition,
            frame,
            transitionBreaks))
        {
            foreach (FachwerkColumnPathPiece piece in SplitByJointPlanes(
                physicalAxis,
                basePiece.Primitives,
                joints,
                true,
                basePiece.EndBreakIndex))
            {
                string webProfile = ResolveWebProfile(profile, piece.Primitives);
                result.Add(FachwerkColumnPartSpec.Web(
                    role,
                    piece.Primitives,
                    physicalAxis,
                    webProfile,
                    piece.EndBreakIndex,
                    normalOffset,
                    piece.AssemblySegmentIndex));
            }
        }
    }

    private static IReadOnlyList<FachwerkColumnPathPiece> BuildTransitionExtendedPieces(
        FachwerkColumnPathDefinition path,
        FachwerkColumnSectionTransitionDefinition transition,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks)
    {
        IReadOnlyList<FachwerkColumnPathPiece> sourcePieces = BuildPieces(path, frame, breaks);
        if (string.Equals(path.NormalizedRole(), "outer-flange", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePieces;
        }

        int firstOmittedIndex = -1;
        int lastOmittedIndex = -1;
        for (int index = 0; index < sourcePieces.Count; index++)
        {
            if (!IsOmittedSectionTransitionPiece(path.NormalizedRole(), sourcePieces[index].Primitives, transition))
            {
                continue;
            }
            if (firstOmittedIndex < 0) firstOmittedIndex = index;
            lastOmittedIndex = index;
        }

        if (firstOmittedIndex <= 0 || lastOmittedIndex >= sourcePieces.Count - 1)
        {
            throw new InvalidOperationException(
                "Переход высоты сечения траектории '" + path.NormalizedRole() +
                "' не образовал нижнюю, переходную и верхнюю части.");
        }

        double jointY = (transition.StartY + transition.EndY) * 0.5;
        var result = new List<FachwerkColumnPathPiece>(sourcePieces.Count - (lastOmittedIndex - firstOmittedIndex + 1));
        for (int index = 0; index < sourcePieces.Count; index++)
        {
            if (index >= firstOmittedIndex && index <= lastOmittedIndex)
            {
                continue;
            }

            FachwerkColumnPathPiece sourcePiece = sourcePieces[index];
            IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives = sourcePiece.Primitives;
            if (index == firstOmittedIndex - 1)
            {
                primitives = ExtendLowerTransitionPiece(primitives, jointY);
            }
            else if (index == lastOmittedIndex + 1)
            {
                primitives = ExtendUpperTransitionPiece(primitives, jointY);
            }
            result.Add(new FachwerkColumnPathPiece(
                path,
                primitives,
                sourcePiece.EndBreakIndex,
                sourcePiece.AssemblySegmentIndex));
        }
        return result;
    }

    private static IReadOnlyList<FachwerkColumnPrimitiveDefinition> ExtendLowerTransitionPiece(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source,
        double jointY)
    {
        List<FachwerkColumnPrimitiveDefinition> primitives =
            CloneWithoutTrailingTransitionLines(source);
        ExtendPrimitiveEndToY(primitives[primitives.Count - 1], jointY);
        return primitives;
    }

    private static IReadOnlyList<FachwerkColumnPrimitiveDefinition> ExtendUpperTransitionPiece(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source,
        double jointY)
    {
        List<FachwerkColumnPrimitiveDefinition> primitives =
            CloneWithoutLeadingTransitionLines(source);
        ExtendPrimitiveStartToY(primitives[0], jointY);
        return primitives;
    }

    private static List<FachwerkColumnPrimitiveDefinition> CloneWithoutTrailingTransitionLines(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source)
    {
        if (source == null || source.Count == 0)
        {
            throw new InvalidOperationException("Нижняя часть перехода сечения не содержит траекторию.");
        }

        int lastArcIndex = -1;
        for (int index = source.Count - 1; index >= 0; index--)
        {
            if (source[index].NormalizedKind() == "arc")
            {
                lastArcIndex = index;
                break;
            }
        }

        int lastIndex = source.Count - 1;
        if (lastArcIndex >= 0 && lastArcIndex < lastIndex)
        {
            for (int index = lastArcIndex + 1; index <= lastIndex; index++)
            {
                if (source[index].NormalizedKind() != "line")
                {
                    lastArcIndex = lastIndex;
                    break;
                }
            }
            lastIndex = lastArcIndex;
        }

        var result = new List<FachwerkColumnPrimitiveDefinition>(lastIndex + 1);
        for (int index = 0; index <= lastIndex; index++)
        {
            result.Add(ClonePrimitive(source[index]));
        }
        return result;
    }

    private static List<FachwerkColumnPrimitiveDefinition> CloneWithoutLeadingTransitionLines(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> source)
    {
        if (source == null || source.Count == 0)
        {
            throw new InvalidOperationException("Верхняя часть перехода сечения не содержит траекторию.");
        }

        int firstArcIndex = -1;
        for (int index = 0; index < source.Count; index++)
        {
            if (source[index].NormalizedKind() == "arc")
            {
                firstArcIndex = index;
                break;
            }
        }

        int firstIndex = 0;
        if (firstArcIndex > 0)
        {
            for (int index = 0; index < firstArcIndex; index++)
            {
                if (source[index].NormalizedKind() != "line")
                {
                    firstArcIndex = 0;
                    break;
                }
            }
            firstIndex = firstArcIndex;
        }

        var result = new List<FachwerkColumnPrimitiveDefinition>(source.Count - firstIndex);
        for (int index = firstIndex; index < source.Count; index++)
        {
            result.Add(ClonePrimitive(source[index]));
        }
        return result;
    }

    private static void ExtendPrimitiveEndToY(
        FachwerkColumnPrimitiveDefinition primitive,
        double targetY)
    {
        primitive.End = PointOnPrimitiveSupportAtY(primitive, primitive.End, targetY);
        RecalculateSweep(primitive);
    }

    private static void ExtendPrimitiveStartToY(
        FachwerkColumnPrimitiveDefinition primitive,
        double targetY)
    {
        primitive.Start = PointOnPrimitiveSupportAtY(primitive, primitive.Start, targetY);
        RecalculateSweep(primitive);
    }

    private static FachwerkColumnLocalPoint PointOnPrimitiveSupportAtY(
        FachwerkColumnPrimitiveDefinition primitive,
        FachwerkColumnLocalPoint reference,
        double targetY)
    {
        if (primitive.NormalizedKind() == "line")
        {
            double dy = primitive.End.Y - primitive.Start.Y;
            if (Math.Abs(dy) <= 1e-7)
            {
                throw new InvalidOperationException(
                    "Невозможно продлить горизонтальный сегмент перехода до другой отметки.");
            }
            double parameter = (targetY - primitive.Start.Y) / dy;
            return NewPoint(
                primitive.Start.X + (primitive.End.X - primitive.Start.X) * parameter,
                targetY);
        }

        double radius = Distance(primitive.Start, primitive.Center);
        double localY = targetY - primitive.Center.Y;
        double squaredX = radius * radius - localY * localY;
        if (squaredX < -SectionTransitionTolerance)
        {
            throw new InvalidOperationException(
                "Дуга перехода не достигает общей плоскости стыка.");
        }

        double deltaX = Math.Sqrt(Math.Max(0.0, squaredX));
        FachwerkColumnLocalPoint first = NewPoint(primitive.Center.X + deltaX, targetY);
        FachwerkColumnLocalPoint second = NewPoint(primitive.Center.X - deltaX, targetY);
        return Distance(first, reference) <= Distance(second, reference) ? first : second;
    }

    private static bool IsOmittedSectionTransitionPiece(
        string role,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        FachwerkColumnSectionTransitionDefinition transition)
    {
        if (string.Equals(role, "outer-flange", StringComparison.OrdinalIgnoreCase) ||
            primitives == null ||
            primitives.Count == 0)
        {
            return false;
        }

        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        foreach (FachwerkColumnPrimitiveDefinition primitive in primitives)
        {
            minY = Math.Min(minY, Math.Min(primitive.Start.Y, primitive.End.Y));
            maxY = Math.Max(maxY, Math.Max(primitive.Start.Y, primitive.End.Y));
        }

        double middleY = (minY + maxY) * 0.5;
        return maxY - minY > SectionTransitionTolerance &&
            minY >= transition.StartY - SectionTransitionTolerance &&
            maxY <= transition.EndY + SectionTransitionTolerance &&
            IsStrictlyInsideSectionTransition(middleY, transition);
    }

    private static string ResolveWebProfile(
        FachwerkColumnProfileDefinition profile,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives)
    {
        if (primitives == null || primitives.Count == 0)
        {
            throw new InvalidOperationException("Невозможно определить профиль пустого участка стенки.");
        }
        double startY = primitives[0].Start.Y;
        double endY = primitives[primitives.Count - 1].End.Y;
        double middleY = (startY + endY) * 0.5;
        return middleY <= profile.SectionTransition.EndY + SectionTransitionTolerance
            ? profile.SectionTransition.LowerWebProfile
            : profile.SectionTransition.UpperWebProfile;
    }

    private static ModelObject CreateFlange(FachwerkColumnPartSpec spec, string fallbackMaterial, string fallbackClass, string mark, int pieceIndex)
    {
        IReadOnlyList<FachwerkColumnContourVertex> readOnlyList = BuildContourVertices(spec.FirstBoundary);
        if (readOnlyList.Count < 2)
        {
            throw new InvalidOperationException("Траектория пояса '" + spec.Role + "' содержит меньше двух вершин.");
        }
        if (IsStraightFlange(readOnlyList))
        {
            Beam beam = new Beam(new Point(readOnlyList[0].Point.X, readOnlyList[0].Point.Y, 0.0), new Point(readOnlyList[readOnlyList.Count - 1].Point.X, readOnlyList[readOnlyList.Count - 1].Point.Y, 0.0));
            beam.Name = BuildName(mark, spec.Role, pieceIndex);
            beam.Class = FirstNonEmpty(spec.ClassName, fallbackClass, "3");
            Beam beam2 = beam;
            SetFlangeProperties(beam2, spec, fallbackMaterial);
            if (!beam2.Insert())
            {
                throw new InvalidOperationException("Tekla не создала прямой пояс '" + beam2.Name + "'.");
            }
            return beam2;
        }
        PolyBeam polyBeam = new PolyBeam(PolyBeam.PolyBeamTypeEnum.BEAM);
        polyBeam.Name = BuildName(mark, spec.Role, pieceIndex);
        polyBeam.Class = FirstNonEmpty(spec.ClassName, fallbackClass, "3");
        PolyBeam polyBeam2 = polyBeam;
        SetFlangeProperties(polyBeam2, spec, fallbackMaterial);
        foreach (FachwerkColumnContourVertex item in readOnlyList)
        {
            Point p = new Point(item.Point.X, item.Point.Y, 0.0);
            if (!polyBeam2.AddContourPoint(new ContourPoint(p, CreateChamfer(item))))
            {
                throw new InvalidOperationException("Tekla отклонила вершину пояса '" + spec.Role + "'.");
            }
        }
        try
        {
            if (!polyBeam2.Insert())
            {
                throw new InvalidOperationException("Tekla не создала пояс '" + polyBeam2.Name + "'.");
            }
            return polyBeam2;
        }
        catch (Exception innerException)
        {
            throw new InvalidOperationException("Tekla не создала полилинейный пояс '" + polyBeam2.Name + "'; вершин: " + readOnlyList.Count.ToString(CultureInfo.InvariantCulture) + ".", innerException);
        }
    }

    private static bool IsStraightFlange(IReadOnlyList<FachwerkColumnContourVertex> vertices)
    {
        if (vertices.Count == 2)
        {
            return true;
        }
        FachwerkColumnLocalPoint point = vertices[0].Point;
        FachwerkColumnLocalPoint point2 = vertices[vertices.Count - 1].Point;
        double num = point2.X - point.X;
        double num2 = point2.Y - point.Y;
        double num3 = Math.Sqrt(num * num + num2 * num2);
        if (num3 < 0.0001)
        {
            return false;
        }
        for (int i = 1; i < vertices.Count - 1; i++)
        {
            FachwerkColumnLocalPoint point3 = vertices[i].Point;
            if (Math.Abs(Cross(point3.X - point.X, point3.Y - point.Y, num, num2)) / num3 > 0.0001)
            {
                return false;
            }
        }
        return true;
    }

    private static void SetFlangeProperties(Part part, FachwerkColumnPartSpec spec, string fallbackMaterial)
    {
        part.Profile.ProfileString = "PL50*180";
        part.Material.MaterialString = FirstNonEmpty(spec.Material, fallbackMaterial, "S355");
        part.Position.Plane = Position.PlaneEnum.MIDDLE;
        part.Position.Depth = Position.DepthEnum.MIDDLE;
        part.Position.Rotation = Position.RotationEnum.TOP;
    }

    private static Part CreateWeb(FachwerkColumnPartSpec spec, string fallbackMaterial, string fallbackClass, string mark, int pieceIndex)
    {
        IReadOnlyList<FachwerkColumnContourVertex> vertices = BuildContourVertices(spec.FirstBoundary);
        if (vertices.Count < 2)
        {
            throw new InvalidOperationException("Траектория стенки '" + spec.Role + "' содержит меньше двух вершин.");
        }

        double z = 0.0 - spec.NormalOffset;
        if (vertices.Count == 2)
        {
            Beam straightWeb = new Beam(Beam.BeamTypeEnum.BEAM);
            straightWeb.Name = BuildName(mark, spec.Role, pieceIndex);
            straightWeb.Class = FirstNonEmpty(spec.ClassName, fallbackClass, "3");
            straightWeb.Profile.ProfileString = spec.Profile;
            straightWeb.Material.MaterialString = FirstNonEmpty(spec.Material, fallbackMaterial, "S355");
            straightWeb.Position.Plane = Position.PlaneEnum.MIDDLE;
            straightWeb.Position.Depth = Position.DepthEnum.MIDDLE;
            straightWeb.Position.Rotation = Position.RotationEnum.FRONT;
            straightWeb.StartPoint = new Point(vertices[0].Point.X, vertices[0].Point.Y, z);
            straightWeb.EndPoint = new Point(vertices[1].Point.X, vertices[1].Point.Y, z);
            if (!straightWeb.Insert())
            {
                throw new InvalidOperationException("Tekla не создала прямой фрагмент стенки '" + straightWeb.Name + "'.");
            }
            return straightWeb;
        }

        PolyBeam web = new PolyBeam(PolyBeam.PolyBeamTypeEnum.BEAM);
        web.Name = BuildName(mark, spec.Role, pieceIndex);
        web.Class = FirstNonEmpty(spec.ClassName, fallbackClass, "3");
        web.Profile.ProfileString = spec.Profile;
        web.Material.MaterialString = FirstNonEmpty(spec.Material, fallbackMaterial, "S355");
        web.Position.Plane = Position.PlaneEnum.MIDDLE;
        web.Position.Depth = Position.DepthEnum.MIDDLE;
        web.Position.Rotation = Position.RotationEnum.FRONT;
        foreach (FachwerkColumnContourVertex vertex in vertices)
        {
            Point point = new Point(vertex.Point.X, vertex.Point.Y, z);
            if (!web.AddContourPoint(new ContourPoint(point, CreateChamfer(vertex))))
            {
                throw new InvalidOperationException("Tekla отклонила вершину стенки '" + spec.Role + "'.");
            }
        }
        try
        {
            if (!web.Insert())
            {
                throw new InvalidOperationException("Tekla не создала стенку '" + web.Name + "'.");
            }
            return web;
        }
        catch (Exception innerException)
        {
            throw new InvalidOperationException(
                "Tekla не создала полилинейную стенку '" + web.Name + "'; вершин: " +
                vertices.Count.ToString(CultureInfo.InvariantCulture) + ".",
                innerException);
        }
    }

    private static void SetPartProperties(ModelObject modelObject, FachwerkColumnPartSpec spec, string mark, int componentId)
    {
        modelObject.SetUserProperty("FK_ROLE", spec.Role);
        modelObject.SetUserProperty("FK_MARK", mark ?? string.Empty);
        modelObject.SetUserProperty("FK_BREAK", spec.EndBreakIndex);
        modelObject.SetUserProperty("FK_OWNER", componentId.ToString(CultureInfo.InvariantCulture));
        modelObject.SetUserProperty("FK_GEOMETRY", (modelObject is Beam) ? "BEAM" : ((modelObject is PolyBeam) ? "POLYBEAM" : "CONTOUR_PLATE"));
    }

    private static FachwerkColumnPathDefinition OffsetToward(FachwerkColumnPathDefinition source, FachwerkColumnPathDefinition target, double distance, string role, string profile, double normalOffset)
    {
        FachwerkVector fachwerkVector = TangentAtStart(source.Primitives[0]);
        double num = target.Primitives[0].Start.X - source.Primitives[0].Start.X;
        double num2 = target.Primitives[0].Start.Y - source.Primitives[0].Start.Y;
        double num3 = 0.0 - fachwerkVector.Y;
        double x = fachwerkVector.X;
        double signedDistance = ((num * num3 + num2 * x >= 0.0) ? distance : (0.0 - distance));
        List<FachwerkColumnPrimitiveDefinition> list = new List<FachwerkColumnPrimitiveDefinition>();
        for (int i = 0; i < source.Primitives.Count; i++)
        {
            list.Add(OffsetPrimitive(source.Primitives[i], signedDistance));
        }
        NormalizePrimitiveConnections(list, role);
        return new FachwerkColumnPathDefinition
        {
            Role = role,
            Profile = profile,
            Material = source.Material,
            ClassName = source.ClassName,
            NormalOffset = normalOffset,
            Primitives = list
        };
    }

    private static FachwerkColumnPathDefinition BuildWebAxis(
        FachwerkColumnPathDefinition innerBoundary,
        FachwerkColumnPathDefinition outerBoundary,
        FachwerkColumnSectionTransitionDefinition transition)
    {
        double lowerOffset = FlangeThickness + ParsePlateHeight(transition.LowerWebProfile) * 0.5;
        double upperOffset = FlangeThickness + ParsePlateHeight(transition.UpperWebProfile) * 0.5;
        FachwerkVector startTangent = TangentAtStart(innerBoundary.Primitives[0]);
        double targetX = outerBoundary.Primitives[0].Start.X - innerBoundary.Primitives[0].Start.X;
        double targetY = outerBoundary.Primitives[0].Start.Y - innerBoundary.Primitives[0].Start.Y;
        double leftNormalX = 0.0 - startTangent.Y;
        double leftNormalY = startTangent.X;
        double directionSign = targetX * leftNormalX + targetY * leftNormalY >= 0.0 ? 1.0 : -1.0;

        List<FachwerkColumnPrimitiveDefinition> primitives = new List<FachwerkColumnPrimitiveDefinition>();
        for (int index = 0; index < innerBoundary.Primitives.Count; index++)
        {
            FachwerkColumnPrimitiveDefinition primitive = innerBoundary.Primitives[index];
            double startOffset = directionSign * InterpolateSectionOffset(
                primitive.Start.Y,
                transition,
                lowerOffset,
                upperOffset);
            double endOffset = directionSign * InterpolateSectionOffset(
                primitive.End.Y,
                transition,
                lowerOffset,
                upperOffset);
            primitives.Add(OffsetPrimitiveWithVariableDistance(primitive, startOffset, endOffset));
        }

        NormalizePrimitiveConnections(primitives, "web-axis");
        return new FachwerkColumnPathDefinition
        {
            Role = "web-axis",
            Profile = "PL25",
            Material = innerBoundary.Material,
            ClassName = innerBoundary.ClassName,
            NormalOffset = 0.0,
            Primitives = primitives
        };
    }

    private static double InterpolateSectionOffset(
        double localY,
        FachwerkColumnSectionTransitionDefinition transition,
        double lowerOffset,
        double upperOffset)
    {
        if (localY <= transition.StartY + SectionTransitionTolerance)
        {
            return lowerOffset;
        }
        if (localY >= transition.EndY - SectionTransitionTolerance)
        {
            return upperOffset;
        }
        double parameter = (localY - transition.StartY) / (transition.EndY - transition.StartY);
        return lowerOffset + (upperOffset - lowerOffset) * parameter;
    }

    private static FachwerkColumnPrimitiveDefinition OffsetPrimitiveWithVariableDistance(
        FachwerkColumnPrimitiveDefinition primitive,
        double startOffset,
        double endOffset)
    {
        if (primitive.NormalizedKind() == "arc")
        {
            if (Math.Abs(startOffset - endOffset) > SectionTransitionTolerance)
            {
                throw new InvalidOperationException(
                    "Переход высоты стенки попал внутрь дуги; каталог должен задавать его отдельным прямым сегментом.");
            }
            return OffsetPrimitive(primitive, (startOffset + endOffset) * 0.5);
        }

        double dx = primitive.End.X - primitive.Start.X;
        double dy = primitive.End.Y - primitive.Start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 1e-7)
        {
            throw new InvalidOperationException("Невозможно сместить вырожденный сегмент оси стенки.");
        }
        double normalX = (0.0 - dy) / length;
        double normalY = dx / length;
        return new FachwerkColumnPrimitiveDefinition
        {
            Kind = "line",
            Start = NewPoint(
                primitive.Start.X + normalX * startOffset,
                primitive.Start.Y + normalY * startOffset),
            End = NewPoint(
                primitive.End.X + normalX * endOffset,
                primitive.End.Y + normalY * endOffset)
        };
    }

    internal static double ParsePlateHeight(string profile)
    {
        string[] parts = (profile ?? string.Empty).Split('*');
        if (parts.Length < 2 ||
            !double.TryParse(parts[parts.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
            height <= 0.0)
        {
            throw new InvalidOperationException("Не удалось определить высоту стенки из профиля '" + profile + "'.");
        }
        return height;
    }

    private static FachwerkColumnPathDefinition ClonePath(FachwerkColumnPathDefinition source, string role, string profile, double normalOffset)
    {
        List<FachwerkColumnPrimitiveDefinition> list = new List<FachwerkColumnPrimitiveDefinition>();
        for (int i = 0; i < source.Primitives.Count; i++)
        {
            list.Add(ClonePrimitive(source.Primitives[i]));
        }
        return new FachwerkColumnPathDefinition
        {
            Role = role,
            Profile = profile,
            Material = source.Material,
            ClassName = source.ClassName,
            NormalOffset = normalOffset,
            Primitives = list
        };
    }

    internal static FachwerkColumnPrimitiveDefinition OffsetPrimitive(FachwerkColumnPrimitiveDefinition source, double signedDistance)
    {
        if (source.NormalizedKind() == "line")
        {
            FachwerkVector fachwerkVector = UnitVector(source.End.X - source.Start.X, source.End.Y - source.Start.Y);
            double num = (0.0 - fachwerkVector.Y) * signedDistance;
            double num2 = fachwerkVector.X * signedDistance;
            return new FachwerkColumnPrimitiveDefinition
            {
                Kind = "line",
                Start = NewPoint(source.Start.X + num, source.Start.Y + num2),
                End = NewPoint(source.End.X + num, source.End.Y + num2)
            };
        }
        double num3 = Distance(source.Start, source.Center);
        int num4 = Math.Sign(source.SweepDeg);
        double num5 = num3 - (double)num4 * signedDistance;
        if (num5 <= 0.0001)
        {
            throw new InvalidOperationException("Смещение дуги уничтожило её радиус.");
        }
        return new FachwerkColumnPrimitiveDefinition
        {
            Kind = "arc",
            Center = source.Center.Clone(),
            Start = ScaleFromCenter(source.Start, source.Center, num5 / num3),
            End = ScaleFromCenter(source.End, source.Center, num5 / num3),
            SweepDeg = source.SweepDeg
        };
    }

    internal static void NormalizePrimitiveConnections(IList<FachwerkColumnPrimitiveDefinition> primitives, string role)
    {
        for (int i = 1; i < primitives.Count; i++)
        {
            FachwerkColumnPrimitiveDefinition fachwerkColumnPrimitiveDefinition = primitives[i - 1];
            FachwerkColumnPrimitiveDefinition fachwerkColumnPrimitiveDefinition2 = primitives[i];
            double num = Distance(fachwerkColumnPrimitiveDefinition.End, fachwerkColumnPrimitiveDefinition2.Start);
            if (!(num <= 0.0001))
            {
                FachwerkColumnLocalPoint fachwerkColumnLocalPoint = NewPoint((fachwerkColumnPrimitiveDefinition.End.X + fachwerkColumnPrimitiveDefinition2.Start.X) * 0.5, (fachwerkColumnPrimitiveDefinition.End.Y + fachwerkColumnPrimitiveDefinition2.Start.Y) * 0.5);
                FachwerkColumnLocalPoint intersection;
                bool flag = TryIntersectPrimitiveSupports(fachwerkColumnPrimitiveDefinition, fachwerkColumnPrimitiveDefinition2, fachwerkColumnLocalPoint, out intersection);
                if (!flag && num <= 0.05)
                {
                    intersection = ((fachwerkColumnPrimitiveDefinition.NormalizedKind() == "arc") ? fachwerkColumnPrimitiveDefinition.End.Clone() : ((fachwerkColumnPrimitiveDefinition2.NormalizedKind() == "arc") ? fachwerkColumnPrimitiveDefinition2.Start.Clone() : fachwerkColumnLocalPoint));
                }
                else if (!flag || Distance(intersection, fachwerkColumnLocalPoint) > 100.0)
                {
                    throw new InvalidOperationException("Не удалось точно сопрячь смещённую траекторию '" + role + "'; разрыв " + num.ToString("0.###", CultureInfo.InvariantCulture) + " мм.");
                }
                fachwerkColumnPrimitiveDefinition.End = intersection.Clone();
                fachwerkColumnPrimitiveDefinition2.Start = intersection.Clone();
                RecalculateSweep(fachwerkColumnPrimitiveDefinition);
                RecalculateSweep(fachwerkColumnPrimitiveDefinition2);
            }
        }
    }

    private static bool TryIntersectPrimitiveSupports(FachwerkColumnPrimitiveDefinition first, FachwerkColumnPrimitiveDefinition second, FachwerkColumnLocalPoint reference, out FachwerkColumnLocalPoint intersection)
    {
        bool flag = first.NormalizedKind() == "line";
        bool flag2 = second.NormalizedKind() == "line";
        if (flag && flag2)
        {
            return TryIntersectLines(first.Start, first.End, second.Start, second.End, out intersection);
        }
        IReadOnlyList<FachwerkColumnLocalPoint> readOnlyList = (flag ? IntersectLineAndCircle(first.Start, first.End, second.Center, Distance(second.Start, second.Center)) : ((!flag2) ? IntersectCircles(first.Center, Distance(first.Start, first.Center), second.Center, Distance(second.Start, second.Center)) : IntersectLineAndCircle(second.Start, second.End, first.Center, Distance(first.Start, first.Center))));
        intersection = null;
        double num = double.MaxValue;
        for (int i = 0; i < readOnlyList.Count; i++)
        {
            double num2 = Distance(readOnlyList[i], reference);
            if (!(num2 >= num))
            {
                num = num2;
                intersection = readOnlyList[i];
            }
        }
        return intersection != null;
    }

    private static IReadOnlyList<FachwerkColumnLocalPoint> IntersectLineAndCircle(FachwerkColumnLocalPoint lineStart, FachwerkColumnLocalPoint lineEnd, FachwerkColumnLocalPoint center, double radius)
    {
        double num = lineEnd.X - lineStart.X;
        double num2 = lineEnd.Y - lineStart.Y;
        double num3 = lineStart.X - center.X;
        double num4 = lineStart.Y - center.Y;
        double num5 = num * num + num2 * num2;
        if (num5 < 1E-07)
        {
            return Array.Empty<FachwerkColumnLocalPoint>();
        }
        double num6 = 2.0 * (num3 * num + num4 * num2);
        double num7 = num3 * num3 + num4 * num4 - radius * radius;
        double num8 = num6 * num6 - 4.0 * num5 * num7;
        if (num8 < -0.0001)
        {
            return Array.Empty<FachwerkColumnLocalPoint>();
        }
        num8 = Math.Max(0.0, num8);
        double num9 = Math.Sqrt(num8);
        double num10 = (0.0 - num6 - num9) / (2.0 * num5);
        double num11 = (0.0 - num6 + num9) / (2.0 * num5);
        FachwerkColumnLocalPoint fachwerkColumnLocalPoint = NewPoint(lineStart.X + num * num10, lineStart.Y + num2 * num10);
        if (!(Math.Abs(num10 - num11) < 1E-07))
        {
            return new FachwerkColumnLocalPoint[2]
            {
                fachwerkColumnLocalPoint,
                NewPoint(lineStart.X + num * num11, lineStart.Y + num2 * num11)
            };
        }
        return new FachwerkColumnLocalPoint[1] { fachwerkColumnLocalPoint };
    }

    private static IReadOnlyList<FachwerkColumnLocalPoint> IntersectCircles(FachwerkColumnLocalPoint firstCenter, double firstRadius, FachwerkColumnLocalPoint secondCenter, double secondRadius)
    {
        double num = secondCenter.X - firstCenter.X;
        double num2 = secondCenter.Y - firstCenter.Y;
        double num3 = Math.Sqrt(num * num + num2 * num2);
        if (num3 < 1E-07 || num3 > firstRadius + secondRadius + 0.0001 || num3 < Math.Abs(firstRadius - secondRadius) - 0.0001)
        {
            return Array.Empty<FachwerkColumnLocalPoint>();
        }
        double num4 = (firstRadius * firstRadius - secondRadius * secondRadius + num3 * num3) / (2.0 * num3);
        double num5 = firstRadius * firstRadius - num4 * num4;
        if (num5 < -0.0001)
        {
            return Array.Empty<FachwerkColumnLocalPoint>();
        }
        double num6 = Math.Sqrt(Math.Max(0.0, num5));
        double num7 = firstCenter.X + num4 * num / num3;
        double num8 = firstCenter.Y + num4 * num2 / num3;
        double num9 = (0.0 - num2) * num6 / num3;
        double num10 = num * num6 / num3;
        FachwerkColumnLocalPoint fachwerkColumnLocalPoint = NewPoint(num7 + num9, num8 + num10);
        if (!(num6 < 1E-07))
        {
            return new FachwerkColumnLocalPoint[2]
            {
                fachwerkColumnLocalPoint,
                NewPoint(num7 - num9, num8 - num10)
            };
        }
        return new FachwerkColumnLocalPoint[1] { fachwerkColumnLocalPoint };
    }

    private static void RecalculateSweep(FachwerkColumnPrimitiveDefinition primitive)
    {
        if (!(primitive.NormalizedKind() != "arc"))
        {
            double num = Math.Atan2(primitive.Start.Y - primitive.Center.Y, primitive.Start.X - primitive.Center.X);
            double num2 = Math.Atan2(primitive.End.Y - primitive.Center.Y, primitive.End.X - primitive.Center.X);
            double num3 = ((primitive.SweepDeg >= 0.0) ? PositiveAngle(num2 - num) : (0.0 - PositiveAngle(num - num2)));
            primitive.SweepDeg = num3 * 180.0 / Math.PI;
        }
    }

    private static IReadOnlyList<FachwerkColumnPrimitiveDefinition> ReversePrimitives(IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives)
    {
        List<FachwerkColumnPrimitiveDefinition> list = new List<FachwerkColumnPrimitiveDefinition>();
        for (int num = primitives.Count - 1; num >= 0; num--)
        {
            FachwerkColumnPrimitiveDefinition fachwerkColumnPrimitiveDefinition = primitives[num];
            list.Add(new FachwerkColumnPrimitiveDefinition
            {
                Kind = fachwerkColumnPrimitiveDefinition.NormalizedKind(),
                Start = fachwerkColumnPrimitiveDefinition.End.Clone(),
                End = fachwerkColumnPrimitiveDefinition.Start.Clone(),
                Center = fachwerkColumnPrimitiveDefinition.Center?.Clone(),
                SweepDeg = 0.0 - fachwerkColumnPrimitiveDefinition.SweepDeg
            });
        }
        return list;
    }

    private static FachwerkColumnPrimitiveDefinition ClonePrimitive(FachwerkColumnPrimitiveDefinition item)
    {
        return new FachwerkColumnPrimitiveDefinition
        {
            Kind = item.NormalizedKind(),
            Start = item.Start.Clone(),
            End = item.End.Clone(),
            Center = item.Center?.Clone(),
            SweepDeg = item.SweepDeg
        };
    }

    private static FachwerkVector TangentAtStart(FachwerkColumnPrimitiveDefinition primitive)
    {
        if (primitive.NormalizedKind() == "line")
        {
            return UnitVector(primitive.End.X - primitive.Start.X, primitive.End.Y - primitive.Start.Y);
        }
        double num = primitive.Start.X - primitive.Center.X;
        double num2 = primitive.Start.Y - primitive.Center.Y;
        if (!(primitive.SweepDeg >= 0.0))
        {
            return UnitVector(num2, 0.0 - num);
        }
        return UnitVector(0.0 - num2, num);
    }

    private static FachwerkVector UnitVector(double x, double y)
    {
        double num = Math.Sqrt(x * x + y * y);
        if (num < 1E-07)
        {
            throw new InvalidOperationException("Невозможно вычислить направление вырожденного сегмента.");
        }
        return new FachwerkVector(x / num, y / num, 0.0);
    }

    private static FachwerkColumnLocalPoint ScaleFromCenter(FachwerkColumnLocalPoint point, FachwerkColumnLocalPoint center, double factor)
    {
        return NewPoint(center.X + (point.X - center.X) * factor, center.Y + (point.Y - center.Y) * factor);
    }

    private static FachwerkColumnLocalPoint NewPoint(double x, double y)
    {
        return new FachwerkColumnLocalPoint
        {
            X = x,
            Y = y
        };
    }
}

internal enum FachwerkColumnPartKind
{
    Flange,
    Web,
}

internal sealed class FachwerkColumnPartSpec
{
    public FachwerkColumnPartKind Kind { get; private set; }

    public string Role { get; private set; }

    public string Profile { get; private set; }

    public string Material { get; private set; }

    public string ClassName { get; private set; }

    public IReadOnlyList<FachwerkColumnPrimitiveDefinition> FirstBoundary { get; private set; }

    public IReadOnlyList<FachwerkColumnPrimitiveDefinition> SecondBoundary { get; private set; }

    public int EndBreakIndex { get; private set; }

    public double NormalOffset { get; private set; }

    public int AssemblySegmentIndex { get; private set; }

    private FachwerkColumnPartSpec()
    {
    }

    public static FachwerkColumnPartSpec Flange(
        string role,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> path,
        FachwerkColumnPathDefinition source,
        int endBreakIndex,
        int assemblySegmentIndex = 0)
    {
        return new FachwerkColumnPartSpec
        {
            Kind = FachwerkColumnPartKind.Flange,
            Role = role,
            Profile = source.Profile,
            Material = source.Material,
            ClassName = source.ClassName,
            FirstBoundary = path,
            SecondBoundary = Array.Empty<FachwerkColumnPrimitiveDefinition>(),
            EndBreakIndex = endBreakIndex,
            NormalOffset = 0.0,
            AssemblySegmentIndex = assemblySegmentIndex
        };
    }

    public static FachwerkColumnPartSpec Web(
        string role,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> path,
        FachwerkColumnPathDefinition source,
        string profile,
        int endBreakIndex,
        double normalOffset,
        int assemblySegmentIndex = 0)
    {
        return new FachwerkColumnPartSpec
        {
            Kind = FachwerkColumnPartKind.Web,
            Role = role,
            Profile = profile,
            Material = source.Material,
            ClassName = source.ClassName,
            FirstBoundary = path,
            SecondBoundary = Array.Empty<FachwerkColumnPrimitiveDefinition>(),
            EndBreakIndex = endBreakIndex,
            NormalOffset = normalOffset,
            AssemblySegmentIndex = assemblySegmentIndex
        };
    }
}
