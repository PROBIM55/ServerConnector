#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace Structura.Tekla.Fachwerk;

internal static class FachwerkLowerRigelNodeTeklaAdapter
{
    private const double ArcChord = 25.0;
    private const double PointTolerance = 1e-5;
    private const double TubeCutterAxisOverrun = 500.0;
    internal const double TubeOuterCutterDiameter = 800.0;
    internal const double CentralTubeAxisOverrun = 200.0;
    private const double BevelRemoval = 12.0;
    private const double BevelCrossOverrun = 2.0;
    private const double BevelAlongOverrun = 5.0;
    internal const string NodeMaterial = "C355-5";
    internal const string RegularPartPrefix = "515-60.";
    internal const string BottomClosureProfile = "PL6";
    internal const string BottomClosureMaterial = "C245-4";
    private const double BottomClosureThickness = 6.0;
    private const string BottomClosureName = "Нижняя заглушка";
    internal const string InsertPrefix = "515-60.ВС-";
    private const string InsertClass = "7";

    internal static IReadOnlyList<Point> ReadAxis(Part part)
    {
        if (part == null) throw new ArgumentNullException(nameof(part));

        if (part is PolyBeam polyBeam)
        {
            try
            {
                IReadOnlyList<Point> sampled = SamplePolycurve(
                    polyBeam.GetCenterLinePolycurve());
                if (sampled.Count >= 2)
                {
                    return sampled;
                }
            }
            catch
            {
                // Tekla can reject Polycurve reads for an outdated model
                // handle. The ordinary centerline remains a valid fallback.
            }
        }

        ArrayList centerLine = part.GetCenterLine(false);
        var result = new List<Point>();
        foreach (object item in centerLine)
        {
            if (item is Point point)
            {
                AppendDistinct(result, point);
            }
        }
        if (result.Count < 2)
        {
            throw new InvalidOperationException(
                "У детали ID " + ObjectId(part) +
                " не удалось прочитать ось минимум из двух точек.");
        }
        return result;
    }

    internal static ContourPlate CreateLowerFlangeFragment(
        Part template,
        FachwerkLowerRigelFlangeSplice splice,
        int componentId)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (splice == null) throw new ArgumentNullException(nameof(splice));

