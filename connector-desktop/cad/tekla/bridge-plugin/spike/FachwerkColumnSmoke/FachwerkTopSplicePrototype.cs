using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace FachwerkColumnSmoke;

internal static class FachwerkTopSplicePrototype
{
    private const string ColumnPluginName = "FachwerkColumnPlugin";
    private const string NodePrefix = "FK_TOP_SPLICE:";
    private const string SourceComponentType = "FachwerkTopSpliceNode";
    private const double SourceOriginX = 36666.0;
    private const double AnchorToleranceMm = 1.5;

    private static readonly string[] ExpectedRoles =
    {
        "side-inner",
        "side-outer",
        "bridge-lower",
        "bridge-upper",
        "cap-inner",
        "cap-outer",
        "closure",
    };

    private static readonly IReadOnlyDictionary<string, string> ExpectedProfiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["side-inner"] = "PL25",
            ["side-outer"] = "PL25",
            ["bridge-lower"] = "PL50*300",
            ["bridge-upper"] = "PL50*300",
            ["cap-inner"] = "PL50*130",
            ["cap-outer"] = "PL50*130",
            ["closure"] = "PL6*155",
        };

    private static readonly IReadOnlyDictionary<string, int> ExpectedBooleanCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["side-inner"] = 1,
            ["side-outer"] = 1,
            ["bridge-lower"] = 3,
            ["bridge-upper"] = 3,
            ["cap-inner"] = 4,
            ["cap-outer"] = 4,
            ["closure"] = 0,
        };

    internal static TopSpliceUpsertResult Upsert(Model model, string requestedMark)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        var mark = RequireMark(requestedMark);
        var context = ResolveContext(model, mark);
        var previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
        var created = new List<ModelObject>();
        var pendingToken = Guid.NewGuid().ToString("N");
        var oldParts = FindNodeParts(model, mark).ToArray();

        try
        {
            var workPlane = new TransformationPlane(new CoordinateSystem(
                context.Origin,
                context.LocalAxisX,
                context.LocalAxisY));
            if (!model.GetWorkPlaneHandler().SetCurrentTransformationPlane(workPlane))
                throw new InvalidOperationException("Tekla did not accept the top-splice local work plane.");

            var parts = new Dictionary<string, Part>(StringComparer.Ordinal)
            {
                ["side-inner"] = CreateSidePlate(
                    context,
                    "side-inner",
                    36743.5,
                    Position.DepthEnum.FRONT,
                    pendingToken),
                ["side-outer"] = CreateSidePlate(
                    context,
                    "side-outer",
                    36588.5,
                    Position.DepthEnum.BEHIND,
                    pendingToken),
                ["bridge-lower"] = CreateBeam(
                    context,
                    "bridge-lower",
                    "PL50*300",
                    S(36516, -1420.747, 43352.047),
                    S(36516, -1137.868, 44486.944),
                    Position.DepthEnum.FRONT,
                    "C355-5",
                    pendingToken),
                ["bridge-upper"] = CreateBeam(
                    context,
                    "bridge-upper",
                    "PL50*300",
                    S(36516, -1132.176, 43352.047),
                    S(36516, -865.022, 44423.872),
                    Position.DepthEnum.BEHIND,
                    "C355-5",
                    pendingToken),
                ["cap-inner"] = CreateBeam(
                    context,
                    "cap-inner",
                    "PL50*130",
                    S(36601, -1126.959, 44530.685),
                    S(36601, -1059.253, 44802.241),
                    Position.DepthEnum.FRONT,
                    "C355-5",
                    pendingToken),
                ["cap-outer"] = CreateBeam(
                    context,
                    "cap-outer",
                    "PL50*130",
                    S(36601, -852.923, 44472.387),
                    S(36601, -785.185, 44744.069),
                    Position.DepthEnum.BEHIND,
                    "C355-5",
                    pendingToken),
                ["closure"] = CreateBeam(
                    context,
                    "closure",
                    "PL6*155",
                    S(36588.5, -1420.747, 43352.047),
                    S(36588.5, -1132.176, 43352.047),
                    Position.DepthEnum.BEHIND,
                    "C245-4",
                    pendingToken),
            };
            created.AddRange(parts.Values);

            var cuts = CreateCuts(parts);
            created.AddRange(cuts);
            AddToColumnAssembly(context.OuterFlange.GetAssembly(), parts.Values);
            var welds = CreateWelds(context, parts);
            created.AddRange(welds);

            foreach (var oldPart in oldParts)
            {
                if (!oldPart.Delete())
                    throw new InvalidOperationException("Tekla could not delete previous top-splice part " + oldPart.Identifier.ID + ".");
            }

            foreach (var pair in parts)
            {
                pair.Value.Name = StableName(mark, pair.Key);
                TrySetIdentity(pair.Value, mark, pair.Key);
                if (!pair.Value.Modify())
                    throw new InvalidOperationException("Tekla could not finalize top-splice role '" + pair.Key + "'.");
            }

            if (!model.CommitChanges())
                throw new InvalidOperationException("Tekla did not commit the top-splice node.");

            return new TopSpliceUpsertResult(
                mark,
                parts.Count,
                welds.Count,
                cuts.Count,
                oldParts.Length,
                context.MaxResidualMm);
        }
        catch
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            model.CommitChanges();
            throw;
        }
        finally
        {
            model.GetWorkPlaneHandler().SetCurrentTransformationPlane(previousPlane);
        }
    }

    internal static TopSpliceInspectionResult Inspect(Model model, string requestedMark)
    {
        var mark = RequireMark(requestedMark);
        var parts = FindNodeParts(model, mark).ToArray();
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var weldIds = new HashSet<int>();
        var booleanIds = new HashSet<int>();
        var validProfiles = true;
        var validBooleanDistribution = true;
        var validSolids = true;
        foreach (var part in parts)
        {
            var role = ReadRole(part, mark);
            roles.Add(role);
            validProfiles &= ExpectedProfiles.TryGetValue(role, out var expectedProfile) &&
                             string.Equals(
                                 part.Profile?.ProfileString,
                                 expectedProfile,
                                 StringComparison.OrdinalIgnoreCase);
            var welds = part.GetWelds();
            while (welds.MoveNext())
            {
                if (welds.Current is BaseWeld weld && weld.Identifier.ID != 0)
                    weldIds.Add(weld.Identifier.ID);
            }
            var booleans = part.GetBooleans();
            var roleBooleanCount = 0;
            while (booleans.MoveNext())
            {
                if (booleans.Current is BooleanPart booleanPart && booleanPart.Identifier.ID != 0)
                {
                    booleanIds.Add(booleanPart.Identifier.ID);
                    roleBooleanCount++;
                }
            }
            validBooleanDistribution &= ExpectedBooleanCounts.TryGetValue(role, out var expectedBooleanCount) &&
                                        roleBooleanCount == expectedBooleanCount;
            try
            {
                var solid = part.GetSolid();
                validSolids &= solid != null &&
                               solid.MaximumPoint != null &&
                               solid.MinimumPoint != null &&
                               Distance(solid.MaximumPoint, solid.MinimumPoint) > 1;
            }
            catch
            {
                validSolids = false;
            }
        }

        return new TopSpliceInspectionResult(
            mark,
            parts.Length,
            weldIds.Count,
            booleanIds.Count,
            roles.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            parts.Length == ExpectedRoles.Length &&
            ExpectedRoles.All(roles.Contains) &&
            booleanIds.Count == 16 &&
            weldIds.Count == 8 &&
            validProfiles &&
            validBooleanDistribution &&
            validSolids);
    }

    private static TopSpliceContext ResolveContext(Model model, string mark)
    {
        var components = model.GetModelObjectSelector()
            .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
        var matches = new List<Component>();
        while (components.MoveNext())
        {
            if (!(components.Current is Component component) ||
                !string.Equals(component.Name, ColumnPluginName, StringComparison.OrdinalIgnoreCase))
                continue;
            var candidateMark = string.Empty;
            component.GetAttribute("fk_mark", ref candidateMark);
            if (string.Equals(candidateMark?.Trim(), mark, StringComparison.OrdinalIgnoreCase))
                matches.Add(component);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException("Expected one Fachwerk column '" + mark + "', found " + matches.Count + ".");

        var roleParts = new Dictionary<string, List<Part>>(StringComparer.Ordinal);
        var children = matches[0].GetChildren();
        while (children.MoveNext())
        {
            if (!(children.Current is Part part)) continue;
            var role = string.Empty;
            part.GetUserProperty("FK_ROLE", ref role);
            if (string.IsNullOrWhiteSpace(role)) continue;
            if (!roleParts.TryGetValue(role, out var values))
            {
                values = new List<Part>();
                roleParts.Add(role, values);
            }
            values.Add(part);
        }

        var outerWeb = ClosestPart(roleParts, "outer-web", 44596.138);
        var innerWeb = ClosestPart(roleParts, "inner-web", 44596.138);
        var outerFlange = ClosestPart(roleParts, "outer-flange", 44756.237);
        var innerFlange = ClosestPart(roleParts, "inner-flange", 44436.04);

        var targetOuter0 = PointAtElevation(outerWeb, 44596.138);
        var targetOuter1 = PointAtElevation(outerWeb, 44650.571);
        var targetInner0 = PointAtElevation(innerWeb, 44596.138);
        var axisY = UnitXY(
            targetOuter1.X - targetOuter0.X,
            targetOuter1.Y - targetOuter0.Y,
            "facade tangent");
        if (-218.316 < 0) axisY = new Vector(-axisY.X, -axisY.Y, 0);

        var sourceAcross = UnitXY(
            targetInner0.X - targetOuter0.X,
            targetInner0.Y - targetOuter0.Y,
            "column width");
        var localAxisX = new Vector(-sourceAcross.X, -sourceAcross.Y, 0);
        var localAxisY = new Vector(axisY.X, axisY.Y, 0);
        var origin = new Point(
            targetOuter0.X - sourceAcross.X * (36588.5 - SourceOriginX) - axisY.X * -770.566,
            targetOuter0.Y - sourceAcross.Y * (36588.5 - SourceOriginX) - axisY.Y * -770.566,
            0);

        var checks = new[]
        {
            Distance(Transform(origin, sourceAcross, axisY, S(36588.5, -770.566, 44596.138)), targetOuter0),
            Distance(Transform(origin, sourceAcross, axisY, S(36588.5, -988.882, 44650.571)), targetOuter1),
            Distance(Transform(origin, sourceAcross, axisY, S(36743.5, -770.566, 44596.138)), targetInner0),
            Distance(
                Transform(origin, sourceAcross, axisY, S(36666, -730.648, 44756.237)),
                PointAtElevation(outerFlange, 44756.237)),
            Distance(
                Transform(origin, sourceAcross, axisY, S(36666, -810.482, 44436.04)),
                PointAtElevation(innerFlange, 44436.04)),
        };
        var maxResidual = checks.Max();
        if (maxResidual > AnchorToleranceMm)
            throw new InvalidOperationException(
                "Top-splice frame residual " + maxResidual.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm exceeds " + AnchorToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm.");

        return new TopSpliceContext(
            mark,
            origin,
            localAxisX,
            localAxisY,
            sourceAcross,
            axisY,
            outerFlange,
            innerFlange,
            maxResidual);
    }

    private static ContourPlate CreateSidePlate(
        TopSpliceContext context,
        string role,
        double sourceX,
        Position.DepthEnum depth,
        string pendingToken)
    {
        var plate = new ContourPlate
        {
            Name = PendingName(context.Mark, role, pendingToken),
            Class = "20",
        };
        plate.Profile.ProfileString = "PL25";
        plate.Material.MaterialString = "C355-5";
        plate.Position.Depth = depth;
        foreach (var point in new[]
        {
            S(sourceX, -1420.747, 43352.047),
            S(sourceX, -1132.176, 43352.047),
            S(sourceX, -865.022, 44423.872),
            S(sourceX, -1034.532, 44466.136),
            S(sourceX, -1091.323, 44478.854),
            S(sourceX, -1137.868, 44486.944),
        })
        {
            if (!plate.AddContourPoint(new ContourPoint(ToLocal(point), new Chamfer())))
                throw new InvalidOperationException("Tekla rejected contour point for '" + role + "'.");
        }
        if (!plate.Insert())
            throw new InvalidOperationException("Tekla could not insert top-splice role '" + role + "'.");
        return plate;
    }

    private static Beam CreateBeam(
        TopSpliceContext context,
        string role,
        string profile,
        SourcePoint start,
        SourcePoint end,
        Position.DepthEnum depth,
        string material,
        string pendingToken)
    {
        var beam = new Beam(ToLocal(start), ToLocal(end))
        {
            Name = PendingName(context.Mark, role, pendingToken),
            Class = "20",
        };
        beam.Profile.ProfileString = profile;
        beam.Material.MaterialString = material;
        beam.Position.Plane = Position.PlaneEnum.LEFT;
        beam.Position.Rotation = Position.RotationEnum.FRONT;
        beam.Position.Depth = depth;
        if (!beam.Insert())
            throw new InvalidOperationException("Tekla could not insert top-splice role '" + role + "'.");
        return beam;
    }

    private static List<BaseWeld> CreateWelds(
        TopSpliceContext context,
        IReadOnlyDictionary<string, Part> parts)
    {
        var welds = new List<BaseWeld>
        {
            CreateWeld(parts["bridge-lower"], parts["side-inner"], 23, true),
            CreateWeld(parts["bridge-lower"], parts["side-outer"], 23, true),
            CreateWeld(parts["bridge-upper"], parts["side-inner"], 23, true),
            CreateWeld(parts["bridge-upper"], parts["side-outer"], 23, true),
            CreateWeld(context.OuterFlange, parts["cap-inner"], 48, true),
            CreateWeld(context.OuterFlange, parts["cap-outer"], 48, true),
            CreateWeld(parts["side-inner"], parts["closure"], 6, false),
            CreateWeld(context.OuterFlange, parts["side-outer"], 6, false),
        };
        return welds;
    }

    private static List<BooleanPart> CreateCuts(IReadOnlyDictionary<string, Part> parts)
    {
        var cuts = new List<BooleanPart>
        {
            CreatePolyBeamCut(
                parts["side-inner"],
                "TRI_A36-30",
                new[]
                {
                    S(36731, -1142.794, 44487.8),
                    S(36731, -1091.323, 44478.855),
                    S(36731, -1034.532, 44466.136),
                    S(36731, -865.022, 44423.872),
                },
                Position.PlaneEnum.LEFT,
                2,
                Position.RotationEnum.BACK,
                Position.DepthEnum.BEHIND),
            CreatePolyBeamCut(
                parts["side-outer"],
                "TRI_A30-36",
                new[]
                {
                    S(36601, -1142.794, 44487.8),
                    S(36601, -1091.323, 44478.855),
                    S(36601, -1034.532, 44466.136),
                    S(36601, -865.022, 44423.872),
                },
                Position.PlaneEnum.RIGHT,
                2,
                Position.RotationEnum.TOP,
                Position.DepthEnum.BEHIND),

            CreateContourCut(parts["bridge-lower"], "BL56", 25,
                S(36576, -1162.126, 44492.99),
                S(36576, -1304.726, 43920.882),
                S(36516, -1304.726, 43920.882),
                S(36516, -1162.126, 44492.99)),
            CreateContourCut(parts["bridge-lower"], "BL56", 25,
                S(36816, -1162.126, 44492.99),
                S(36816, -1304.726, 43920.882),
                S(36756, -1304.726, 43920.882),
                S(36756, -1162.126, 44492.99)),
            CreateContourCut(parts["bridge-lower"], "BL185", 0,
                S(36666, -1139.81, 44487.424),
                S(36666, -1186.384, 44499.036),
                S(36666, -1197.993, 44452.459)),

            CreateContourCut(parts["bridge-upper"], "BL56", 25,
                S(36816, -840.764, 44417.825),
                S(36816, -965.288, 43918.231),
                S(36756, -965.288, 43918.231),
                S(36756, -840.764, 44417.825)),
            CreateContourCut(parts["bridge-upper"], "BL56", 25,
                S(36576, -840.764, 44417.825),
                S(36576, -965.288, 43918.231),
                S(36516, -965.288, 43918.231),
                S(36516, -840.764, 44417.825)),
            CreateContourCut(parts["bridge-upper"], "BL320", 0,
                S(36666, -863.081, 44423.385),
                S(36666, -816.506, 44411.779),
                S(36666, -826.248, 44372.695)),

            CreateContourCut(parts["cap-inner"], "BL56", -25,
                S(36601, -1083.51, 44808.289),
                S(36601, -1088.348, 44788.883),
                S(36621, -1083.51, 44808.289)),
            CreateContourCut(parts["cap-inner"], "BL56", -25,
                S(36711, -1083.51, 44808.289),
                S(36731, -1088.348, 44788.883),
                S(36731, -1083.51, 44808.289)),
            CreateContourCut(parts["cap-inner"], "BL56", -25,
                S(36601, -1146.378, 44556.139),
                S(36601, -1151.217, 44536.733),
                S(36621, -1151.217, 44536.733)),
            CreateContourCut(parts["cap-inner"], "BL56", -25,
                S(36711, -1151.217, 44536.733),
                S(36731, -1151.217, 44536.733),
                S(36731, -1146.378, 44556.139)),

            CreateContourCut(parts["cap-outer"], "BL56", -25,
                S(36601, -760.959, 44737.895),
                S(36601, -765.797, 44718.489),
                S(36621, -760.959, 44737.895)),
            CreateContourCut(parts["cap-outer"], "BL56", -25,
                S(36711, -760.959, 44737.895),
                S(36731, -765.797, 44718.489),
                S(36731, -760.959, 44737.895)),
            CreateContourCut(parts["cap-outer"], "BL56", -25,
                S(36601, -823.827, 44485.745),
                S(36601, -828.665, 44466.339),
                S(36621, -828.665, 44466.339)),
            CreateContourCut(parts["cap-outer"], "BL56", -25,
                S(36711, -828.665, 44466.339),
                S(36731, -828.665, 44466.339),
                S(36731, -823.827, 44485.745)),
        };
        return cuts;
    }

    private static BooleanPart CreatePolyBeamCut(
        Part father,
        string profile,
        IReadOnlyList<SourcePoint> points,
        Position.PlaneEnum plane,
        double planeOffset,
        Position.RotationEnum rotation,
        Position.DepthEnum depth)
    {
        var cutter = new PolyBeam(PolyBeam.PolyBeamTypeEnum.BEAM)
        {
            Name = "FK_TOP_SPLICE_CUTTER",
            Class = BooleanPart.BooleanOperativeClassName,
        };
        cutter.Profile.ProfileString = profile;
        cutter.Material.MaterialString = "C245";
        cutter.Position.Plane = plane;
        cutter.Position.PlaneOffset = planeOffset;
        cutter.Position.Rotation = rotation;
        cutter.Position.Depth = depth;
        foreach (var point in points)
        {
            if (!cutter.AddContourPoint(new ContourPoint(ToLocal(point), new Chamfer())))
                throw new InvalidOperationException("Tekla rejected a top-splice profile cutter point.");
        }
        return InsertBooleanCut(father, cutter);
    }

    private static BooleanPart CreateContourCut(
        Part father,
        string profile,
        double depthOffset,
        params SourcePoint[] points)
    {
        var cutter = new ContourPlate
        {
            Name = "FK_TOP_SPLICE_CUTTER",
            Class = BooleanPart.BooleanOperativeClassName,
        };
        cutter.Profile.ProfileString = profile;
        cutter.Material.MaterialString = "ANTIMATERIAL";
        cutter.Position.Depth = Position.DepthEnum.MIDDLE;
        cutter.Position.DepthOffset = depthOffset;
        foreach (var point in points)
        {
            if (!cutter.AddContourPoint(new ContourPoint(ToLocal(point), new Chamfer())))
                throw new InvalidOperationException("Tekla rejected a top-splice contour cutter point.");
        }
        return InsertBooleanCut(father, cutter);
    }

    private static BooleanPart InsertBooleanCut(Part father, Part cutter)
    {
        BooleanPart? booleanPart = null;
        try
        {
            if (!cutter.Insert())
                throw new InvalidOperationException("Tekla could not insert a top-splice operative part.");
            booleanPart = new BooleanPart
            {
                Father = father,
                Type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
            };
            booleanPart.SetOperativePart(cutter);
            if (!booleanPart.Insert())
                throw new InvalidOperationException("Tekla could not insert a top-splice BooleanPart.");
            try { cutter.Delete(); }
            catch { }
            return booleanPart;
        }
        catch
        {
            if (booleanPart != null && booleanPart.Identifier.ID != 0)
            {
                try { booleanPart.Delete(); }
                catch { }
            }
            if (cutter.Identifier.ID != 0)
            {
                try { cutter.Delete(); }
                catch { }
            }
            throw;
        }
    }

    private static BaseWeld CreateWeld(Part main, Part secondary, double size, bool fullPenetration)
    {
        var weld = new Weld
        {
            MainObject = main,
            SecondaryObject = secondary,
            SizeAbove = size,
            TypeAbove = fullPenetration
                ? BaseWeld.WeldTypeEnum.WELD_TYPE_BEVEL_GROOVE_SINGLE_BEVEL_BUTT
                : BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET,
            TypeBelow = BaseWeld.WeldTypeEnum.WELD_TYPE_NONE,
            Preparation = fullPenetration
                ? BaseWeld.WeldPreparationTypeEnum.PREPARATION_SECONDARY
                : BaseWeld.WeldPreparationTypeEnum.PREPARATION_NONE,
            ShopWeld = true,
        };
        if (!weld.Insert())
            throw new InvalidOperationException("Tekla could not insert weld between node parts.");
        return weld;
    }

    private static void AddToColumnAssembly(Assembly assembly, IEnumerable<Part> parts)
    {
        if (assembly == null)
            throw new InvalidOperationException("Tekla did not return the column assembly.");
        foreach (var part in parts)
        {
            if (!assembly.Add(part))
                throw new InvalidOperationException("Tekla could not add top-splice part to column assembly.");
        }
        if (!assembly.Modify())
            throw new InvalidOperationException("Tekla could not update the column assembly.");
    }

    private static Part ClosestPart(
        IReadOnlyDictionary<string, List<Part>> roleParts,
        string role,
        double elevation)
    {
        if (!roleParts.TryGetValue(role, out var values) || values.Count == 0)
            throw new InvalidOperationException("Column has no semantic role '" + role + "'.");
        return values
            .OrderBy(part => Math.Abs(PointAtElevation(part, elevation).Z - elevation))
            .ThenBy(part => MinDistanceToElevation(part, elevation))
            .First();
    }

    private static Point PointAtElevation(Part part, double elevation)
    {
        var points = part.GetCenterLine(false).OfType<Point>().ToArray();
        if (points.Length == 0)
            throw new InvalidOperationException("Part " + part.Identifier.ID + " has no centerline points.");
        return points.OrderBy(point => Math.Abs(point.Z - elevation)).First();
    }

    private static double MinDistanceToElevation(Part part, double elevation) =>
        Math.Abs(PointAtElevation(part, elevation).Z - elevation);

    private static Vector UnitXY(double x, double y, string label)
    {
        var length = Math.Sqrt(x * x + y * y);
        if (length < 1e-6)
            throw new InvalidOperationException("Cannot resolve " + label + ".");
        return new Vector(x / length, y / length, 0);
    }

    private static Point Transform(
        Point origin,
        Vector sourceAcross,
        Vector sourceAlong,
        SourcePoint source) =>
        new(
            origin.X + sourceAcross.X * (source.X - SourceOriginX) + sourceAlong.X * source.Y,
            origin.Y + sourceAcross.Y * (source.X - SourceOriginX) + sourceAlong.Y * source.Y,
            source.Z);

    private static Point ToLocal(SourcePoint source) =>
        new(SourceOriginX - source.X, source.Y, source.Z);

    private static double Distance(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static IEnumerable<Part> FindNodeParts(Model model, string mark)
    {
        var namePrefix = NodePrefix + mark + ":";
        var externalIdPrefix = ExternalId(mark, string.Empty);
        var all = model.GetModelObjectSelector().GetAllObjects();
        while (all.MoveNext())
        {
            if (!(all.Current is Part part)) continue;
            var externalId = string.Empty;
            try { part.GetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", ref externalId); }
            catch { }
            if ((externalId ?? string.Empty).StartsWith(externalIdPrefix, StringComparison.Ordinal) ||
                (part.Name ?? string.Empty).StartsWith(namePrefix, StringComparison.Ordinal))
            {
                yield return part;
            }
        }
    }

    private static string StableName(string mark, string role) => NodePrefix + mark + ":" + role;

    private static string PendingName(string mark, string role, string token) =>
        StableName(mark, role) + ":pending:" + token;

    private static string ReadRole(Part part, string mark)
    {
        var externalId = string.Empty;
        try { part.GetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", ref externalId); }
        catch { }
        var externalPrefix = ExternalId(mark, string.Empty);
        var storedExternalId = externalId ?? string.Empty;
        if (storedExternalId.StartsWith(externalPrefix, StringComparison.Ordinal))
            return storedExternalId.Substring(externalPrefix.Length);

        var value = part.Name ?? string.Empty;
        var pending = value.IndexOf(":pending:", StringComparison.Ordinal);
        if (pending >= 0) value = value.Substring(0, pending);
        var separator = value.LastIndexOf(':');
        return separator >= 0 ? value.Substring(separator + 1) : value;
    }

    private static void TrySetIdentity(ModelObject modelObject, string mark, string role)
    {
        TrySetUserProperty(modelObject, "STRUCTURA_EXTERNAL_OBJECT_ID", ExternalId(mark, role));
        TrySetUserProperty(modelObject, "STRUCTURA_COMPONENT_TYPE", SourceComponentType);
        TrySetUserProperty(modelObject, "FK_NODE_ROLE", role);
    }

    private static string ExternalId(string mark, string role) =>
        "fachwerk-top-splice:" + mark + ":" + role;

    private static void TrySetUserProperty(ModelObject modelObject, string name, string value)
    {
        try { modelObject.SetUserProperty(name, value); }
        catch { }
    }

    private static string RequireMark(string requestedMark)
    {
        var mark = (requestedMark ?? string.Empty).Trim();
        if (mark.Length == 0) throw new ArgumentException("Column mark is required.", nameof(requestedMark));
        return mark;
    }

    private static SourcePoint S(double x, double y, double z) => new(x, y, z);

    private sealed class TopSpliceContext
    {
        internal TopSpliceContext(
            string mark,
            Point origin,
            Vector localAxisX,
            Vector localAxisY,
            Vector sourceAcross,
            Vector sourceAlong,
            Part outerFlange,
            Part innerFlange,
            double maxResidualMm)
        {
            Mark = mark;
            Origin = origin;
            LocalAxisX = localAxisX;
            LocalAxisY = localAxisY;
            SourceAcross = sourceAcross;
            SourceAlong = sourceAlong;
            OuterFlange = outerFlange;
            InnerFlange = innerFlange;
            MaxResidualMm = maxResidualMm;
        }

        internal string Mark { get; }
        internal Point Origin { get; }
        internal Vector LocalAxisX { get; }
        internal Vector LocalAxisY { get; }
        internal Vector SourceAcross { get; }
        internal Vector SourceAlong { get; }
        internal Part OuterFlange { get; }
        internal Part InnerFlange { get; }
        internal double MaxResidualMm { get; }
    }

    private readonly struct SourcePoint
    {
        internal SourcePoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal double X { get; }
        internal double Y { get; }
        internal double Z { get; }
    }
}

internal sealed class TopSpliceUpsertResult
{
    internal TopSpliceUpsertResult(
        string mark,
        int partCount,
        int weldCount,
        int booleanCount,
        int removedPartCount,
        double maxFrameResidualMm)
    {
        Mark = mark;
        PartCount = partCount;
        WeldCount = weldCount;
        BooleanCount = booleanCount;
        RemovedPartCount = removedPartCount;
        MaxFrameResidualMm = maxFrameResidualMm;
    }

    internal string Mark { get; }
    internal int PartCount { get; }
    internal int WeldCount { get; }
    internal int BooleanCount { get; }
    internal int RemovedPartCount { get; }
    internal double MaxFrameResidualMm { get; }
}

internal sealed class TopSpliceInspectionResult
{
    internal TopSpliceInspectionResult(
        string mark,
        int partCount,
        int weldCount,
        int booleanCount,
        IReadOnlyList<string> roles,
        bool isComplete)
    {
        Mark = mark;
        PartCount = partCount;
        WeldCount = weldCount;
        BooleanCount = booleanCount;
        Roles = roles;
        IsComplete = isComplete;
    }

    internal string Mark { get; }
    internal int PartCount { get; }
    internal int WeldCount { get; }
    internal int BooleanCount { get; }
    internal IReadOnlyList<string> Roles { get; }
    internal bool IsComplete { get; }
}