        var plate = CreatePlate(
            splice.LowerFragmentBoundary,
            "PL" + splice.Thickness.ToString(
                "0.###",
                CultureInfo.InvariantCulture),
            NodeMaterial,
            template.Class,
            GetLowerFlangeName(splice.Role),
            splice.Role + "-lower-fragment",
            componentId);
        if (string.Equals(
                splice.Role,
                "outer-flange",
                StringComparison.OrdinalIgnoreCase))
        {
            plate.Position.Depth = Position.DepthEnum.BEHIND;
            plate.Position.DepthOffset = 0.0;
        }
        else
        {
            Vector contourNormal = BoundaryNormal(
                splice.LowerFragmentBoundary);
            double outwardOrientation =
                FachwerkLowerRigelNodeGeometry.Dot(
                    contourNormal,
                    splice.OutwardNormal);
            if (Math.Abs(outwardOrientation) < 0.9)
            {
                throw new InvalidOperationException(
                    "Контур нижнего пояса '" + splice.Role +
                    "' не лежит во внутренней плоскости пояса.");
            }
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            plate.Position.DepthOffset =
                Math.Sign(outwardOrientation) * splice.Thickness * 0.5;
        }
        plate.Finish = template.Finish;
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала нижний фрагмент пояса '" +
                splice.Role + "'.");
        }
        return plate;
    }

    internal static ContourPlate CreateFlangeInsert(
        Part template,
        FachwerkLowerRigelFlangeSplice splice,
        int componentId)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (splice == null) throw new ArgumentNullException(nameof(splice));

        string insertName = GetFlangeInsertName(splice.Role);
        var plate = CreatePlate(
            splice.InsertBoundary,
            "PL" + splice.Thickness.ToString(
                "0.###",
                CultureInfo.InvariantCulture),
            NodeMaterial,
            InsertClass,
            insertName,
            splice.Role + "-insert",
            componentId);
        plate.Finish = template.Finish;
        plate.PartNumber = new NumberingSeries(InsertPrefix, 1);
        plate.AssemblyNumber = new NumberingSeries(InsertPrefix, 1);
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала вставку пояса '" +
                splice.Role + "'.");
        }

        Assembly assembly = plate.GetAssembly();
        if (assembly == null)
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не вернула сборку вставки пояса '" +
                splice.Role + "'.");
        }
        assembly.Name = insertName;
        assembly.AssemblyNumber = new NumberingSeries(InsertPrefix, 1);
        if (!assembly.Modify())
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не применила атрибуты сборки вставки пояса '" +
                splice.Role + "'.");
        }
        return plate;
    }

    internal static Beam CreateCentralTube(
        Beam template,
        FachwerkLowerRigelLayout layout,
        string material,
        string className,
        int componentId)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (layout == null) throw new ArgumentNullException(nameof(layout));

        Vector direction = FachwerkLowerRigelNodeGeometry.Normalize(
            FachwerkLowerRigelNodeGeometry.VectorBetween(
                layout.LeftCentralTubePoint,
                layout.RightCentralTubePoint),
            "ось внутренней трубы");
        var beam = new Beam(
            FachwerkLowerRigelNodeGeometry.Add(
                layout.LeftCentralTubePoint,
                direction,
                -CentralTubeAxisOverrun),
            FachwerkLowerRigelNodeGeometry.Add(
                layout.RightCentralTubePoint,
                direction,
                CentralTubeAxisOverrun))
        {
            Name = "Внутренняя труба",
            Class = className,
            Finish = template.Finish,
        };
        beam.Profile.ProfileString = template.Profile.ProfileString;
        beam.Material.MaterialString = NodeMaterial;
        CopyPosition(template.Position, beam.Position);
        ApplyRegularNumbering(beam);
        Tag(beam, "central-tube", componentId);
        if (!beam.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала центральную трубу нижнего ригеля.");
        }
        return beam;
    }

    internal static BooleanPart CutByExtendedCentralTube(
        Part father,
        Beam centralTube,
        string role,
        int componentId)
    {
        if (father == null) throw new ArgumentNullException(nameof(father));
        if (centralTube == null)
            throw new ArgumentNullException(nameof(centralTube));

        Vector direction = FachwerkLowerRigelNodeGeometry.Normalize(
            FachwerkLowerRigelNodeGeometry.VectorBetween(
                centralTube.StartPoint,
                centralTube.EndPoint),
            role + " ось центральной трубы");
        Beam cutter = null;
        BooleanPart cut = null;
        try
        {
            cutter = new Beam(
                FachwerkLowerRigelNodeGeometry.Add(
                    centralTube.StartPoint,
                    direction,
                    -TubeCutterAxisOverrun),
                FachwerkLowerRigelNodeGeometry.Add(
                    centralTube.EndPoint,
                    direction,
                    TubeCutterAxisOverrun))
            {
                Name = "Обрезка наружного пояса по центральной трубе",
                Class = BooleanPart.BooleanOperativeClassName,
                Finish = centralTube.Finish,
            };
            cutter.Profile.ProfileString =
                centralTube.Profile.ProfileString;
            cutter.Material.MaterialString =
                centralTube.Material.MaterialString;
            CopyPosition(centralTube.Position, cutter.Position);
            Tag(cutter, role + "-operative", componentId);
            if (!cutter.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала удлинённый резак центральной трубы '" +
                    role + "'.");
            }

            cut = CreateBoolean(
                father,
                cutter,
                BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
                role,
                componentId);
            DeleteTemporary(cutter, role + "-operative");
            cutter = null;
            return cut;
        }
        catch
        {
            TryDelete(cut);
            TryDelete(cutter);
            throw;
        }
    }

    internal static Weld CreateShopWeld(
        Part tube,
        Part secondary,
        string role,
        int componentId)
    {
        if (tube == null) throw new ArgumentNullException(nameof(tube));
        if (secondary == null)
            throw new ArgumentNullException(nameof(secondary));

        var weld = new Weld
        {
            MainObject = tube,
            SecondaryObject = secondary,
            ShopWeld = true,
            ConnectAssemblies = false,
            ReferenceText = role,
        };
        Tag(weld, role, componentId);
        if (!weld.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала цеховую сварку '" + role + "'.");
        }
        return weld;
    }

    internal static ContourPlate CreateTransitionPlate(
        FachwerkLowerRigelTransition transition,
        Part alignmentPart,
        string profile,
        string material,
        string className,
        int componentId)
    {
        if (transition == null)
            throw new ArgumentNullException(nameof(transition));
        if (alignmentPart == null)
            throw new ArgumentNullException(nameof(alignmentPart));

        var plate = CreatePlate(
            transition.Boundary,
            profile,
            material,
            className,
            GetTransitionName(transition.Role),
            transition.Role,
            componentId);
        AlignDepthToPart(
            plate,
            transition.Boundary,
            alignmentPart,
            transition.ReferencePlane);
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала переходную пластину '" +
                transition.Role + "'.");
        }
        CorrectInsertedDepth(
            plate,
            transition.Boundary,
            alignmentPart,
            transition.ReferencePlane);
        return plate;
    }

    private static string GetLowerFlangeName(string role)
    {
        if (string.Equals(role, "outer-flange", StringComparison.OrdinalIgnoreCase))
        {
            return "Наружный пояс узла";
        }
        if (string.Equals(role, "inner-flange", StringComparison.OrdinalIgnoreCase))
        {
            return "Внутренний пояс узла";
        }

        throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "Неизвестная роль нижнего фрагмента пояса.");
    }

    private static string GetFlangeInsertName(string role)
    {
        if (string.Equals(role, "outer-flange", StringComparison.OrdinalIgnoreCase))
        {
            return "Наружная вставка";
        }
        if (string.Equals(role, "inner-flange", StringComparison.OrdinalIgnoreCase))
        {
            return "Внутренняя вставка";
        }

        throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "Неизвестная роль вставки пояса.");
    }

    private static string GetTransitionName(string role)
    {
        if (string.Equals(role, "left-transition", StringComparison.OrdinalIgnoreCase))
        {
            return "Левая стенка узла";
        }
        if (string.Equals(role, "right-transition", StringComparison.OrdinalIgnoreCase))
        {
            return "Правая стенка узла";
        }

        throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "Неизвестная роль переходной стенки.");
    }

    internal static ContourPlate CreateBottomClosure(
        FachwerkLowerRigelBottomClosure closure,
        string className,
        int componentId)
    {
        if (closure == null) throw new ArgumentNullException(nameof(closure));

        var plate = CreatePlate(
            closure.Boundary,
            BottomClosureProfile,
            BottomClosureMaterial,
            string.IsNullOrWhiteSpace(className) ? "20" : className,
            BottomClosureName,
            "bottom-closure",
            componentId);
        Vector contourNormal = BoundaryNormal(closure.Boundary);
        double extrusionOrientation =
            FachwerkLowerRigelNodeGeometry.Dot(
                contourNormal,
                closure.ExtrusionDirection);
        if (Math.Abs(extrusionOrientation) < 0.05)
        {
            throw new InvalidOperationException(
                "Плоскость нижней заглушки не перпендикулярна " +
                "направлению её выдавливания.");
        }

        // Place the upper face on the transition ends and extrude the full
        // PL6 thickness away from the node.
        plate.Position.Depth = Position.DepthEnum.MIDDLE;
        plate.Position.DepthOffset =
            Math.Sign(extrusionOrientation) * BottomClosureThickness * 0.5;
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала нижнюю заглушку.");
        }
        return plate;
    }

    internal static Fitting CreateFitting(
        Part father,
        FachwerkLowerRigelPlane sourcePlane,
        Point keepPoint,
        string role,
        int componentId)
    {
        if (father == null) throw new ArgumentNullException(nameof(father));
        if (sourcePlane == null)
            throw new ArgumentNullException(nameof(sourcePlane));
        if (keepPoint == null) throw new ArgumentNullException(nameof(keepPoint));

        FachwerkLowerRigelPlane plane = sourcePlane.OrientedToward(keepPoint);
        var fitting = new Fitting
        {
            Father = father,
            Plane = plane.ToTeklaPlane(),
        };
        Tag(fitting, role, componentId);
        if (!fitting.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала подгонку '" + role + "'.");
        }
        return fitting;
    }

    internal static CutPlane CreateCutPlane(
        Part father,
        FachwerkLowerRigelPlane sourcePlane,
        Point keepPoint,
        string role,
        int componentId)
    {
        if (father == null) throw new ArgumentNullException(nameof(father));
        if (sourcePlane == null)
            throw new ArgumentNullException(nameof(sourcePlane));
        if (keepPoint == null) throw new ArgumentNullException(nameof(keepPoint));

        // CutPlane removes the half-space indicated by its normal. Point the
        // normal away from the span that must remain between the two cuts.
        FachwerkLowerRigelPlane plane =
            sourcePlane.OrientedAwayFrom(keepPoint);
        var cutPlane = new CutPlane
        {
            Father = father,
            Plane = plane.ToTeklaPlane(),
        };
        Tag(cutPlane, role, componentId);
        if (!cutPlane.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала срез по линии '" + role + "'.");
        }
        return cutPlane;
    }

    internal static IReadOnlyList<BooleanPart> CreateTransitionBevels(
        ContourPlate transitionPlate,
        FachwerkLowerRigelTransition transitionGeometry,
        string material,
        int componentId)
    {
        if (transitionPlate == null)
            throw new ArgumentNullException(nameof(transitionPlate));
        if (transitionGeometry == null)
            throw new ArgumentNullException(nameof(transitionGeometry));

        var result = new List<BooleanPart>();
        try
        {
            AddEdgeBevels(
                result,
                transitionPlate,
                transitionGeometry.FirstEdge,
                transitionGeometry.SecondEdge,
                transitionGeometry.InwardDirection,
                material,
                transitionGeometry.Role + "-first-edge",
                componentId);
            AddEdgeBevels(
                result,
                transitionPlate,
                transitionGeometry.SecondEdge,
                transitionGeometry.FirstEdge,
                transitionGeometry.InwardDirection,
                material,
                transitionGeometry.Role + "-second-edge",
                componentId);
            return result;
        }
        catch
        {
            for (var index = result.Count - 1; index >= 0; index--)
            {
                TryDelete(result[index]);
            }
            throw;
        }
    }

    private static void AddEdgeBevels(
        ICollection<BooleanPart> target,
        ContourPlate father,
        IReadOnlyList<Point> edge,
        IReadOnlyList<Point> oppositeEdge,
        Vector inwardDirection,
        string material,
        string role,
        int componentId)
    {
        if (edge == null ||
            oppositeEdge == null ||
            edge.Count != oppositeEdge.Count ||
            edge.Count < 2)
        {
            throw new InvalidOperationException(
                "Не удалось получить парные кромки для разделки '" +
                role + "'.");
        }

        for (var index = 0; index < edge.Count - 1; index++)
        {
            Point start = edge[index];
            Point end = edge[index + 1];
            double length = FachwerkLowerRigelNodeGeometry.Distance(
                start,
                end);
            if (length <= PointTolerance)
            {
                continue;
            }

            Vector along = FachwerkLowerRigelNodeGeometry.Normalize(
                FachwerkLowerRigelNodeGeometry.VectorBetween(start, end),
                role + " касательная");
            Vector depth = ProjectPerpendicular(
                inwardDirection,
                along,
                role + " направление глубины");
            Vector towardOpposite =
                FachwerkLowerRigelNodeGeometry.VectorBetween(
                    FachwerkLowerRigelNodeGeometry.Lerp(
                        start,
                        end,
                        0.5),
                    FachwerkLowerRigelNodeGeometry.Lerp(
                        oppositeEdge[index],
                        oppositeEdge[index + 1],
                        0.5));
            Vector across = ProjectPerpendicular(
                towardOpposite,
                along,
                depth,
                role + " направление внутрь ширины");

            ContourPlate cutter = CreateBevelCutter(
                FachwerkLowerRigelNodeGeometry.Lerp(start, end, 0.5),
                along,
                across,
                depth,
                length,
                material,
                role + "-" + index.ToString(CultureInfo.InvariantCulture),
                componentId);
            BooleanPart cut = null;
            try
            {
                if (!cutter.Insert())
                {
                    throw new InvalidOperationException(
                        "Tekla не создала режущий клин '" + role + "'.");
                }
                cut = CreateBoolean(
                    father,
                    cutter,
                    BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
                    role + "-" + index.ToString(CultureInfo.InvariantCulture),
                    componentId);
                target.Add(cut);
                TryDelete(cutter);
            }
            catch
            {
                TryDelete(cut);
                TryDelete(cutter);
                throw;
            }
        }
    }

    private static ContourPlate CreateBevelCutter(
        Point edgeMidpoint,
        Vector along,
        Vector across,
        Vector depth,
        double segmentLength,
        string material,
        string role,
        int componentId)
    {
        var cutter = CreatePlate(
            BuildBevelCutterBoundary(edgeMidpoint, across, depth),
            "PL" +
            (segmentLength + 2 * BevelAlongOverrun).ToString(
                "0.###",
                CultureInfo.InvariantCulture),
            material,
            BooleanPart.BooleanOperativeClassName,
            "Разделка переходной пластины",
            role + "-operative",
            componentId);
        cutter.Position.Depth = Position.DepthEnum.MIDDLE;
        cutter.Position.DepthOffset = 0;
        return cutter;
    }

    internal static IReadOnlyList<Point> BuildBevelCutterBoundary(
        Point edgePoint,
        Vector across,
        Vector depth)
    {
        Vector normalizedAcross = FachwerkLowerRigelNodeGeometry.Normalize(
            across,
            "направление разделки по ширине");
        Vector normalizedDepth = FachwerkLowerRigelNodeGeometry.Normalize(
            depth,
            "направление разделки по толщине");
        double leg = BevelRemoval + BevelCrossOverrun;
        return new[]
        {
            At(
                edgePoint,
                normalizedAcross,
                -BevelCrossOverrun,
                normalizedDepth,
                -BevelCrossOverrun),
            At(
                edgePoint,
                normalizedAcross,
                leg,
                normalizedDepth,
                -BevelCrossOverrun),
            At(
                edgePoint,
                normalizedAcross,
                -BevelCrossOverrun,
                normalizedDepth,
                leg),
        };
    }

    private static Vector ProjectPerpendicular(
        Vector source,
        Vector axis,
        string label)
    {
        Vector projected = FachwerkLowerRigelNodeGeometry.Subtract(
            source,
            FachwerkLowerRigelNodeGeometry.Scale(
                axis,
                FachwerkLowerRigelNodeGeometry.Dot(source, axis)));
        return FachwerkLowerRigelNodeGeometry.Normalize(projected, label);
    }

    private static Vector ProjectPerpendicular(
        Vector source,
        Vector firstAxis,
        Vector secondAxis,
        string label)
    {
        Vector projected = FachwerkLowerRigelNodeGeometry.Subtract(
            FachwerkLowerRigelNodeGeometry.Subtract(
                source,
                FachwerkLowerRigelNodeGeometry.Scale(
                    firstAxis,
                    FachwerkLowerRigelNodeGeometry.Dot(
                        source,
                        firstAxis))),
            FachwerkLowerRigelNodeGeometry.Scale(
                secondAxis,
                FachwerkLowerRigelNodeGeometry.Dot(
                    source,
                    secondAxis)));
        return FachwerkLowerRigelNodeGeometry.Normalize(projected, label);
    }

    private static Point At(
        Point origin,
        Vector first,
        double firstDistance,
        Vector second,
        double secondDistance)
    {
        return new Point(
            origin.X +
            first.X * firstDistance +
            second.X * secondDistance,
            origin.Y +
            first.Y * firstDistance +
            second.Y * secondDistance,
            origin.Z +
            first.Z * firstDistance +
            second.Z * secondDistance);
    }

    internal static BooleanPart AddTrimmedEndPlate(
        ContourPlate transition,
        FachwerkLowerRigelTransition transitionGeometry,
        Part alignmentPart,
        IReadOnlyList<Point> endPlateBoundary,
        Point tubeAxisPoint,
        Vector tubeAxisDirection,
        double tubeDiameter,
        string profile,
        string material,
        string role,
        int componentId)
    {
        if (transition == null)
            throw new ArgumentNullException(nameof(transition));
        if (transitionGeometry == null)
            throw new ArgumentNullException(nameof(transitionGeometry));
        if (alignmentPart == null)
            throw new ArgumentNullException(nameof(alignmentPart));
        if (tubeAxisPoint == null)
            throw new ArgumentNullException(nameof(tubeAxisPoint));
        if (tubeAxisDirection == null)
            throw new ArgumentNullException(nameof(tubeAxisDirection));

        ContourPlate endPlate = CreatePlate(
            endPlateBoundary,
            profile,
            material,
            BooleanPart.BooleanOperativeClassName,
            "Торцевая вставка",
            role + "-operative",
            componentId);
        AlignDepthToPart(
            endPlate,
            endPlateBoundary,
            alignmentPart,
            transitionGeometry.ReferencePlane);
        Beam outerCutter = null;
        Beam innerCutter = null;
        BooleanPart shellCut = null;
        BooleanPart trimCut = null;
        BooleanPart addition = null;
        try
        {
            if (!endPlate.Insert())
            {
                throw new InvalidOperationException(
                    "Tekla не создала торцевую вставку '" + role + "'.");
            }
            CorrectInsertedDepth(
                endPlate,
                endPlateBoundary,
                alignmentPart,
                transitionGeometry.ReferencePlane);

            outerCutter = CreateTubeCutter(
                tubeAxisPoint,
                tubeAxisDirection,
                TubeOuterCutterDiameter,
                material,
                role + "-outer-shell",
                componentId);
            innerCutter = CreateTubeCutter(
                tubeAxisPoint,
                tubeAxisDirection,
                tubeDiameter,
                material,
                role + "-inner-shell",
                componentId);
            shellCut = CreateBoolean(
                outerCutter,
                innerCutter,
                BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
                role + "-shell",
                componentId);
            DeleteTemporary(
                innerCutter,
                role + "-inner-shell-operative");
            innerCutter = null;

            trimCut = CreateBoolean(
                endPlate,
                outerCutter,
                BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
                role + "-trim",
                componentId);

            // BooleanPart copies its operative solid during Insert(). Remove
            // the construction history from the source shell before deleting
            // its physical carrier, otherwise Tekla keeps the D800 beam as a
            // component part.
            DeleteTemporary(shellCut, role + "-shell-operation");
            shellCut = null;
            DeleteTemporary(outerCutter, role + "-outer-shell-carrier");
            outerCutter = null;

            addition = CreateBoolean(
                transition,
                endPlate,
                BooleanPart.BooleanTypeEnum.BOOLEAN_ADD,
                role,
                componentId);

            // Tekla deletes the original operative part automatically for a
            // BOOLEAN_ADD. Its trim Boolean is deleted together with it.
            trimCut = null;
            endPlate = null;
            return addition;
        }
        catch
        {
            TryDelete(addition);
            TryDelete(trimCut);
            TryDelete(shellCut);
            TryDelete(innerCutter);
            TryDelete(outerCutter);
            TryDelete(endPlate);
            throw;
        }
    }

    private static Beam CreateTubeCutter(
        Point axisPoint,
        Vector axisDirection,
        double diameter,
        string material,
        string role,
        int componentId)
    {
        Vector direction = FachwerkLowerRigelNodeGeometry.Normalize(
            axisDirection,
            role + " ось цилиндрического резака");
        var cutter = new Beam(
            FachwerkLowerRigelNodeGeometry.Add(
                axisPoint,
                direction,
                -TubeCutterAxisOverrun),
            FachwerkLowerRigelNodeGeometry.Add(
                axisPoint,
                direction,
                TubeCutterAxisOverrun))
        {
            Name = "Цилиндрическая обрезка",
            Class = BooleanPart.BooleanOperativeClassName,
        };
        cutter.Profile.ProfileString =
            "D" + diameter.ToString("0.###", CultureInfo.InvariantCulture);
        cutter.Material.MaterialString = material;
        cutter.Position.Plane = Position.PlaneEnum.MIDDLE;
        cutter.Position.Depth = Position.DepthEnum.MIDDLE;
        cutter.Position.Rotation = Position.RotationEnum.FRONT;
        Tag(cutter, role, componentId);
        if (!cutter.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала цилиндрический резак '" + role + "'.");
        }
        return cutter;
    }

    private static BooleanPart CreateBoolean(
        Part father,
        Part operative,
        BooleanPart.BooleanTypeEnum type,
        string role,
        int componentId)
    {
        var booleanPart = new BooleanPart
        {
            Father = father,
            Type = type,
        };
        if (!booleanPart.SetOperativePart(operative))
        {
            throw new InvalidOperationException(
                "Tekla не приняла операционную деталь для '" + role + "'.");
        }
        Tag(booleanPart, role, componentId);
        if (!booleanPart.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала булеву операцию '" + role + "'.");
        }
        return booleanPart;
    }

    private static void DeleteTemporary(
        ModelObject modelObject,
        string role)
    {
        if (modelObject == null ||
            modelObject.Identifier == null ||
            modelObject.Identifier.ID == 0)
        {
            return;
        }

        try
        {
            if (!modelObject.Delete())
            {
                throw new InvalidOperationException(
                    "Tekla не удалила временное тело '" + role + "'.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Ошибка удаления временного тела '" + role + "'.",
                exception);
        }
    }

    private static void TryDelete(ModelObject modelObject)
    {
        if (modelObject == null ||
            modelObject.Identifier == null ||
            modelObject.Identifier.ID == 0)
        {
            return;
        }
        try { modelObject.Delete(); }
        catch { }
    }

    internal static ControlLine CreateControlLine(
        FachwerkLowerRigelLayout layout,
        int componentId)
    {
        if (layout == null) throw new ArgumentNullException(nameof(layout));

        var line = new ControlLine(
            new LineSegment(
                new Point(layout.LeftTubeIntersection),
                new Point(layout.RightTubeIntersection)),
            false);
        Tag(line, "control-line", componentId);
        if (!line.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала контрольную ось нижнего ригеля.");
        }
        return line;
    }

    internal static BeamState CaptureBeam(Beam beam)
    {
        if (beam == null) throw new ArgumentNullException(nameof(beam));
        return new BeamState(
            new Point(beam.StartPoint),
            new Point(beam.EndPoint));
    }

    internal static void ApplyCorrectedJointPoint(
        Beam beam,
        BeamState state,
        Point jointPoint)
    {
        if (beam == null) throw new ArgumentNullException(nameof(beam));
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (jointPoint == null)
            throw new ArgumentNullException(nameof(jointPoint));

        BeamState corrected = BuildCorrectedBeamState(
            state,
            jointPoint);

        // Tekla exposes beam endpoints in the active work plane. Rewrite both
        // points from the global snapshot so Modify cannot move the far end.
        beam.StartPoint = new Point(corrected.Start);
        beam.EndPoint = new Point(corrected.End);
        if (!beam.Modify())
        {
            throw new InvalidOperationException(
                "Tekla не применила коррекцию оси трубы ID " +
                ObjectId(beam) + ".");
        }
    }

    internal static BeamState BuildCorrectedBeamState(
        BeamState state,
        Point jointPoint)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (jointPoint == null)
            throw new ArgumentNullException(nameof(jointPoint));

        bool correctStart =
            FachwerkLowerRigelNodeGeometry.Distance(
                state.Start,
                jointPoint) <=
            FachwerkLowerRigelNodeGeometry.Distance(
                state.End,
                jointPoint);

        return new BeamState(
            new Point(correctStart ? jointPoint : state.Start),
            new Point(correctStart ? state.End : jointPoint));
    }

    internal static void RestoreBeam(Beam beam, BeamState state)
    {
        if (beam == null || state == null) return;
        beam.StartPoint = new Point(state.Start);
        beam.EndPoint = new Point(state.End);
        beam.Modify();
    }

    internal static Point FarBeamEndpoint(
        BeamState state,
        Point jointPoint)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        return FachwerkLowerRigelNodeGeometry.Distance(
                   state.Start,
                   jointPoint) >=
               FachwerkLowerRigelNodeGeometry.Distance(
                   state.End,
                   jointPoint)
            ? new Point(state.Start)
            : new Point(state.End);
    }

    private static IReadOnlyList<Point> SamplePolycurve(Polycurve polycurve)
    {
        if (polycurve == null)
        {
            throw new ArgumentNullException(nameof(polycurve));
        }

        var result = new List<Point>();
        IEnumerator<ICurve> curves = polycurve.GetEnumerator();
        while (curves.MoveNext())
        {
            ICurve curve = curves.Current;
            if (curve is LineSegment line)
            {
                AppendDistinct(result, line.StartPoint);
                AppendDistinct(result, line.EndPoint);
                continue;
            }
            if (curve is Arc arc)
            {
                SampleArc(result, arc);
                continue;
            }

            AppendDistinct(result, curve.StartPoint);
            AppendDistinct(result, curve.EndPoint);
        }
        return result;
    }

    private static void SampleArc(
        ICollection<Point> result,
        Arc arc)
    {
        int segments = Math.Max(
            4,
            (int)Math.Ceiling(Math.Max(arc.Length, ArcChord) / ArcChord));
        Vector radial = FachwerkLowerRigelNodeGeometry.Normalize(
            arc.StartDirection,
            "радиус дуги оси стенки");
        Vector tangent = FachwerkLowerRigelNodeGeometry.Normalize(
            FachwerkLowerRigelNodeGeometry.Cross(arc.Normal, radial),
            "касательная дуги оси стенки");
        for (var index = 0; index <= segments; index++)
        {
            double angle = arc.Angle * index / segments;
            var point = new Point(
                arc.CenterPoint.X +
                arc.Radius *
                (radial.X * Math.Cos(angle) + tangent.X * Math.Sin(angle)),
                arc.CenterPoint.Y +
                arc.Radius *
                (radial.Y * Math.Cos(angle) + tangent.Y * Math.Sin(angle)),
                arc.CenterPoint.Z +
                arc.Radius *
                (radial.Z * Math.Cos(angle) + tangent.Z * Math.Sin(angle)));
            AppendDistinct(result, point);
        }
    }

    private static ContourPlate CreatePlate(
        IReadOnlyList<Point> boundary,
        string profile,
        string material,
        string className,
        string name,
        string role,
        int componentId)
    {
        if (boundary == null || boundary.Count < 3)
        {
            throw new InvalidOperationException(
                "Контур пластины '" + role +
                "' содержит меньше трех вершин.");
        }

        var plate = new ContourPlate
        {
            Name = name,
            Class = className,
        };
        plate.Profile.ProfileString = profile;
        plate.Material.MaterialString = string.IsNullOrWhiteSpace(material)
            ? NodeMaterial
            : material.Trim();
        plate.Position.Depth = Position.DepthEnum.MIDDLE;
        ApplyRegularNumbering(plate);
        foreach (Point point in boundary)
        {
            if (!plate.AddContourPoint(
                    new ContourPoint(new Point(point), new Chamfer())))
            {
                throw new InvalidOperationException(
                    "Tekla отклонила вершину пластины '" + role + "'.");
            }
        }
        Tag(plate, role, componentId);
        return plate;
    }

    private static void AlignDepthToPart(
        ContourPlate plate,
        IReadOnlyList<Point> boundary,
        Part alignmentPart,
        FachwerkLowerRigelPlane referencePlane)
    {
        Vector contourNormal = BoundaryNormal(boundary);
        double orientation = FachwerkLowerRigelNodeGeometry.Dot(
            contourNormal,
            referencePlane.Normal);
        if (Math.Abs(orientation) < 0.9)
        {
            throw new InvalidOperationException(
                "Контур переходной пластины не лежит в расчетной плоскости.");
        }

        double targetCenter = SolidCenterDistance(
            alignmentPart,
            referencePlane);
        plate.Position.Depth = Position.DepthEnum.MIDDLE;
        plate.Position.DepthOffset = targetCenter * Math.Sign(orientation);
    }

    private static void CorrectInsertedDepth(
        ContourPlate plate,
        IReadOnlyList<Point> boundary,
        Part alignmentPart,
        FachwerkLowerRigelPlane referencePlane)
    {
        const double tolerance = 0.05;
        double targetCenter = SolidCenterDistance(
            alignmentPart,
            referencePlane);
        double actualCenter = SolidCenterDistance(
            plate,
            referencePlane);
        double correction = targetCenter - actualCenter;
        if (Math.Abs(correction) <= tolerance)
        {
            return;
        }

        Vector contourNormal = BoundaryNormal(boundary);
        double orientation = Math.Sign(
            FachwerkLowerRigelNodeGeometry.Dot(
                contourNormal,
                referencePlane.Normal));
        plate.Position.DepthOffset += correction * orientation;
        if (!plate.Modify())
        {
            throw new InvalidOperationException(
                "Tekla не применила выравнивание по глубине для пластины '" +
                plate.Name + "'.");
        }
    }

    private static double SolidCenterDistance(
        Part part,
        FachwerkLowerRigelPlane referencePlane)
    {
        Solid solid = part.GetSolid();
        if (solid == null || !solid.IsValid())
        {
            throw new InvalidOperationException(
                "Не удалось получить Solid детали ID " +
                ObjectId(part) + " для выравнивания по глубине.");
        }

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        int count = 0;
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
                    double distance =
                        FachwerkLowerRigelNodeGeometry.SignedDistance(
                            vertices.Current,
                            referencePlane);
                    minimum = Math.Min(minimum, distance);
                    maximum = Math.Max(maximum, distance);
                    count++;
                }
            }
        }

        if (count == 0 ||
            double.IsInfinity(minimum) ||
            double.IsInfinity(maximum))
        {
            throw new InvalidOperationException(
                "Solid детали ID " + ObjectId(part) +
                " не содержит вершин для выравнивания по глубине.");
        }
        return (minimum + maximum) * 0.5;
    }

    private static Vector BoundaryNormal(IReadOnlyList<Point> boundary)
    {
        for (var firstIndex = 1;
             firstIndex < boundary.Count - 1;
             firstIndex++)
        {
            Vector first = FachwerkLowerRigelNodeGeometry.VectorBetween(
                boundary[0],
                boundary[firstIndex]);
            for (var secondIndex = firstIndex + 1;
                 secondIndex < boundary.Count;
                 secondIndex++)
            {
                Vector second = FachwerkLowerRigelNodeGeometry.VectorBetween(
                    boundary[0],
                    boundary[secondIndex]);
                Vector normal = FachwerkLowerRigelNodeGeometry.Cross(
                    first,
                    second);
                if (FachwerkLowerRigelNodeGeometry.Length(normal) > 1e-7)
                {
                    return FachwerkLowerRigelNodeGeometry.Normalize(
                        normal,
                        "нормаль контура переходной пластины");
                }
            }
        }

        throw new InvalidOperationException(
            "Не удалось определить нормаль контура переходной пластины.");
    }

    private static void CopyPosition(
        Position source,
        Position target)
    {
        target.Plane = source.Plane;
        target.PlaneOffset = source.PlaneOffset;
        target.Depth = source.Depth;
        target.DepthOffset = source.DepthOffset;
        target.Rotation = source.Rotation;
        target.RotationOffset = source.RotationOffset;
    }

    private static void ApplyRegularNumbering(Part target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        target.PartNumber = new NumberingSeries(RegularPartPrefix, 1);
        target.AssemblyNumber = new NumberingSeries(string.Empty, 1);
    }

    private static void AppendDistinct(
        ICollection<Point> points,
        Point point)
    {
        if (point == null) return;
        if (points is List<Point> list &&
            list.Count > 0 &&
            FachwerkLowerRigelNodeGeometry.Distance(
                list[list.Count - 1],
                point) <= PointTolerance)
        {
            return;
        }
        points.Add(new Point(point));
    }

    private static void Tag(
        ModelObject modelObject,
        string role,
        int componentId)
    {
        string label = StablePluginLabel(role);
        if (!modelObject.SetLabel(label))
        {
            throw new InvalidOperationException(
                "Tekla не зарегистрировала стабильную метку '" + label +
                "' для объекта '" + role + "'.");
        }
        TrySetUserProperty(modelObject, "FK_ROLE", role);
        TrySetUserProperty(
            modelObject,
            "FK_OWNER",
            componentId.ToString(CultureInfo.InvariantCulture));
    }

    internal static string StablePluginLabel(string role)
    {
        unchecked
        {
            uint hash = 2166136261;
            string value = role ?? string.Empty;
            for (var index = 0; index < value.Length; index++)
            {
                hash ^= value[index];
                hash *= 16777619;
            }
            return "FKLR_" + hash.ToString("X8", CultureInfo.InvariantCulture);
        }
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
            // Diagnostics are optional in environments without custom UDAs.
        }
    }

    private static string ObjectId(ModelObject modelObject)
    {
        return modelObject?.Identifier == null
            ? "0"
            : modelObject.Identifier.ID.ToString(
                CultureInfo.InvariantCulture);
    }
}

internal sealed class BeamState
{
    internal BeamState(Point start, Point end)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; }
    public Point End { get; }
}
