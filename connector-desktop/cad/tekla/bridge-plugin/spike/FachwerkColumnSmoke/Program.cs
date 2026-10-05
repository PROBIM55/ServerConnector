using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Solid;
using Structura.Tekla.Fachwerk;

namespace FachwerkColumnSmoke;

internal static class Program
{
    private const string PluginName = "FachwerkColumnPlugin";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "catalog-check", StringComparison.OrdinalIgnoreCase))
                return CheckCatalog(args.Length > 1 ? args[1] : null);
            if (args.Length > 2 && string.Equals(args[0], "break-plan-check", StringComparison.OrdinalIgnoreCase))
                return CheckBreakPlan(args[1], args[2]);
            if (args.Length > 0 && string.Equals(args[0], "lower-rigel-node-check", StringComparison.OrdinalIgnoreCase))
                return CheckLowerRigelNodeGeometry();
            if (args.Length > 0 && string.Equals(args[0], "rigel-insert-check", StringComparison.OrdinalIgnoreCase))
            {
                CheckRigelInsertConnectionContract();
                Console.WriteLine("RIGEL_INSERT_CHECK_OK");
                return 0;
            }
            if (args.Length > 0 && string.Equals(args[0], "form-value-check", StringComparison.OrdinalIgnoreCase))
            {
                CheckFormValueContract();
                Console.WriteLine("FORM_VALUE_CHECK_OK");
                return 0;
            }

            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                Console.Error.WriteLine("NO_TEKLA_CONNECTION");
                return 10;
            }

            if (args.Length > 0 && string.Equals(args[0], "inspect-selected", StringComparison.OrdinalIgnoreCase))
                return Inspect(new Tekla.Structures.Model.UI.ModelObjectSelector().GetSelectedObjects(), model);
            if (args.Length > 0 && string.Equals(args[0], "inspect-selected-node", StringComparison.OrdinalIgnoreCase))
                return InspectSelectedNode(model, includeOperativeGeometry: false);
            if (args.Length > 0 && string.Equals(args[0], "inspect-selected-node-operatives", StringComparison.OrdinalIgnoreCase))
                return InspectSelectedNode(model, includeOperativeGeometry: true);
            if (args.Length > 1 && string.Equals(args[0], "inspect-boolean-operative", StringComparison.OrdinalIgnoreCase))
                return InspectBooleanOperative(model, args[1]);
            if (args.Length > 1 && string.Equals(args[0], "upsert-top-splice-node", StringComparison.OrdinalIgnoreCase))
                return UpsertTopSpliceNode(model, args[1]);
            if (args.Length > 1 && string.Equals(args[0], "inspect-top-splice-node", StringComparison.OrdinalIgnoreCase))
                return InspectTopSpliceNode(model, args[1]);
            if (args.Length > 0 && string.Equals(args[0], "inspect-fachwerk", StringComparison.OrdinalIgnoreCase))
                return InspectFachwerk(model);
            if (args.Length > 0 && string.Equals(args[0], "inspect-fachwerk-components", StringComparison.OrdinalIgnoreCase))
                return InspectFachwerkComponents(model);
            if (args.Length > 1 && string.Equals(args[0], "inspect-fachwerk-column", StringComparison.OrdinalIgnoreCase))
                return InspectFachwerkColumn(model, args[1]);
            if (args.Length > 1 && string.Equals(args[0], "inspect-fachwerk-bevels", StringComparison.OrdinalIgnoreCase))
                return InspectFachwerkBevels(model, args[1]);
            if (args.Length > 0 && string.Equals(args[0], "inspect-fachwerk-rigel-parts", StringComparison.OrdinalIgnoreCase))
                return InspectFachwerkRigelParts(model);
            if (args.Length > 1 && string.Equals(args[0], "inspect-profile", StringComparison.OrdinalIgnoreCase))
                return InspectProfile(model, args[1]);
            if (args.Length > 1 && string.Equals(args[0], "inspect-component", StringComparison.OrdinalIgnoreCase))
                return InspectComponent(model, args[1]);
            if (args.Length > 0 && string.Equals(args[0], "tekla-part-check", StringComparison.OrdinalIgnoreCase))
                return CheckTeklaPartCreation(model, args.Length > 1 ? args[1] : null);
            if (args.Length > 2 && string.Equals(args[0], "tekla-batch-part-check", StringComparison.OrdinalIgnoreCase))
                return CheckTeklaBatchPartCreation(model, args[1], args[2]);

            var offset = DateTime.UtcNow.Ticks % 100000;
            var insertion = new Point(200000 + offset, -200000, 43330);
            var direction = new Point(insertion.X + 1000, insertion.Y, insertion.Z);
            var component = new Component
            {
                Name = PluginName,
                Number = BaseComponent.PLUGIN_OBJECT_NUMBER,
            };
            var input = new ComponentInput();
            input.AddTwoInputPositions(insertion, direction);
            component.SetComponentInput(input);
            component.SetAttribute("fk_profile_key", "СФ1");
            component.SetAttribute("fk_mark", "SMOKE_СФ1");
            component.SetAttribute("fk_material", "C355-5");
            component.SetAttribute("fk_class", "3");
            component.SetAttribute("fk_rotation_deg", 0.0);
            component.SetAttribute("fk_bevel_profile", string.Empty);

            if (!component.Insert())
            {
                Console.Error.WriteLine("INSERT_FALSE");
                return 20;
            }
            if (!model.CommitChanges())
            {
                Console.Error.WriteLine("COMMIT_FALSE");
                return 21;
            }

            var childCount = 0;
            var children = component.GetChildren();
            while (children.MoveNext())
            {
                if (children.Current is ModelObject) childCount++;
            }

            Console.WriteLine($"INSERT_OK component={component.Identifier.ID} children={childCount} insertion={insertion.X:0.###},{insertion.Y:0.###},{insertion.Z:0.###}");
            return childCount > 0 ? 0 : 22;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 99;
        }
    }

    private static int CheckLowerRigelNodeGeometry()
    {
        RequireNear(
            InvokeLowerRigelNodePrivate<double>(
                "Positive",
                (double)int.MinValue,
                25.0,
                "Зазор"),
            25,
            "Tekla numeric sentinel fallback");
        Require(
            InvokeLowerRigelNodePrivate<string>(
                "Normalize",
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                "PL25") == "PL25",
            "Tekla string sentinel fallback");
        Require(
            InvokeLowerRigelNodePrivate<bool>(
                "Enabled",
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                true),
            "Tekla boolean sentinel fallback");
        var lowerNodeDefaults = new FachwerkLowerRigelNodePluginData();
        Require(
            lowerNodeDefaults.AxisCorrectionEnabled == "NO",
            "lower-rigel tube correction default");
        Require(
            lowerNodeDefaults.AutomaticLengthEnabled == "NO",
            "lower-rigel automatic length default");
        Require(
            lowerNodeDefaults.BottomClosureEnabled == "NO",
            "lower-rigel bottom closure default");
        Require(
            FachwerkLowerRigelNodeTeklaAdapter.BottomClosureProfile == "PL6" &&
            FachwerkLowerRigelNodeTeklaAdapter.BottomClosureMaterial == "C245-4" &&
            FachwerkLowerRigelNodeTeklaAdapter.RegularPartPrefix == "515-60.",
            "lower-rigel bottom closure attributes");

        var leftTube = new[]
        {
            new Point(-1000, 0, 0),
            new Point(-250, 0, 0),
        };
        var rightTube = new[]
        {
            new Point(250, 0, 0),
            new Point(1000, 0, 0),
        };
        var leftWeb = new[]
        {
            new Point(-250, 0, 0),
            new Point(-250, 0, -600),
        };
        var rightWeb = new[]
        {
            new Point(250, 0, 0),
            new Point(250, 0, -300),
        };

        FachwerkLowerRigelLayout layout =
            FachwerkLowerRigelNodeGeometry.BuildLayout(
                leftTube,
                rightTube,
                leftWeb,
                rightWeb,
                25,
                530,
                350,
                500,
                200);

        RequireNear(layout.LeftOuterPlane.Origin.X, -262.5, "left outer plane");
        RequireNear(layout.LeftInnerPlane.Origin.X, -237.5, "left inner plane");
        RequireNear(layout.RightInnerPlane.Origin.X, 237.5, "right inner plane");
        RequireNear(layout.RightOuterPlane.Origin.X, 262.5, "right outer plane");
        RequirePointNear(
            layout.LeftCentralTubePoint,
            layout.LeftTubeIntersection,
            "left central tube endpoint comes from source tube intersection");
        RequirePointNear(
            layout.RightCentralTubePoint,
            layout.RightTubeIntersection,
            "right central tube endpoint comes from source tube intersection");
        FachwerkLowerRigelLayout extendedSourceLayout =
            FachwerkLowerRigelNodeGeometry.BuildLayout(
                new[]
                {
                    new Point(-1000, 0, 0),
                    new Point(-300, 0, 0),
                },
                new[]
                {
                    new Point(300, 0, 0),
                    new Point(1000, 0, 0),
                },
                leftWeb,
                rightWeb,
                25,
                530,
                350,
                500,
                200);
        RequirePointNear(
            extendedSourceLayout.LeftTubeIntersection,
            new Point(-262.5, 0, 0),
            "left source tube may extend to the joint plane");
        RequirePointNear(
            extendedSourceLayout.RightTubeIntersection,
            new Point(262.5, 0, 0),
            "right source tube may extend to the joint plane");
        var centralKeepPoint = new Point(0, 0, 0);
        Require(
            FachwerkLowerRigelNodeGeometry.SignedDistance(
                centralKeepPoint,
                layout.LeftInnerPlane.OrientedAwayFrom(centralKeepPoint)) < 0,
            "left central tube cut must keep the middle span");
        Require(
            FachwerkLowerRigelNodeGeometry.SignedDistance(
                centralKeepPoint,
                layout.RightInnerPlane.OrientedAwayFrom(centralKeepPoint)) < 0,
            "right central tube cut must keep the middle span");
        RequireNear(layout.LeftTransition.FarPlane.Origin.Z, -500, "left transition length");
        RequireNear(layout.RightTransition.FarPlane.Origin.Z, -200, "right transition length");
        RequireNear(
            layout.LeftTransition.ReferencePlane.Origin.X,
            -262.5,
            "left transition reference plane");
        RequireNear(
            layout.RightTransition.ReferencePlane.Origin.X,
            262.5,
            "right transition reference plane");
        Require(
            layout.LeftTransition.Boundary.All(
                point => Math.Abs(point.X + 262.5) <= 1e-3),
            "left transition handles moved out of their reference plane");
        Require(
            layout.RightTransition.Boundary.All(
                point => Math.Abs(point.X - 262.5) <= 1e-3),
            "right transition handles moved out of their reference plane");
        RequireNear(
            layout.LeftTransition.InwardDirection.X,
            1,
            "left transition inward direction");
        RequireNear(
            layout.RightTransition.InwardDirection.X,
            -1,
            "right transition inward direction");
        Require(layout.LeftTransition.Boundary.Count >= 4, "left transition contour is empty");
        Require(layout.RightTransition.Boundary.Count >= 4, "right transition contour is empty");
        Require(layout.LeftEndPlateBoundary.Count >= 12, "left end plate contour is empty");
        Require(layout.RightEndPlateBoundary.Count >= 12, "right end plate contour is empty");

        double leftEndRadius = layout.LeftEndPlateBoundary.Max(
            point => Math.Sqrt(point.Y * point.Y + point.Z * point.Z));
        RequireNear(leftEndRadius, 285, "left end plate overbuild radius");

        double leftWidth =
            layout.LeftTransition.Boundary
                .Max(point => point.Y) -
            layout.LeftTransition.Boundary
                .Min(point => point.Y);
        double rightWidth =
            layout.RightTransition.Boundary
                .Max(point => point.Y) -
            layout.RightTransition.Boundary
                .Min(point => point.Y);
        RequireNear(leftWidth, 350, "left transition width");
        RequireNear(rightWidth, 350, "right transition width");

        FachwerkLowerRigelLayout montageSource =
            FachwerkLowerRigelNodeGeometry.BuildLayout(
                leftTube,
                rightTube,
                leftWeb,
                new[]
                {
                    new Point(250, 0, 0),
                    new Point(250, 0, -600),
                },
                25,
                530,
                350,
                500,
                500);
        FachwerkLowerRigelMontageLayout montage =
            FachwerkLowerRigelNodeGeometry.BuildMontageLayout(
                montageSource);
        FachwerkLowerRigelBottomClosure rectangularClosure =
            FachwerkLowerRigelNodeGeometry.BuildBottomClosure(
                montageSource);
        Require(
            rectangularClosure != null &&
            rectangularClosure.Boundary.Count == 4 &&
            !rectangularClosure.IsTriangular,
            "both protruding sides require a four-point closure");

        FachwerkLowerRigelBottomClosure triangularClosure =
            FachwerkLowerRigelNodeGeometry.BuildBottomClosure(
                new Point(-250, -175, -500),
                new Point(250, -175, -500),
                new Point(250, 175, -500),
                new Point(-250, 175, -500),
                true,
                false,
                new Vector(0, 0, -1));
        Require(
            triangularClosure != null &&
            triangularClosure.Boundary.Count == 3 &&
            triangularClosure.IsTriangular,
            "one protruding side requires a triangular closure");
        RequirePointNear(
            triangularClosure.Boundary[2],
            new Point(0, 175, -500),
            "triangular closure apex");
        Require(
            FachwerkLowerRigelNodeGeometry.BuildBottomClosure(
                new Point(-250, -175, -100),
                new Point(250, -175, -100),
                new Point(250, 175, -100),
                new Point(-250, 175, -100),
                false,
                false,
                new Vector(0, 0, -1)) == null,
            "non-protruding transition sides must not create a closure");
        RequireNear(
            SignedAlong(
                montage.LeftUpperWebPlane.Origin,
                montage.ReferencePlane),
            -30,
            "upper web erection offset");
        RequireNear(
            SignedAlong(
                montage.LowerFlangePlane.Origin,
                montage.ReferencePlane),
            -30,
            "lower flange erection offset");
        RequireNear(
            SignedAlong(
                montage.UpperFlangePlane.Origin,
                montage.ReferencePlane),
            120,
            "upper flange erection offset");
        RequireNear(
            SignedAlong(
                montage.InsertEndPlane.Origin,
                montage.ReferencePlane),
            150,
            "insert penetration offset");

        var oppositeFlangeAxis = new[]
        {
            new Point(450, 0, -400),
            new Point(450, 0, -2000),
        };
        FachwerkLowerRigelFlangeSplice flange =
            FachwerkLowerRigelNodeGeometry.BuildFlangeSplice(
                "outer-flange",
                new[]
                {
                    new Point(0, 0, -400),
                    new Point(0, 0, -2000),
                },
                oppositeFlangeAxis,
                montage,
                new Point(-500, 0, 0),
                new Vector(1, 0, 0),
                265);
        RequireNear(
            FachwerkLowerRigelNodeGeometry.Distance(
                flange.InsertBoundary[0],
                flange.InsertBoundary[1]),
            180,
            "flange insert length");
        RequireNear(
            Math.Abs(flange.InsertBoundary[0].Y),
            90,
            "flange insert half width");
        RequireNear(
            Math.Abs(flange.InsertBoundary.Average(point => point.X)),
            0,
            "flange insert remains centered on source axis");
        double contactRadius = Math.Sqrt(
            flange.TubeCutPlane.Origin.Y *
            flange.TubeCutPlane.Origin.Y +
            flange.TubeCutPlane.Origin.Z *
            flange.TubeCutPlane.Origin.Z);
        RequireNear(
            contactRadius,
            265,
            "flange cut contact on cylinder");
        RequireNear(
            FachwerkLowerRigelNodeGeometry.Distance(
                flange.LowerFragmentEndPoint,
                new Point(0, 0, -400)),
            0,
            "lower flange fragment preserves the source endpoint");
        Require(
            flange.LowerFragmentBoundary.Count == 4,
            "lower flange fragment must use a four-point contour");
        RequireNear(
            flange.LowerFragmentBoundary.Average(point => point.X),
            25,
            "lower flange handles lie on the inner face");
        RequireNear(
            FachwerkLowerRigelNodeGeometry.Distance(
                new Point(
                    (flange.LowerFragmentBoundary[1].X +
                     flange.LowerFragmentBoundary[2].X) * 0.5,
                    (flange.LowerFragmentBoundary[1].Y +
                     flange.LowerFragmentBoundary[2].Y) * 0.5,
                    (flange.LowerFragmentBoundary[1].Z +
                     flange.LowerFragmentBoundary[2].Z) * 0.5),
                new Point(25, 0, -400)),
            0,
            "outer lower flange reaches the source endpoint");

        FachwerkLowerRigelFlangeSplice innerFlange =
            FachwerkLowerRigelNodeGeometry.BuildFlangeSplice(
                "inner-flange",
                oppositeFlangeAxis,
                new[]
                {
                    new Point(0, 0, -400),
                    new Point(0, 0, -2000),
                },
                montage,
                new Point(-500, 0, 0),
                new Vector(1, 0, 0),
                265);
        RequireNear(
            innerFlange.LowerFragmentBoundary.Average(point => point.X),
            425,
            "inner lower flange handles remain on its inner face");
        foreach (Point tubeCorner in
                 innerFlange.LowerFragmentBoundary.Skip(1).Take(2))
        {
            RequireNear(
                Math.Sqrt(
                    tubeCorner.Y * tubeCorner.Y +
                    tubeCorner.Z * tubeCorner.Z),
                265,
                "inner lower flange corner remains seated on tube");
        }

        double automaticLength =
            FachwerkLowerRigelNodeAutoLength.Calculate(
                leftTube,
                rightTube,
                leftWeb,
                new[]
                {
                    new Point(250, 0, 0),
                    new Point(250, 0, -600),
                },
                new[]
                {
                    new Point(0, 0, -400),
                    new Point(0, 0, -2000),
                },
                oppositeFlangeAxis,
                25,
                530,
                350,
                500);
        double automaticShortEdge =
            FachwerkLowerRigelNodeAutoLength.Measure(
                leftTube,
                rightTube,
                leftWeb,
                new[]
                {
                    new Point(250, 0, 0),
                    new Point(250, 0, -600),
                },
                new[]
                {
                    new Point(0, 0, -400),
                    new Point(0, 0, -2000),
                },
                oppositeFlangeAxis,
                25,
                530,
                350,
                automaticLength);
        Require(
            automaticLength > 300,
            "automatic length must reject the solution inside the tube");
        Require(
            Math.Abs(
                automaticShortEdge -
                FachwerkLowerRigelNodeAutoLength.TargetShortEdgeLength) <= 0.1,
            "automatic lower-flange short edge must be 250 mm");

        FachwerkLowerRigelFlangeSplice offsetTubeFlange =
            FachwerkLowerRigelNodeGeometry.BuildFlangeSplice(
                "offset-tube-flange",
                new[]
                {
                    new Point(0, 0, -400),
                    new Point(0, 0, -2000),
                },
                oppositeFlangeAxis,
                montage,
                new Point(-500, 50, 0),
                new Vector(1, 0, 0),
                265);
        foreach (Point tubeCorner in
                 offsetTubeFlange.LowerFragmentBoundary.Skip(1).Take(2))
        {
            double offsetTubeCornerRadius = Math.Sqrt(
                Math.Pow(tubeCorner.Y - 50, 2) +
                tubeCorner.Z * tubeCorner.Z);
            RequireNear(
                offsetTubeCornerRadius,
                265,
                "offset tube lower flange corner");
        }
        string cutLabel =
            FachwerkLowerRigelNodeTeklaAdapter.StablePluginLabel(
                "left-end-addition-trim");
        Require(
            cutLabel ==
            FachwerkLowerRigelNodeTeklaAdapter.StablePluginLabel(
                "left-end-addition-trim"),
            "lower rigel output labels must be deterministic");
        Require(
            cutLabel !=
            FachwerkLowerRigelNodeTeklaAdapter.StablePluginLabel(
                "left-end-addition"),
            "cut and add outputs must have different labels");
        Require(
            cutLabel.Length == 13,
            "lower rigel output labels must stay compact");
        Require(
            FachwerkLowerRigelNodeTeklaAdapter.NodeMaterial == "C355-5",
            "lower rigel node material");
        Require(
            FachwerkLowerRigelNodeTeklaAdapter.RegularPartPrefix == "515-60.",
            "lower rigel regular part prefix");
        Require(
            FachwerkLowerRigelNodeTeklaAdapter.InsertPrefix ==
            "515-60.ВС-",
            "lower rigel insert prefix");
        RequireNear(
            FachwerkLowerRigelNodeTeklaAdapter.TubeOuterCutterDiameter,
            800,
            "lower rigel outer shell cutter diameter");
        RequireNear(
            FachwerkLowerRigelNodeTeklaAdapter.CentralTubeAxisOverrun,
            200,
            "lower rigel central tube axis overrun");

        IReadOnlyList<Point> bevelTriangle =
            FachwerkLowerRigelNodeTeklaAdapter.BuildBevelCutterBoundary(
                new Point(0, 0, 0),
                new Vector(0, 1, 0),
                new Vector(1, 0, 0));
        RequirePointNear(
            bevelTriangle[0],
            new Point(-2, -2, 0),
            "bevel outside corner");
        RequirePointNear(
            bevelTriangle[1],
            new Point(-2, 14, 0),
            "bevel width corner");
        RequirePointNear(
            bevelTriangle[2],
            new Point(14, -2, 0),
            "bevel depth corner");

        var nearEndState = new BeamState(
            new Point(-1000, 20, 30),
            new Point(-250, 40, 50));
        BeamState correctedNearEnd =
            FachwerkLowerRigelNodeTeklaAdapter.BuildCorrectedBeamState(
                nearEndState,
                new Point(-175, 42, 52));
        RequirePointNear(
            correctedNearEnd.Start,
            nearEndState.Start,
            "far start endpoint");
        RequirePointNear(
            correctedNearEnd.End,
            new Point(-175, 42, 52),
            "source-axis end reaches the joint plane");

        var nearStartState = new BeamState(
            new Point(250, 40, 50),
            new Point(1000, 20, 30));
        BeamState correctedNearStart =
            FachwerkLowerRigelNodeTeklaAdapter.BuildCorrectedBeamState(
                nearStartState,
                new Point(175, 42, 52));
        RequirePointNear(
            correctedNearStart.Start,
            new Point(175, 42, 52),
            "source-axis start reaches the joint plane");
        RequirePointNear(
            correctedNearStart.End,
            nearStartState.End,
            "far end endpoint");

        Console.WriteLine(
            "LOWER_RIGEL_NODE_OK " +
            "gap=25 width=350 left=500 right=200 " +
            "autoLength=" +
            automaticLength.ToString("0.###", CultureInfo.InvariantCulture) +
            " autoEdge=" +
            automaticShortEdge.ToString("0.###", CultureInfo.InvariantCulture) +
            " " +
            "farEndpoints=preserved endContours=" +
            layout.LeftEndPlateBoundary.Count + "," +
            layout.RightEndPlateBoundary.Count);
        return 0;
    }

    private static double SignedAlong(
        Point point,
        FachwerkLowerRigelPlane plane)
    {
        return FachwerkLowerRigelNodeGeometry.Dot(
            FachwerkLowerRigelNodeGeometry.VectorBetween(
                plane.Origin,
                point),
            plane.Normal);
    }

    private static T InvokeLowerRigelNodePrivate<T>(
        string methodName,
        params object[] arguments)
    {
        MethodInfo? method =
            typeof(FachwerkLowerRigelNodePlugin).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
        Require(method != null, "missing lower-rigel helper " + methodName);
        object? result = method!.Invoke(null, arguments);
        Require(result is T, "unexpected result from lower-rigel helper " + methodName);
        return (T)result!;
    }

    private static void RequireNear(
        double actual,
        double expected,
        string label)
    {
        if (Math.Abs(actual - expected) > 1e-3)
        {
            throw new InvalidOperationException(
                label + " expected " +
                expected.ToString("0.###", CultureInfo.InvariantCulture) +
                ", got " +
                actual.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static void CheckRigelPlaneReferencedBevel(
        IReadOnlyList<Point> boundary,
        Vector rigelPlaneDirection,
        Vector rigelPlaneNormal,
        Vector outward,
        double rootFace,
        string label)
    {
        Require(boundary.Count == 4, label + " cutter must have four points");
        Point root = boundary[0];
        RequireNear(
            root.X * rigelPlaneNormal.X +
            root.Y * rigelPlaneNormal.Y +
            root.Z * rigelPlaneNormal.Z,
            0,
            label + " root must remain on the rigel face plane");
        RequireNear(
            root.X * outward.X +
            root.Y * outward.Y +
            root.Z * outward.Z,
            rootFace,
            label + " root face");

        Vector bevel = new Vector(
            boundary[1].X - root.X,
            boundary[1].Y - root.Y,
            boundary[1].Z - root.Z);
        double bevelLength = Math.Sqrt(
            bevel.X * bevel.X +
            bevel.Y * bevel.Y +
            bevel.Z * bevel.Z);
        double referenceLength = Math.Sqrt(
            rigelPlaneDirection.X * rigelPlaneDirection.X +
            rigelPlaneDirection.Y * rigelPlaneDirection.Y +
            rigelPlaneDirection.Z * rigelPlaneDirection.Z);
        double cosine = Math.Abs(
            (bevel.X * rigelPlaneDirection.X +
             bevel.Y * rigelPlaneDirection.Y +
             bevel.Z * rigelPlaneDirection.Z) /
            (bevelLength * referenceLength));
        cosine = Math.Max(-1, Math.Min(1, cosine));
        RequireNear(
            Math.Acos(cosine) * 180.0 / Math.PI,
            40,
            label + " angle relative to rigel plane");
    }

    private static void RequirePointNear(
        Point actual,
        Point expected,
        string label)
    {
        RequireNear(actual.X, expected.X, label + " X");
        RequireNear(actual.Y, expected.Y, label + " Y");
        RequireNear(actual.Z, expected.Z, label + " Z");
    }

    private static int CheckTeklaPartCreation(Model model, string? requestedCatalogPath)
    {
        var created = new List<ModelObject>();
        var workPlaneHandler = model.GetWorkPlaneHandler();
        var previousPlane = workPlaneHandler.GetCurrentTransformationPlane();
        try
        {
            var insertion = new Point(250000, -250000, 43330);
            var frame = new FachwerkColumnFrame(
                insertion,
                new FachwerkVector(1, 0, 0),
                insertion.Z);
            var breaks = new[]
            {
                new FachwerkColumnBreak(16020, "ALL_FOUR", 1),
                new FachwerkColumnBreak(22931, "ALL_FOUR", 2),
                new FachwerkColumnBreak(36245, "ALL_FOUR", 3),
            };
            var specs = FachwerkColumnGeometry.BuildSectionParts(
                FachwerkColumnProfileCatalog.Load(requestedCatalogPath).Require("СФ1"),
                frame,
                breaks);
            Require(specs.Count == 19, "live Tekla check expected 19 physical pieces, got " + specs.Count);
            Require(workPlaneHandler.SetCurrentTransformationPlane(FachwerkColumnGeometry.CreateFacadePlane(frame)),
                "failed to set temporary facade work plane");

            for (var index = 0; index < specs.Count; index++)
            {
                created.Add(FachwerkColumnGeometry.CreatePart(
                    specs[index],
                    "C355-5",
                    "99",
                    "SMOKE_API_СФ1",
                    0,
                    index + 1));
            }

            Console.WriteLine("TEKLA_PARTS_OK inserted=" + created.Count + " expected=19");
            return 0;
        }
        finally
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
            model.CommitChanges();
        }
    }

    private static int CheckTeklaBatchPartCreation(Model model, string catalogPath, string planPath)
    {
        var catalog = FachwerkColumnProfileCatalog.Load(catalogPath);
        var created = new List<ModelObject>();
        var workPlaneHandler = model.GetWorkPlaneHandler();
        var previousPlane = workPlaneHandler.GetCurrentTransformationPlane();
        var checkedProfiles = 0;
        var checkedBreaks = 0;
        var checkedParts = 0;
        try
        {
            foreach (var rawLine in File.ReadAllLines(planPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var values = line.Split('|');
                Require(values.Length == 3, "invalid break plan line: " + line);
                var profileKey = values[0].Trim();
                Require(double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var insertionZ),
                    "invalid insertion Z for " + profileKey);

                var breaks = ParseBreaks(profileKey, values[2]);
                var insertion = new Point(250000 + checkedProfiles * 3000, -250000, insertionZ);
                var frame = new FachwerkColumnFrame(
                    insertion,
                    new FachwerkVector(1, 0, 0),
                    insertionZ);
                var profile = catalog.Require(profileKey);
                var specs = FachwerkColumnGeometry.BuildSectionParts(profile, frame, breaks);
                var expected = ExpectedSectionPieceCount(profile, frame, breaks);
                Require(specs.Count == expected,
                    profileKey + " expected " + expected + " physical pieces, got " + specs.Count);
                Require(workPlaneHandler.SetCurrentTransformationPlane(FachwerkColumnGeometry.CreateFacadePlane(frame)),
                    "failed to set temporary facade work plane for " + profileKey);

                var profileCreated = new List<ModelObject>();
                try
                {
                    for (var index = 0; index < specs.Count; index++)
                    {
                        var part = FachwerkColumnGeometry.CreatePart(
                            specs[index],
                            "C355-5",
                            "99",
                            "SMOKE_API_" + profileKey,
                            0,
                            index + 1);
                        profileCreated.Add(part);
                        created.Add(part);
                    }
                    Require(profileCreated.Count == expected,
                        profileKey + " inserted " + profileCreated.Count + " physical pieces, expected " + expected);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        profileKey + " failed after inserting " + profileCreated.Count + " of " + expected + " pieces",
                        exception);
                }

                checkedProfiles++;
                checkedBreaks += breaks.Count;
                checkedParts += profileCreated.Count;
            }

            Require(checkedProfiles == 47, "expected 47 planned columns, got " + checkedProfiles);
            Console.WriteLine(
                "TEKLA_BATCH_PARTS_OK profiles=" + checkedProfiles +
                " breaks=" + checkedBreaks +
                " parts=" + checkedParts);
            return 0;
        }
        finally
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
            model.CommitChanges();
        }
    }

    private static int CheckBreakPlan(string catalogPath, string planPath)
    {
        var catalog = FachwerkColumnProfileCatalog.Load(catalogPath);
        var checkedProfiles = 0;
        var checkedBreaks = 0;
        foreach (var rawLine in File.ReadAllLines(planPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var values = line.Split('|');
            Require(values.Length == 3, "invalid break plan line: " + line);
            var profileKey = values[0].Trim();
            Require(double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var insertionZ),
                "invalid insertion Z for " + profileKey);

            var breaks = ParseBreaks(profileKey, values[2]);

            var frame = new FachwerkColumnFrame(
                new Point(0, 0, 0),
                new FachwerkVector(1, 0, 0),
                insertionZ);
            var profile = catalog.Require(profileKey);
            var parts = FachwerkColumnGeometry.BuildSectionParts(profile, frame, breaks);
            var expectedByRole = ExpectedSectionPiecesByRole(profile, frame, breaks);
            foreach (var expected in expectedByRole)
            {
                var actual = parts.Count(item => string.Equals(item.Role, expected.Key, StringComparison.OrdinalIgnoreCase));
                Require(actual == expected.Value,
                    profileKey + "/" + expected.Key + " expected " + expected.Value + " pieces, got " + actual);
            }
            foreach (var part in parts)
            {
                var firstVertices = FachwerkColumnGeometry.BuildContourVertices(part.FirstBoundary);
                Require(firstVertices.Count >= 2 && firstVertices.All(item => item.Point.IsFinite()),
                    profileKey + "/" + part.Role + " has invalid first-boundary geometry after splitting");
            }
            checkedProfiles++;
            checkedBreaks += breaks.Count;
        }
        Require(checkedProfiles == 47, "expected 47 planned columns, got " + checkedProfiles);
        Console.WriteLine("BREAK_PLAN_OK profiles=" + checkedProfiles + " breaks=" + checkedBreaks);
        return 0;
    }

    private static List<FachwerkColumnBreak> ParseBreaks(string profileKey, string rawValue)
    {
        var breaks = new List<FachwerkColumnBreak>();
        var rawBreaks = rawValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < rawBreaks.Length; index++)
        {
            var breakValues = rawBreaks[index].Split(':');
            Require(breakValues.Length == 2, "invalid break for " + profileKey + ": " + rawBreaks[index]);
            Require(double.TryParse(breakValues[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var elevation),
                "invalid break elevation for " + profileKey + ": " + breakValues[0]);
            breaks.Add(new FachwerkColumnBreak(elevation, breakValues[1].Trim(), index + 1));
        }
        return breaks;
    }

    private static int CheckCatalog(string? requestedPath)
    {
        var catalog = FachwerkColumnProfileCatalog.Load(requestedPath);
        var frame = new FachwerkColumnFrame(new Point(0, 0, 0), new FachwerkVector(1, 0, 0));
        var pathCount = 0;
        var primitiveCount = 0;
        var arcCount = 0;
        var roundingCount = 0;
        var arcPointCount = 0;
        var bevelCount = 0;

        foreach (var profile in catalog.Profiles)
        {
            profile.Validate();
            var sectionParts = FachwerkColumnGeometry.BuildSectionParts(
                profile,
                frame,
                Array.Empty<FachwerkColumnBreak>());
            Require(sectionParts.Count == 7,
                profile.Key + " must create seven pieces at the intrinsic 450/380 transition");
            Require(sectionParts.Count(item => item.Role == "outer-flange") == 1,
                profile.Key + " must keep only the outer flange continuous at the section transition");
            Require(sectionParts.Where(item => item.Role != "outer-flange")
                    .GroupBy(item => item.Role)
                    .All(group => group.Count() == 2),
                profile.Key + " must split both webs and the inner flange at the section transition");
            var intrinsicTransitionAssemblyPlan =
                FachwerkColumnAssemblyPlanner.Build(profile.Mark, sectionParts);
            Require(intrinsicTransitionAssemblyPlan.Segments.Count == 1 &&
                    intrinsicTransitionAssemblyPlan.Segments[0].MemberPartIndices.Count == 7,
                profile.Key + " intrinsic 450/380 transition must remain one seven-part assembly");
            AssertCompleteAssemblyAssignment(sectionParts, intrinsicTransitionAssemblyPlan);
            AssertSectionTransitionExtensions(profile, sectionParts);
            var catalogJointLine = profile.RequirePath("outer-flange")
                .Primitives
                .First(item =>
                    item.NormalizedKind() == "line" &&
                    Math.Abs(item.End.Y - item.Start.Y) > 100 &&
                    (item.Start.Y + item.End.Y) * 0.5 <
                        profile.SectionTransition.StartY - 1);
            var catalogJointElevation =
                (catalogJointLine.Start.Y + catalogJointLine.End.Y) * 0.5;
            var catalogStiffeners =
                FachwerkColumnGeometry.BuildStiffenerSpecs(
                    profile,
                    frame,
                    Array.Empty<FachwerkColumnBreak>(),
                    new FachwerkColumnStiffenerSettings(
                        true,
                        "PL20",
                        "C355-5",
                        15,
                        new[] { catalogJointElevation }));
            Require(
                catalogStiffeners.Count == 1 &&
                catalogStiffeners[0].Contour.Count == 4 &&
                catalogStiffeners[0].Contour.Count(
                    item => item.HasChamfer) == 2,
                profile.Key +
                " must create one four-corner independent stiffener with two outer chamfers");
            var catalogJointParts = FachwerkColumnGeometry.BuildSectionParts(
                profile,
                frame,
                new[]
                {
                    new FachwerkColumnBreak(
                        catalogJointElevation,
                        FachwerkColumnBreak.AllFourMode,
                        1,
                        -5,
                        7,
                        -9,
                        11),
                });
            var catalogJointPlan = FachwerkColumnAssemblyPlanner.Build(
                profile.Mark,
                catalogJointParts);
            Require(
                catalogJointPlan.Segments.Count == 2 &&
                catalogJointParts.Select(item => item.AssemblySegmentIndex)
                    .Distinct()
                    .OrderBy(item => item)
                    .SequenceEqual(new[] { 0, 1 }),
                profile.Key +
                " perpendicular joint with offsets must create exactly two explicit assembly segments");
            Require(sectionParts.Where(item => item.Kind == FachwerkColumnPartKind.Flange)
                    .All(item => string.Equals(item.Profile, "PL50*180", StringComparison.OrdinalIgnoreCase)),
                profile.Key + " flanges must use PL50*180");
            Require(sectionParts.Where(item => item.Kind == FachwerkColumnPartKind.Web)
                    .Count(item => string.Equals(item.Profile, "PL25*350", StringComparison.OrdinalIgnoreCase)) == 2,
                profile.Key + " must create two lower PL25*350 web pieces");
            Require(sectionParts.Where(item => item.Kind == FachwerkColumnPartKind.Web)
                    .Count(item => string.Equals(item.Profile, "PL25*280", StringComparison.OrdinalIgnoreCase)) == 2,
                profile.Key + " must create two upper PL25*280 web pieces");
            var webPieceNumber = 0;
            foreach (var webPart in sectionParts.Where(item => item.Kind == FachwerkColumnPartKind.Web))
            {
                webPieceNumber++;
                var bevels = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
                    profile.Key,
                    webPart,
                    "catalog-piece-" + webPieceNumber.ToString("D2", CultureInfo.InvariantCulture));
                Require(bevels.Count == 2,
                    profile.Key + " web piece must create two physical edge bevels");
                double expectedRotationOffset =
                    string.Equals(webPart.Profile, "PL25*350", StringComparison.OrdinalIgnoreCase)
                        ? FachwerkColumnBevelGeometry.LowerCutterRotationOffset
                        : FachwerkColumnBevelGeometry.UpperCutterRotationOffset;
                Require(bevels.All(spec =>
                        Math.Abs(spec.CutterRotationOffset - expectedRotationOffset) < 1e-9),
                    profile.Key + "/" + webPart.Role + "/" + webPart.Profile +
                    " bevel rotation must follow the physical section height");
                bevelCount += bevels.Count;
            }

            var innerWebParts = sectionParts.Where(item => item.Role == "inner-web").ToArray();
            var outerWebParts = sectionParts.Where(item => item.Role == "outer-web").ToArray();
            Require(innerWebParts.Length == outerWebParts.Length,
                profile.Key + " web roles must have the same number of pieces");
            Require(innerWebParts.All(item => Math.Abs(item.NormalOffset + 77.5) < 0.001),
                profile.Key + " inner web must stay at -77.5 mm in the facade-normal direction");
            Require(outerWebParts.All(item => Math.Abs(item.NormalOffset - 77.5) < 0.001),
                profile.Key + " outer web must stay at +77.5 mm in the facade-normal direction");
            Require(innerWebParts.Concat(outerWebParts).All(item => item.SecondBoundary.Count == 0),
                profile.Key + " webs must be centerline PolyBeam specs, not plate contours");
            for (var webIndex = 0; webIndex < innerWebParts.Length; webIndex++)
            {
                AssertSamePrimitivePath(
                    profile.Key + "/web-piece-" + webIndex.ToString(CultureInfo.InvariantCulture),
                    innerWebParts[webIndex].FirstBoundary,
                    outerWebParts[webIndex].FirstBoundary);
            }

            var innerPath = profile.RequirePath("inner-flange");
            var outerPath = profile.RequirePath("outer-flange");
            Require(Math.Abs(Distance(innerPath.Primitives[0].Start, outerPath.Primitives[0].Start) - 450.0) < 1.01,
                profile.Key + " lower section depth must be 450 mm");
            Require(Math.Abs(Distance(
                    innerPath.Primitives[innerPath.Primitives.Count - 1].End,
                    outerPath.Primitives[outerPath.Primitives.Count - 1].End) - 380.0) < 1.01,
                profile.Key + " upper section depth must be 380 mm");
            var lowerWebAxisPoint = innerWebParts[0].FirstBoundary[0].Start;
            var upperWebAxisPoint = innerWebParts[innerWebParts.Length - 1]
                .FirstBoundary[innerWebParts[innerWebParts.Length - 1].FirstBoundary.Count - 1].End;
            Require(Math.Abs(Distance(lowerWebAxisPoint, innerPath.Primitives[0].Start) - 225.0) < 1.01,
                profile.Key + " lower web axis must be centered in the 450 mm section");
            Require(Math.Abs(Distance(
                    upperWebAxisPoint,
                    innerPath.Primitives[innerPath.Primitives.Count - 1].End) - 190.0) < 1.01,
                profile.Key + " upper web axis must be centered in the 380 mm section");
            foreach (var path in profile.Paths)
            {
                pathCount++;
                primitiveCount += path.Primitives.Count;
                arcCount += path.Primitives.Count(item => item.NormalizedKind() == "arc");
                AssertConnectedPath(profile.Key, path);
                var pieces = FachwerkColumnGeometry.BuildPieces(path, frame, Array.Empty<FachwerkColumnBreak>());
                Require(pieces.Count == 1, profile.Key + "/" + path.Role + " unexpectedly split without breaks");
                var vertices = FachwerkColumnGeometry.BuildContourVertices(pieces[0].Primitives);
                Require(vertices.Count >= 2, profile.Key + "/" + path.Role + " has fewer than two contour vertices");
                Require(vertices.All(item => item.Point.IsFinite()), profile.Key + "/" + path.Role + " has a non-finite contour vertex");
                Require(vertices.All(item => item.ChamferKind != FachwerkColumnChamferKind.Rounding || item.Radius > 0), profile.Key + "/" + path.Role + " has an invalid radius");
                roundingCount += vertices.Count(item => item.ChamferKind == FachwerkColumnChamferKind.Rounding);
                arcPointCount += vertices.Count(item => item.ChamferKind == FachwerkColumnChamferKind.ArcPoint);
            }
        }

        Require(catalog.Profiles.Count == 47, "expected 47 profiles, got " + catalog.Profiles.Count);
        Require(pathCount == 188, "expected 188 physical paths, got " + pathCount);
        Require(bevelCount == 376, "expected 376 intrinsic web-edge bevel specs, got " + bevelCount);
        Require(roundingCount + arcPointCount == arcCount, "curved vertices " + (roundingCount + arcPointCount) + " do not match source arcs " + arcCount);

        var sf1 = catalog.Require("СФ1");
        CheckInpAttributeContract();
        CheckFormRegistrationContract();
        CheckRigelConnectionContract();
        CheckRigelInsertConnectionContract();
        CheckFormValueContract();
        CheckFrameContract();
        CheckBreakAttributeContract();
        CheckStiffenerContract(sf1, frame);
        CheckBevelGeometryContract();
        CheckAttributeContract();
        CheckAssemblyPlanContract(sf1, frame);
        CheckJointPlaneContract(sf1, frame);
        CheckErectionJointContract(sf1, frame);
        var transitionSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            frame,
            Array.Empty<FachwerkColumnBreak>());
        Require(transitionSection.Count == 7, "СФ1 intrinsic section transition must create seven pieces");
        Require(transitionSection.Count(item => item.Kind == FachwerkColumnPartKind.Flange) == 3,
            "СФ1 must contain one continuous outer flange and two inner-flange pieces");
        Require(transitionSection.Count(item => item.Kind == FachwerkColumnPartKind.Web) == 4,
            "СФ1 must contain two pieces for each web");

        var referenceLine = sf1.Paths[0].Primitives.First(item => item.NormalizedKind() == "line");
        var splitElevation = (referenceLine.Start.Y + referenceLine.End.Y) * 0.5;
        foreach (var path in sf1.Paths)
        {
            var split = FachwerkColumnGeometry.BuildPieces(
                path,
                frame,
                new[] { new FachwerkColumnBreak(splitElevation, "ALL_FOUR", 1) });
            Require(split.Count == 2, "ALL_FOUR did not split СФ1/" + path.Role + " exactly once");
        }

        var allFourSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            frame,
            new[] { new FachwerkColumnBreak(splitElevation, "ALL_FOUR", 1) });
        Require(allFourSection.Count == 11,
            "one ordinary split plus the intrinsic transition must create eleven pieces, got " + allFourSection.Count);
        Require(allFourSection.Count(item => item.Role == "outer-flange") == 2,
            "ordinary split must split the outer flange");
        Require(allFourSection.Where(item => item.Role != "outer-flange")
                .GroupBy(item => item.Role)
                .All(group => group.Count() == 3),
            "ordinary split must additionally split both webs and the inner flange");

        var elevatedFrame = new FachwerkColumnFrame(new Point(0, 0, 43330), new FachwerkVector(1, 0, 0));
        var absoluteSplitSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            elevatedFrame,
            new[] { new FachwerkColumnBreak(43330 + splitElevation, "ALL_FOUR", 1) });
        Require(absoluteSplitSection.Count == 11,
            "absolute model elevation must split all four physical roles relative to insertion Z");

        var sf45 = catalog.Require("СФ45");
        var pluginLocalFrame = new FachwerkColumnFrame(
            new Point(0, 0, 0),
            new FachwerkVector(1, 0, 0),
            43330);
        var sf45Split = FachwerkColumnGeometry.BuildSectionParts(
            sf45,
            pluginLocalFrame,
            new[] { new FachwerkColumnBreak(34000, "ALL_FOUR", 1) });
        Require(sf45Split.Count == 11,
            "СФ45 at global insertion Z=43330 must create eleven pieces at absolute Z=34000");

        var legacyBreak = new FachwerkColumnBreak(splitElevation, "OUTER_WEB_CONTINUOUS", 1);
        Require(string.Equals(legacyBreak.Mode, "ALL_FOUR", StringComparison.OrdinalIgnoreCase),
            "legacy OUTER_WEB_CONTINUOUS values must normalize to ALL_FOUR");
        var legacySection = FachwerkColumnGeometry.BuildSectionParts(sf1, frame, new[] { legacyBreak });
        Require(legacySection.Count == 11,
            "legacy break mode must rebuild with the regular four-part contract");
        foreach (var role in legacySection.GroupBy(item => item.Role))
        {
            var offsets = role.Select(item => item.NormalOffset).Distinct().ToArray();
            Require(offsets.Length == 1, "split pieces changed the section offset for " + role.Key);
        }

        var coincidentTransitionSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            frame,
            new[] { new FachwerkColumnBreak(sf1.SectionTransition.EndY, "ALL_FOUR", 1) });
        Require(coincidentTransitionSection.Count == 8,
            "an explicit rigel split at the section transition must split all four physical roles");
        Require(coincidentTransitionSection.GroupBy(item => item.Role).All(group => group.Count() == 2),
            "coincident rigel split must override outer-flange continuity");

        var ignoredTransitionBreakSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            frame,
            new[]
            {
                new FachwerkColumnBreak(
                    (sf1.SectionTransition.StartY + sf1.SectionTransition.EndY) * 0.5,
                    "ALL_FOUR",
                    1),
            });
        Require(ignoredTransitionBreakSection.Count == 7,
            "a user break inside the intrinsic section transition must not create fragments");
        AssertSectionTransitionExtensions(sf1, ignoredTransitionBreakSection);

        var firstMultipleSplit = referenceLine.Start.Y + (referenceLine.End.Y - referenceLine.Start.Y) / 3.0;
        var secondMultipleSplit = referenceLine.Start.Y + (referenceLine.End.Y - referenceLine.Start.Y) * 2.0 / 3.0;
        var multipleSplitSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            frame,
            new[]
            {
                new FachwerkColumnBreak(firstMultipleSplit, "ALL_FOUR", 1),
                new FachwerkColumnBreak(secondMultipleSplit, "ALL_FOUR", 2),
            });
        Require(multipleSplitSection.Count == 15,
            "two ordinary levels plus the intrinsic transition must create fifteen pieces, got " + multipleSplitSection.Count);
        Require(multipleSplitSection.Count(item => item.Role == "outer-flange") == 3,
            "two ordinary levels must create three outer-flange pieces");
        Require(multipleSplitSection.Where(item => item.Role != "outer-flange")
                .GroupBy(item => item.Role)
                .All(group => group.Count() == 4),
            "two ordinary levels must create four pieces for each transition-split role");

        var sf1ProductionSection = FachwerkColumnGeometry.BuildSectionParts(
            sf1,
            pluginLocalFrame,
            new[]
            {
                new FachwerkColumnBreak(16020, "ALL_FOUR", 1),
                new FachwerkColumnBreak(22931, "ALL_FOUR", 2),
                new FachwerkColumnBreak(36245, "ALL_FOUR", 3),
            });
        Require(sf1ProductionSection.Count == 19,
            "СФ1 production rigel levels must create nineteen physical pieces, got " + sf1ProductionSection.Count);
        Require(sf1ProductionSection.Count(item => item.Role == "outer-flange") == 4,
            "СФ1 production rigel levels must create four outer-flange pieces");
        Require(sf1ProductionSection.Where(item => item.Role != "outer-flange")
                .GroupBy(item => item.Role)
                .All(group => group.Count() == 5),
            "СФ1 production levels must create five pieces for both webs and the inner flange");
        var sf1ProductionAssemblyPlan =
            FachwerkColumnAssemblyPlanner.Build(sf1.Mark, sf1ProductionSection);
        Require(sf1ProductionAssemblyPlan.Segments.Count == 4,
            "three user rigel levels must create exactly four assemblies; " +
            "the intrinsic 450/380 transition must not add an assembly");
        Require(sf1ProductionAssemblyPlan.Segments.Count(
                segment => segment.MemberPartIndices.Count == 7) == 1,
            "the assembly crossing the intrinsic 450/380 transition must contain all seven physical pieces");
        Require(sf1ProductionAssemblyPlan.Segments.Count(
                segment => segment.MemberPartIndices.Count == 4) == 3,
            "the three assemblies outside the intrinsic transition must contain four physical roles each");
        AssertCompleteAssemblyAssignment(sf1ProductionSection, sf1ProductionAssemblyPlan);

        foreach (var repairedProfile in new[] { catalog.Require("СФ24"), catalog.Require("СФ27") })
        {
            var inner = repairedProfile.RequirePath("inner-flange");
            var outer = repairedProfile.RequirePath("outer-flange");
            Require(Math.Abs(Distance(inner.Primitives[0].Start, outer.Primitives[0].Start) - 450.0) < 1.01,
                repairedProfile.Key + " lost the repaired lower semantic segment");
        }

        Console.WriteLine(
            "CATALOG_OK profiles=" + catalog.Profiles.Count.ToString(CultureInfo.InvariantCulture) +
            " paths=" + pathCount.ToString(CultureInfo.InvariantCulture) +
            " primitives=" + primitiveCount.ToString(CultureInfo.InvariantCulture) +
            " arcs=" + arcCount.ToString(CultureInfo.InvariantCulture) +
            " rounded=" + roundingCount.ToString(CultureInfo.InvariantCulture) +
            " arcPoints=" + arcPointCount.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static void CheckAssemblyPlanContract(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame)
    {
        var transitionParts = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>());
        var transitionPlan = FachwerkColumnAssemblyPlanner.Build(profile.Mark, transitionParts);
        Require(transitionPlan.Segments.Count == 1,
            "intrinsic 450/380 transition must remain one column assembly");
        Require(transitionPlan.Segments[0].MemberPartIndices.Count == 7,
            "the transition assembly must contain both physical pieces of each interrupted role " +
            "and the continuous outer flange");
        AssertCompleteAssemblyAssignment(transitionParts, transitionPlan);

        var referenceLine = profile.Paths[0].Primitives.First(item => item.NormalizedKind() == "line");
        var splitElevation = (referenceLine.Start.Y + referenceLine.End.Y) * 0.5;
        var splitParts = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            new[] { new FachwerkColumnBreak(splitElevation, "ALL_FOUR", 1) });
        var splitPlan = FachwerkColumnAssemblyPlanner.Build(profile.Mark, splitParts);
        Require(splitPlan.Segments.Count == 2,
            "one user break must create two assemblies regardless of the intrinsic transition");
        Require(splitPlan.Segments.Count(segment => segment.MemberPartIndices.Count == 4) == 1,
            "the assembly outside the intrinsic transition must contain four physical roles");
        Require(splitPlan.Segments.Count(segment => segment.MemberPartIndices.Count == 7) == 1,
            "the assembly crossing the intrinsic transition must contain seven physical pieces");
        AssertCompleteAssemblyAssignment(splitParts, splitPlan);
    }

    private static void CheckJointPlaneContract(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame)
    {
        var referenceLine = profile.RequirePath("outer-flange")
            .Primitives
            .First(item => item.NormalizedKind() == "line");
        var splitElevation =
            referenceLine.Start.Y +
            (referenceLine.End.Y - referenceLine.Start.Y) * 0.5;
        var zeroBreak = new FachwerkColumnBreak(
            splitElevation,
            FachwerkColumnBreak.AllFourMode,
            1);
        var zeroParts = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            new[] { zeroBreak });
        var zeroOuter = RequireJointEndpoint(
            zeroParts,
            "outer-flange",
            0,
            true);
        var outerLowerPart = zeroParts.Single(
            item => item.Role == "outer-flange" &&
                item.AssemblySegmentIndex == 0);
        var tangent = UpwardTerminalTangent(
            outerLowerPart.FirstBoundary[
                outerLowerPart.FirstBoundary.Count - 1],
            true);
        foreach (var role in new[]
        {
            "inner-flange",
            "inner-web",
            "outer-web",
        })
        {
            var endpoint = RequireJointEndpoint(
                zeroParts,
                role,
                0,
                true);
            Require(
                Math.Abs(Project(endpoint, zeroOuter, tangent)) <= 0.05,
                "zero-offset perpendicular joint lost coplanarity for " + role);
        }

        var offsetBreak = new FachwerkColumnBreak(
            splitElevation,
            FachwerkColumnBreak.AllFourMode,
            1,
            -20,
            30,
            -40,
            50);
        var offsetParts = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            new[] { offsetBreak });
        foreach (var role in new[] { "inner-flange", "outer-flange" })
        {
            var lower = RequireJointEndpoint(offsetParts, role, 0, true);
            var upper = RequireJointEndpoint(offsetParts, role, 1, false);
            Require(
                Math.Abs(Project(lower, zeroOuter, tangent) + 20) <= 0.1,
                role + " lower offset is not -20 mm");
            Require(
                Math.Abs(Project(upper, zeroOuter, tangent) - 30) <= 0.1,
                role + " upper offset is not +30 mm");
        }
        foreach (var role in new[] { "inner-web", "outer-web" })
        {
            var lower = RequireJointEndpoint(offsetParts, role, 0, true);
            var upper = RequireJointEndpoint(offsetParts, role, 1, false);
            Require(
                Math.Abs(Project(lower, zeroOuter, tangent) + 40) <= 0.1,
                role + " lower offset is not -40 mm");
            Require(
                Math.Abs(Project(upper, zeroOuter, tangent) - 50) <= 0.1,
                role + " upper offset is not +50 mm");
        }
        var offsetAssemblyPlan = FachwerkColumnAssemblyPlanner.Build(
            profile.Mark,
            offsetParts);
        Require(
            offsetAssemblyPlan.Segments.Count == 2,
            "four independent offsets must not create extra assemblies");

        var reversedOuter = new FachwerkColumnPathDefinition
        {
            Role = "outer-flange",
            Profile = "PL50*180",
            Primitives = new List<FachwerkColumnPrimitiveDefinition>
            {
                new FachwerkColumnPrimitiveDefinition
                {
                    Kind = "line",
                    Start = new FachwerkColumnLocalPoint { X = 100, Y = 1000 },
                    End = new FachwerkColumnLocalPoint { X = 0, Y = 0 },
                },
            },
        };
        var reversedBreak = new FachwerkColumnBreak(
            500,
            FachwerkColumnBreak.AllFourMode,
            1,
            -25,
            35,
            -45,
            55);
        var reversedJoints = FachwerkColumnGeometry.ResolveJointPlanes(
            reversedOuter,
            frame,
            new[] { reversedBreak },
            new FachwerkColumnSectionTransitionDefinition
            {
                StartY = -2000,
                EndY = -1000,
            });
        Require(
            reversedJoints.Count == 1 &&
            reversedJoints[0].UpwardNormal.Y > 0,
            "reversed source path changed the semantic upward offset direction");
        var reversedPieces = FachwerkColumnGeometry.SplitByJointPlanes(
            reversedOuter,
            reversedOuter.Primitives,
            reversedJoints,
            false,
            0);
        Require(
            reversedPieces.Count == 2 &&
            reversedPieces[0].AssemblySegmentIndex == 1 &&
            reversedPieces[1].AssemblySegmentIndex == 0,
            "reversed path lost explicit lower/upper assembly ownership");
    }

    private static void CheckErectionJointContract(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame)
    {
        var referenceLine = profile.RequirePath("outer-flange")
            .Primitives
            .First(item => item.NormalizedKind() == "line");
        var splitElevation =
            referenceLine.Start.Y +
            (referenceLine.End.Y - referenceLine.Start.Y) * 0.5;
        var ordinaryBreak = new FachwerkColumnBreak(
            splitElevation,
            FachwerkColumnBreak.AllFourMode,
            1,
            FachwerkColumnBreak.ErectionFlangeLowerOffset,
            FachwerkColumnBreak.ErectionFlangeUpperOffset,
            FachwerkColumnBreak.ErectionWebLowerOffset,
            FachwerkColumnBreak.ErectionWebUpperOffset);
        var ordinaryParts = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            new[] { ordinaryBreak });
        Require(
            FachwerkColumnGeometry.BuildErectionJointSpecs(
                profile,
                frame,
                new[] { ordinaryBreak },
                ordinaryParts).Count == 0,
            "ordinary perpendicular joint must not create erection inserts or bevels");

        var erectionBreak = new FachwerkColumnBreak(
            splitElevation,
            FachwerkColumnBreak.ErectionSpliceMode,
            1,
            FachwerkColumnBreak.ErectionFlangeLowerOffset,
            FachwerkColumnBreak.ErectionFlangeUpperOffset,
            FachwerkColumnBreak.ErectionWebLowerOffset,
            FachwerkColumnBreak.ErectionWebUpperOffset);
        Require(
            erectionBreak.IsErectionSplice() &&
            string.Equals(
                erectionBreak.Mode,
                FachwerkColumnBreak.ErectionSpliceMode,
                StringComparison.Ordinal),
            "erection joint mode must survive normalization");
        var customErectionBreak = new FachwerkColumnBreak(
            splitElevation,
            FachwerkColumnBreak.ErectionSpliceMode,
            1,
            -71,
            82,
            -39,
            -68);
        Require(
            Math.Abs(customErectionBreak.FlangeLowerOffset + 71) < 1e-9 &&
            Math.Abs(customErectionBreak.FlangeUpperOffset - 82) < 1e-9 &&
            Math.Abs(customErectionBreak.WebLowerOffset + 39) < 1e-9 &&
            Math.Abs(customErectionBreak.WebUpperOffset + 68) < 1e-9,
            "custom erection offsets must remain editable and persisted");

        var erectionParts = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            new[] { erectionBreak });
        var specs = FachwerkColumnGeometry.BuildErectionJointSpecs(
            profile,
            frame,
            new[] { erectionBreak },
            erectionParts);
        Require(specs.Count == 1,
            "one erection joint must create one erection geometry group");
        var spec = specs[0];
        Require(spec.Inserts.Count == 2,
            "one erection joint must create exactly two flange inserts");
        Require(
            spec.Inserts.Select(item => item.FlangeRole)
                .OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(
                    new[] { "inner-flange", "outer-flange" },
                    StringComparer.Ordinal),
            "erection inserts must belong to the inner and outer flange paths");
        foreach (var insert in spec.Inserts)
        {
            var projectedLength =
                (insert.End.X - insert.Start.X) * insert.UpwardTangent.X +
                (insert.End.Y - insert.Start.Y) * insert.UpwardTangent.Y;
            Require(
                Math.Abs(
                    projectedLength -
                    (FachwerkColumnBreak.ErectionFlangeUpperOffset -
                     FachwerkColumnBreak.ErectionFlangeLowerOffset +
                     FachwerkColumnGeometry.ErectionInsertUpperPenetration)) <=
                0.1,
                insert.FlangeRole +
                " erection insert must extend 30 mm into the upper flange");
        }

        Require(spec.Bevels.Count == 6,
            "one erection joint must create exactly six bevels");
        Require(
            spec.Bevels.Count(item =>
                item.Target ==
                FachwerkColumnErectionBevelTarget.FlangeInsert) == 2,
            "both flange inserts must receive a lower-end bevel");
        Require(
            spec.Bevels.Count(item =>
                item.Target ==
                FachwerkColumnErectionBevelTarget.PhysicalPart) == 4,
            "upper flanges and upper webs must receive four bevels");
        Require(
            spec.Bevels.All(item =>
                Math.Abs(item.Thickness -
                    (item.SemanticRole.Contains("web")
                        ? FachwerkColumnGeometry.WebThickness
                        : FachwerkColumnGeometry.FlangeThickness)) < 1e-9),
            "erection bevel thickness must match its target part");
        Require(
            spec.Bevels.Count(item =>
                item.SemanticRole.EndsWith(
                    "-upper-lower-end",
                    StringComparison.Ordinal)) == 4 &&
            spec.Bevels.All(item =>
                !item.SemanticRole.EndsWith(
                    "-upper-end",
                    StringComparison.Ordinal)),
            "bevels must be limited to insert lower ends and upper-part lower ends");
        var webBevels = spec.Bevels
            .Where(item => item.SemanticRole.Contains("web"))
            .ToArray();
        Require(
            webBevels.Length == 2 &&
            webBevels.Any(item => item.OutwardNormal.Z > 0.99) &&
            webBevels.Any(item => item.OutwardNormal.Z < -0.99),
            "web bevels must open outward on opposite sides of the box");
        Require(
            Math.Abs(FachwerkColumnGeometry.ErectionBevelAngleDeg - 40) <
                1e-9 &&
            Math.Abs(FachwerkColumnGeometry.ErectionBevelRootFace - 2) <
                1e-9,
            "erection bevel contract must remain 40 degrees with 2 mm root face");
    }

    private static FachwerkColumnLocalPoint RequireJointEndpoint(
        IReadOnlyList<FachwerkColumnPartSpec> parts,
        string role,
        int assemblySegmentIndex,
        bool end)
    {
        var candidates = parts
            .Where(item => item.Role == role &&
                item.AssemblySegmentIndex == assemblySegmentIndex)
            .Select(item =>
            {
                var primitives = item.FirstBoundary;
                return end
                    ? primitives[primitives.Count - 1].End
                    : primitives[0].Start;
            })
            .OrderBy(item => item.Y)
            .ToArray();
        Require(
            candidates.Length > 0,
            "missing joint endpoint for " + role +
            "/assembly-" + assemblySegmentIndex);
        return end
            ? candidates[candidates.Length - 1]
            : candidates[0];
    }

    private static FachwerkVector UpwardTerminalTangent(
        FachwerkColumnPrimitiveDefinition primitive,
        bool atEnd)
    {
        double x;
        double y;
        if (primitive.NormalizedKind() == "line")
        {
            x = primitive.End.X - primitive.Start.X;
            y = primitive.End.Y - primitive.Start.Y;
        }
        else
        {
            var point = atEnd ? primitive.End : primitive.Start;
            var radialX = point.X - primitive.Center.X;
            var radialY = point.Y - primitive.Center.Y;
            var sign = primitive.SweepDeg >= 0 ? 1.0 : -1.0;
            x = -radialY * sign;
            y = radialX * sign;
        }
        var length = Math.Sqrt(x * x + y * y);
        x /= length;
        y /= length;
        if (y < 0 || (Math.Abs(y) <= 1e-9 && x < 0))
        {
            x = -x;
            y = -y;
        }
        return new FachwerkVector(x, y, 0);
    }

    private static double Project(
        FachwerkColumnLocalPoint point,
        FachwerkColumnLocalPoint origin,
        FachwerkVector direction) =>
        (point.X - origin.X) * direction.X +
        (point.Y - origin.Y) * direction.Y;

    private static void AssertCompleteAssemblyAssignment(
        IReadOnlyList<FachwerkColumnPartSpec> parts,
        FachwerkColumnAssemblyPlan plan)
    {
        Require(plan.AssignedPartIndices.SequenceEqual(Enumerable.Range(0, parts.Count)),
            "assembly plan must assign every physical piece exactly once");
        foreach (var segment in plan.Segments)
        {
            Require(string.Equals(parts[segment.MainPartIndex].Role, "outer-web", StringComparison.OrdinalIgnoreCase),
                "positive-offset outer-web must remain the semantic left-web main part");
            Require(segment.MemberPartIndices.Distinct().Count() == segment.MemberPartIndices.Count,
                "assembly segment contains duplicate physical part indices");
            Require(segment.WeldSecondaryPartIndices.Count == segment.MemberPartIndices.Count - 1,
                "each assembly secondary must have exactly one shop weld to the main part");
            var weldKeys = segment.WeldSecondaryPartIndices
                .Select(index => FachwerkColumnAssemblyTeklaAdapter.BuildWeldKey(segment, parts[index], index))
                .ToArray();
            Require(weldKeys.Distinct(StringComparer.Ordinal).Count() == weldKeys.Length,
                "each physical secondary in an assembly must have a unique Tekla weld key");
        }
    }

    private static Dictionary<string, int> ExpectedSectionPiecesByRole(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks)
    {
        var effectiveBreaks = new List<FachwerkColumnBreak>();
        if (breaks != null)
        {
            effectiveBreaks.AddRange(breaks.Where(item =>
            {
                var localY = item.Elevation - frame.GlobalInsertionZ;
                return localY <= profile.SectionTransition.StartY + 0.25 ||
                    localY >= profile.SectionTransition.EndY - 0.25;
            }));
        }
        effectiveBreaks.Add(new FachwerkColumnBreak(
            frame.GlobalInsertionZ + profile.SectionTransition.StartY,
            FachwerkColumnBreak.SectionTransitionMode,
            -1001));
        effectiveBreaks.Add(new FachwerkColumnBreak(
            frame.GlobalInsertionZ + profile.SectionTransition.EndY,
            FachwerkColumnBreak.SectionTransitionMode,
            -1000));

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in profile.Paths)
        {
            var role = path.NormalizedRole();
            result[role] = FachwerkColumnGeometry.BuildPieces(path, frame, effectiveBreaks)
                .Count(piece => role == "outer-flange" ||
                    !IsInsideSectionTransition(piece.Primitives, profile.SectionTransition));
        }
        return result;
    }

    private static void AssertSectionTransitionExtensions(
        FachwerkColumnProfileDefinition profile,
        IReadOnlyList<FachwerkColumnPartSpec> parts)
    {
        var transition = profile.SectionTransition;
        var jointY = (transition.StartY + transition.EndY) * 0.5;
        var outer = parts.Where(item => item.Role == "outer-flange").ToArray();
        Require(outer.Length == 1, profile.Key + " outer flange must remain one continuous physical part");
        var sourceOuter = profile.RequirePath("outer-flange").Primitives;
        Require(outer[0].FirstBoundary.Count == sourceOuter.Count,
            profile.Key + " outer flange transition changed primitive topology");
        Require(outer[0].FirstBoundary.Select(item => item.NormalizedKind())
                .SequenceEqual(sourceOuter.Select(item => item.NormalizedKind())),
            profile.Key + " outer flange transition changed primitive kinds");
        var physicalOuterBounds = PrimitiveBounds(outer[0].FirstBoundary);
        Require(physicalOuterBounds.MinY < transition.StartY &&
                physicalOuterBounds.MaxY > transition.EndY,
            profile.Key + " outer flange no longer crosses the complete section transition");

        foreach (var role in new[] { "inner-flange", "inner-web", "outer-web" })
        {
            var roleParts = parts.Where(item => item.Role == role).ToArray();
            Require(roleParts.Length == 2,
                profile.Key + "/" + role + " must have exactly two physical parts around the transition");

            var ordered = roleParts
                .OrderBy(item => PrimitiveBounds(item.FirstBoundary).MinY)
                .ToArray();
            var lowerTerminal =
                ordered[0].FirstBoundary[ordered[0].FirstBoundary.Count - 1];
            var upperInitial = ordered[1].FirstBoundary[0];
            Require(Math.Abs(lowerTerminal.End.Y - jointY) <= 0.26,
                profile.Key + "/" + role + " lower part does not extend to the common transition plane");
            Require(Math.Abs(upperInitial.Start.Y - jointY) <= 0.26,
                profile.Key + "/" + role + " upper part does not extend to the common transition plane");
            Require(lowerTerminal.NormalizedKind() == "arc",
                profile.Key + "/" + role + " lower part retains a short transition tail");
            Require(upperInitial.NormalizedKind() == "arc",
                profile.Key + "/" + role + " upper part retains a short transition tail");
        }
    }

    private static bool IsInsideSectionTransition(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives,
        FachwerkColumnSectionTransitionDefinition transition)
    {
        var bounds = PrimitiveBounds(primitives);
        var middleY = (bounds.MinY + bounds.MaxY) * 0.5;
        return bounds.MaxY - bounds.MinY > 0.25 &&
            bounds.MinY >= transition.StartY - 0.25 &&
            bounds.MaxY <= transition.EndY + 0.25 &&
            middleY > transition.StartY + 0.25 &&
            middleY < transition.EndY - 0.25;
    }

    private static (double MinY, double MaxY) PrimitiveBounds(
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> primitives)
    {
        return (
            primitives.Min(item => Math.Min(item.Start.Y, item.End.Y)),
            primitives.Max(item => Math.Max(item.Start.Y, item.End.Y)));
    }

    private static int ExpectedSectionPieceCount(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame,
        IReadOnlyList<FachwerkColumnBreak> breaks) =>
        ExpectedSectionPiecesByRole(profile, frame, breaks).Values.Sum();

    private static void AssertConnectedPath(string profileKey, FachwerkColumnPathDefinition path)
    {
        for (var index = 1; index < path.Primitives.Count; index++)
        {
            var gap = Distance(path.Primitives[index - 1].End, path.Primitives[index].Start);
            Require(gap <= 1.01,
                profileKey + "/" + path.Role + " has a " + gap.ToString("0.###", CultureInfo.InvariantCulture) + " mm chain gap");
        }
    }

    private static void AssertSamePrimitivePath(
        string label,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> first,
        IReadOnlyList<FachwerkColumnPrimitiveDefinition> second)
    {
        Require(first.Count == second.Count, label + " primitive counts differ");
        for (var index = 0; index < first.Count; index++)
        {
            Require(first[index].NormalizedKind() == second[index].NormalizedKind(),
                label + " primitive kinds differ at " + index.ToString(CultureInfo.InvariantCulture));
            Require(Distance(first[index].Start, second[index].Start) < 0.001,
                label + " starts differ at " + index.ToString(CultureInfo.InvariantCulture));
            Require(Distance(first[index].End, second[index].End) < 0.001,
                label + " ends differ at " + index.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int InspectComponent(Model model, string rawIdentifier)
    {
        if (!int.TryParse(rawIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var identifier))
            throw new InvalidOperationException("Component identifier must be an integer.");
        var component = model.SelectModelObject(new Identifier(identifier)) as Component;
        if (component == null)
            throw new InvalidOperationException("Component not found: " + rawIdentifier);

        Console.WriteLine("COMPONENT id=" + identifier + " name=" + component.Name);
        var input = component.GetComponentInput();
        var inputIndex = 0;
        foreach (var item in input)
        {
            inputIndex++;
            Console.WriteLine("INPUT[" + inputIndex + "] type=" + (item?.GetType().FullName ?? "null") + " value=" + (item ?? "null"));
            if (item == null) continue;
            foreach (var property in item.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                try
                {
                    var value = property.GetValue(item, null);
                    if (value is IEnumerable sequence && !(value is string))
                    {
                        var values = sequence.Cast<object>().Select(FormatInspectionValue);
                        Console.WriteLine("  " + property.Name + "=[" + string.Join(";", values) + "]");
                    }
                    else
                    {
                        Console.WriteLine("  " + property.Name + "=" + FormatInspectionValue(value));
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine("  " + property.Name + "=<" + exception.GetType().Name + ">");
                }
            }
        }
        return 0;
    }

    private static string FormatInspectionValue(object? value)
    {
        if (value is Point point)
            return point.X.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                point.Y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                point.Z.ToString("0.###", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
    }

    private static void CheckFrameContract()
    {
        var insertion = new Point(100, 200, 43330);
        var directionY = new Point(100, 1200, 43330);
        var frame = FachwerkColumnGeometry.ResolveFrame(insertion, directionY, 0);
        Require(Math.Abs(frame.AxisX.X) < 1e-9 && Math.Abs(frame.AxisX.Y - 1) < 1e-9,
            "second picked point must define local +X in plan");

        var corrected = FachwerkColumnGeometry.ResolveFrame(insertion, directionY, 90);
        Require(Math.Abs(corrected.AxisX.X + 1) < 1e-9 && Math.Abs(corrected.AxisX.Y) < 1e-9,
            "additional rotation must rotate the picked local +X direction");
    }

    private static void CheckFormRegistrationContract()
    {
        var directControls = typeof(FachwerkColumnForm)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => typeof(Control).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name)
            .ToArray();
        var editableAttributeNames = typeof(FachwerkColumnPluginData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Select(field => new
            {
                Field = field,
                Attribute = field.CustomAttributes.FirstOrDefault(attribute =>
                    string.Equals(
                        attribute.AttributeType.Name,
                        "StructuresFieldAttribute",
                        StringComparison.Ordinal)),
            })
            .Where(item =>
                item.Attribute != null &&
                !string.Equals(
                    item.Field.Name,
                    "ExternalObjectId",
                    StringComparison.Ordinal))
            .Select(item =>
                Convert.ToString(
                    item.Attribute.ConstructorArguments[0].Value,
                    CultureInfo.InvariantCulture) ?? string.Empty)
            .ToArray();
        var formAttributeCount = editableAttributeNames.Length;
        Require(
            directControls.Count(name => name.StartsWith("_attributeFk", StringComparison.Ordinal)) ==
            formAttributeCount,
            "Tekla 2020 requires one direct attribute Control field for every editable component attribute");
        Require(
            directControls.Count(name => name.StartsWith("_filterFk", StringComparison.Ordinal)) ==
            formAttributeCount,
            "Tekla 2020 requires one direct filter Control field for every editable component attribute");
        var attributeSuffixes = directControls
            .Where(name => name.StartsWith("_attributeFk", StringComparison.Ordinal))
            .Select(name => name.Substring("_attribute".Length))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var filterSuffixes = directControls
            .Where(name => name.StartsWith("_filterFk", StringComparison.Ordinal))
            .Select(name => name.Substring("_filter".Length))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(attributeSuffixes.SequenceEqual(filterSuffixes, StringComparer.Ordinal),
            "Every Tekla form attribute must have exactly one same-named filter");
        var expectedAttributeFields = editableAttributeNames
            .Select(name =>
                "_attribute" +
                FachwerkColumnForm.AttributeFieldSuffix(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var actualAttributeFields = directControls
            .Where(name => name.StartsWith("_attributeFk", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(
            expectedAttributeFields.SequenceEqual(actualAttributeFields, StringComparer.Ordinal),
            "every editable StructuresField must map to the exact direct Tekla form field");
        Require(
            attributeSuffixes.Count(name =>
                name.StartsWith("FkBreak", StringComparison.Ordinal) &&
                (name.EndsWith("FlLo", StringComparison.Ordinal) ||
                 name.EndsWith("FlHi", StringComparison.Ordinal) ||
                 name.EndsWith("WebLo", StringComparison.Ordinal) ||
                 name.EndsWith("WebHi", StringComparison.Ordinal))) == 32,
            "all 32 joint offsets must be ordinary direct Tekla form attributes");
        Require(
            typeof(FachwerkColumnForm).GetField(
                "_jointOffsetRows",
                BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "joint offsets must not use a second local form state");
        Require(FachwerkColumnForm.ShouldTransferAttribute(false, false),
            "an unfiltered form value must still transfer");
        Require(FachwerkColumnForm.ShouldTransferAttribute(true, true),
            "a checked Tekla filter must transfer its value");
        Require(!FachwerkColumnForm.ShouldTransferAttribute(true, false),
            "an unchecked Tekla filter must not parse or transfer its value");
        var unexpectedDirectControls = directControls
            .Where(name =>
                !name.StartsWith("_attributeFk", StringComparison.Ordinal) &&
                !name.StartsWith("_filterFk", StringComparison.Ordinal) &&
                !string.Equals(name, "_hiddenBindingsPanel", StringComparison.Ordinal) &&
                !string.Equals(name, "_createBevelsCombo", StringComparison.Ordinal) &&
                !string.Equals(name, "_createSplitsCombo", StringComparison.Ordinal))
            .ToArray();
        Require(unexpectedDirectControls.Length == 0,
            "Tagged storage controls must not be exposed through duplicate direct Control fields: " +
            string.Join(", ", unexpectedDirectControls));
    }

    private static void CheckRigelConnectionContract()
    {
        var usedInputIds = new HashSet<int> { 1 };
        FachwerkColumnRigelConnectionPlugin.ValidatePickedSectionIds(
            new[] { 2, 3, 4, 5 },
            usedInputIds,
            "верхней");
        Require(
            usedInputIds.SetEquals(new[] { 1, 2, 3, 4, 5 }),
            "group picker must retain the rigel and four upper part inputs");
        bool duplicateSectionRejected = false;
        try
        {
            FachwerkColumnRigelConnectionPlugin.ValidatePickedSectionIds(
                new[] { 6, 7, 8, 2 },
                usedInputIds,
                "нижней");
        }
        catch (InvalidOperationException)
        {
            duplicateSectionRejected = true;
        }
        Require(
            duplicateSectionRejected,
            "group picker must reject a part reused by another section");
        bool wrongGroupSizeRejected = false;
        try
        {
            FachwerkColumnRigelConnectionPlugin.ValidatePickedSectionIds(
                new[] { 6, 7, 8 },
                usedInputIds,
                "нижней");
        }
        catch (InvalidOperationException)
        {
            wrongGroupSizeRejected = true;
        }
        Require(
            wrongGroupSizeRejected,
            "group picker must require exactly four parts per section");

        var data = new FachwerkColumnRigelConnectionPluginData
        {
            BevelAngle = 35,
            BevelRootFace = 3,
        };
        foreach (string role in new[]
                 {
                     "outer-flange",
                     "inner-flange",
                     "outer-web",
                     "inner-web",
                 })
        {
            FachwerkRigelConnectionBevelParameters inherited =
                FachwerkColumnRigelConnectionPlugin.ResolveBevelParameters(
                    data,
                    role);
            Require(
                Math.Abs(inherited.AngleDeg - 35) < 1e-9 &&
                Math.Abs(inherited.RootFace - 3) < 1e-9,
                "legacy rigel connection must keep the common bevel pair for " +
                role);
        }

        data.InnerFlangeBevelAngle = 41;
        data.InnerFlangeBevelRootFace = 4;
        data.LeftWebBevelAngle = 42;
        data.LeftWebBevelRootFace = 5;
        data.RightWebBevelAngle = 43;
        data.RightWebBevelRootFace = 6;
        AssertRigelBevel(data, "outer-flange", 35, 3);
        AssertRigelBevel(data, "inner-flange", 41, 4);
        AssertRigelBevel(data, "outer-web", 42, 5);
        AssertRigelBevel(data, "inner-web", 43, 6);
        IReadOnlyList<FachwerkBevelCutterProfilePoint> cutterProfile =
            FachwerkColumnRigelConnectionTeklaAdapter.BuildCutterProfile(
                25,
                40,
                2);
        Require(
            cutterProfile.Count == 4,
            "rigel bevel cutter must have one continuous working edge");
        double workingAlong =
            cutterProfile[3].Along - cutterProfile[0].Along;
        double workingOutward =
            cutterProfile[3].Outward - cutterProfile[0].Outward;
        double actualWorkingLength = Math.Sqrt(
            workingAlong * workingAlong +
            workingOutward * workingOutward);
        double nominalRemovedThickness = 23;
        double nominalRun =
            nominalRemovedThickness *
            Math.Tan(40 * Math.PI / 180.0);
        double nominalWorkingLength = Math.Sqrt(
            nominalRun * nominalRun +
            nominalRemovedThickness * nominalRemovedThickness);
        Require(
            Math.Abs(actualWorkingLength - 3 * nominalWorkingLength) < 1e-9,
            "rigel bevel working edge must be extended to three nominal lengths");
        Require(
            Math.Abs(
                workingAlong / workingOutward -
                nominalRun / nominalRemovedThickness) < 1e-9,
            "extended rigel bevel working edge must preserve the requested angle");

        var dataFields = typeof(FachwerkColumnRigelConnectionPluginData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(field => field.CustomAttributes)
            .Where(attribute =>
                string.Equals(
                    attribute.AttributeType.Name,
                    "StructuresFieldAttribute",
                    StringComparison.Ordinal))
            .Select(attribute =>
                Convert.ToString(
                    attribute.ConstructorArguments[0].Value,
                    CultureInfo.InvariantCulture) ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var directControls = typeof(FachwerkColumnRigelConnectionForm)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => typeof(Control).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name)
            .ToArray();
        Require(
            directControls.Count(name =>
                name.StartsWith("_attributeFkrc", StringComparison.Ordinal)) ==
            dataFields.Length,
            "rigel connection form must register every component attribute");
        Require(
            directControls.Count(name =>
                name.StartsWith("_filterFkrc", StringComparison.Ordinal)) ==
            dataFields.Length,
            "rigel connection form must register a filter for every attribute");
        string[] attributeSuffixes = directControls
            .Where(name =>
                name.StartsWith("_attributeFkrc", StringComparison.Ordinal))
            .Select(name => name.Substring("_attribute".Length))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] filterSuffixes = directControls
            .Where(name =>
                name.StartsWith("_filterFkrc", StringComparison.Ordinal))
            .Select(name => name.Substring("_filter".Length))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(
            attributeSuffixes.SequenceEqual(
                filterSuffixes,
                StringComparer.Ordinal),
            "rigel connection attributes and filters must be paired");
    }

    private static void AssertRigelBevel(
        FachwerkColumnRigelConnectionPluginData data,
        string role,
        double expectedAngle,
        double expectedRoot)
    {
        FachwerkRigelConnectionBevelParameters actual =
            FachwerkColumnRigelConnectionPlugin.ResolveBevelParameters(
                data,
                role);
        Require(
            Math.Abs(actual.AngleDeg - expectedAngle) < 1e-9 &&
            Math.Abs(actual.RootFace - expectedRoot) < 1e-9,
            role + " must receive its own bevel angle and root face");
    }

    private static void CheckRigelInsertConnectionContract()
    {
        Require(
            FachwerkColumnRigelInsertConnectionGeometry.NormalizeSelectedWebRole(
                "LEFT") == "outer-web",
            "left insert side must resolve to the semantic left web");
        Require(
            FachwerkColumnRigelInsertConnectionGeometry.NormalizeSelectedWebRole(
                "RIGHT") == "inner-web",
            "right insert side must resolve to the semantic right web");
        Require(
            FachwerkColumnRigelInsertConnectionGeometry.NormalizeControlFlangeRole(
                "INNER") == "inner-flange",
            "inner control side must resolve to the inner flange");
        Require(
            FachwerkColumnRigelInsertConnectionGeometry.NormalizeControlFlangeRole(
                "OUTER") == "outer-flange",
            "outer control side must resolve to the outer flange");
        RequireNear(
            FachwerkColumnRigelInsertConnectionGeometry.LongitudinalRootFace(25),
            13,
            "longitudinal web bevel must keep a 13 mm root face on PL25");
        RequireNear(
            FachwerkColumnRigelInsertConnectionGeometry.LongitudinalRootFace(30),
            13,
            "longitudinal web bevel root face must remain 13 mm for thicker plates");
        FachwerkRigelInsertBevelSpec longitudinalBevel =
            FachwerkColumnRigelInsertConnectionGeometry.BuildLongitudinalBevel(
                "vertical-web-test",
                "outer-web",
                null,
                new Point(0, 0, 0),
                new Point(0, 500, 0),
                new Vector(1, 0, 0),
                new Vector(0, 0, 1),
                25);
        RequireNear(
            longitudinalBevel.Section.Anchor.Z,
            12.5,
            "vertical bevel cutter must be based on the plate center plane");
        RequireNear(
            longitudinalBevel.Section.CrossSpan,
            500,
            "vertical bevel must keep its exact calculated height");
        RequireNear(
            longitudinalBevel.Section.CrossOverrun,
            0,
            "vertical bevel must not extend beyond its calculated end planes");
        FachwerkColumnRigelInsertConnectionGeometry.ParsePlateDimensions(
            "PL25*350",
            out double parsedThickness,
            out double parsedWidth);
        RequireNear(parsedThickness, 25, "rigel insert plate thickness");
        RequireNear(parsedWidth, 350, "rigel insert plate width");

        Point bevelEdgeStart = new Point(10, 20, 30);
        Point bevelEdgeEnd = new Point(110, 70, 30);
        double bevelEdgeLength = Math.Sqrt(12500);
        Vector expectedBevelAxis = new Vector(
            100 / bevelEdgeLength,
            50 / bevelEdgeLength,
            0);
        Vector expectedIntoPart = new Vector(
            expectedBevelAxis.Y,
            -expectedBevelAxis.X,
            0);
        Point bevelAnchor = new Point(60, 45, 30);
        Point bevelInterior = new Point(
            bevelAnchor.X + 100 * expectedIntoPart.X,
            bevelAnchor.Y + 100 * expectedIntoPart.Y,
            bevelAnchor.Z);
        FachwerkBevelSection transverseBevel =
            FachwerkColumnRigelInsertConnectionGeometry.BuildTransverseBevel(
                "sloped-contact",
                "outer-flange",
                null,
                bevelEdgeStart,
                bevelEdgeEnd,
                bevelInterior,
                new Vector(0, 0, 1),
                50);
        RequireNear(transverseBevel.Anchor.X, bevelAnchor.X,
            "rigel insert bevel anchor X");
        RequireNear(transverseBevel.Anchor.Y, bevelAnchor.Y,
            "rigel insert bevel anchor Y");
        RequireNear(transverseBevel.Anchor.Z, bevelAnchor.Z + 25,
            "rigel insert bevel cutter anchor must use the plate center plane");
        RequireNear(transverseBevel.CrossSpan, bevelEdgeLength,
            "rigel insert bevel must span the real sloped contact edge");
        RequireNear(transverseBevel.CrossAxis.X, expectedBevelAxis.X,
            "rigel insert bevel axis X");
        RequireNear(transverseBevel.CrossAxis.Y, expectedBevelAxis.Y,
            "rigel insert bevel axis Y");
        Require(
            transverseBevel.IntoLowerSection.X * expectedIntoPart.X +
            transverseBevel.IntoLowerSection.Y * expectedIntoPart.Y > 0.999,
            "rigel insert bevel must point from the contact edge into the part");

        double rigelTilt = 17 * Math.PI / 180.0;
        Vector rigelReferenceDirection = new Vector(
            Math.Sin(rigelTilt),
            0,
            Math.Cos(rigelTilt));
        Vector rigelReferenceNormal = new Vector(
            Math.Cos(rigelTilt),
            0,
            -Math.Sin(rigelTilt));
        var upperRigelBevel = new FachwerkBevelSection(
            "upper-rigel-plane-angle",
            "outer-flange",
            null,
            new Point(0, 0, 12.5),
            new Vector(1, 0, 0),
            new Vector(0, 0, 1),
            new Vector(0, 1, 0),
            25,
            180,
            100,
            rigelReferenceNormal);
        IReadOnlyList<Point> upperRigelCutter =
            FachwerkColumnRigelConnectionTeklaAdapter
                .BuildPlaneReferencedCutterBoundary(
                    upperRigelBevel,
                    40,
                    2);
        CheckRigelPlaneReferencedBevel(
            upperRigelCutter,
            rigelReferenceDirection,
            rigelReferenceNormal,
            new Vector(0, 0, 1),
            2,
            "upper rigel contact bevel");

        Vector lowerRigelReferenceDirection = new Vector(
            -Math.Sin(rigelTilt),
            0,
            -Math.Cos(rigelTilt));
        Vector lowerRigelReferenceNormal = new Vector(
            Math.Cos(rigelTilt),
            0,
            -Math.Sin(rigelTilt));
        var lowerRigelBevel = new FachwerkBevelSection(
            "lower-rigel-plane-angle",
            "inner-flange",
            null,
            new Point(0, 0, -12.5),
            new Vector(-1, 0, 0),
            new Vector(0, 0, -1),
            new Vector(0, -1, 0),
            25,
            180,
            100,
            lowerRigelReferenceNormal);
        IReadOnlyList<Point> lowerRigelCutter =
            FachwerkColumnRigelConnectionTeklaAdapter
                .BuildPlaneReferencedCutterBoundary(
                    lowerRigelBevel,
                    40,
                    2);
        CheckRigelPlaneReferencedBevel(
            lowerRigelCutter,
            lowerRigelReferenceDirection,
            lowerRigelReferenceNormal,
            new Vector(0, 0, -1),
            2,
            "lower rigel contact bevel");

        var slopedRigelPlane = new FachwerkRigelFacePlane(
            new Point(0, 0, 0),
            new Vector(0, 1, 0),
            new Vector(-1, 0, 1),
            new Vector(1 / Math.Sqrt(2), 0, 1 / Math.Sqrt(2)),
            Array.Empty<Point>());
        var innerFaceFrame = new FachwerkRigelInsertPartFrame(
            null,
            new Point(0, 0, 0),
            new Vector(1, 0, 0),
            new Vector(0, 0, 1),
            new Vector(0, -1, 0),
            50,
            180);
        Point innerFaceContact =
            FachwerkColumnRigelInsertConnectionGeometry.InnerFaceRailPlaneIntersection(
                innerFaceFrame,
                slopedRigelPlane,
                -1);
        RequireNear(innerFaceContact.X, 25,
            "rigel insert contact must be solved on the inner face rail");
        RequireNear(innerFaceContact.Y, 90,
            "rigel insert contact must preserve the requested side rail");
        RequireNear(innerFaceContact.Z, -25,
            "rigel insert handle must lie on the inner face");
        RequireNear(
            (innerFaceContact.X - slopedRigelPlane.Origin.X) *
            slopedRigelPlane.Normal.X +
            (innerFaceContact.Y - slopedRigelPlane.Origin.Y) *
            slopedRigelPlane.Normal.Y +
            (innerFaceContact.Z - slopedRigelPlane.Origin.Z) *
            slopedRigelPlane.Normal.Z,
            0,
            "rigel insert contact handle must remain in the rigel face plane");

        var data = new FachwerkColumnRigelInsertConnectionPluginData
        {
            InsertFlangeAngle = 35,
            PartFlangeAngle = 36,
            InsertWebAngle = 37,
            PartWebAngle = 38,
            DirectWebAngle = 39,
            RootFace = 3,
            UpperOuterFlangeDepth = "FRONT",
            UpperInnerFlangeDepth = "MIDDLE",
            UpperWebDepth = "BEHIND",
            LowerOuterFlangeDepth = "BEHIND",
            LowerInnerFlangeDepth = "FRONT",
            LowerWebDepth = "MIDDLE",
        };
        FachwerkRigelInsertBevelParameters parameters =
            FachwerkColumnRigelInsertConnectionPlugin.ResolveBevelParameters(data);
        RequireNear(parameters.Angle(FachwerkRigelInsertBevelKind.InsertFlange), 35,
            "insert flange bevel angle");
        RequireNear(parameters.Angle(FachwerkRigelInsertBevelKind.PartFlange), 36,
            "part flange bevel angle");
        RequireNear(parameters.Angle(FachwerkRigelInsertBevelKind.InsertWeb), 37,
            "insert web bevel angle");
        RequireNear(parameters.Angle(FachwerkRigelInsertBevelKind.PartWeb), 38,
            "part web bevel angle");
        RequireNear(parameters.Angle(FachwerkRigelInsertBevelKind.DirectWeb), 39,
            "direct web bevel angle");
        RequireNear(parameters.RootFace, 3, "rigel insert root face");
        FachwerkRigelInsertDepthModes depthModes =
            FachwerkColumnRigelInsertConnectionPlugin.ResolveDepthModes(data);
        Require(
            depthModes.For("upper", "outer-flange") ==
            FachwerkRigelInsertDepthMode.Front &&
            depthModes.For("upper", "inner-flange") ==
            FachwerkRigelInsertDepthMode.Middle &&
            depthModes.For("upper", "outer-web") ==
            FachwerkRigelInsertDepthMode.Behind &&
            depthModes.For("lower", "outer-flange") ==
            FachwerkRigelInsertDepthMode.Behind &&
            depthModes.For("lower", "inner-flange") ==
            FachwerkRigelInsertDepthMode.Front &&
            depthModes.For("lower", "inner-web") ==
            FachwerkRigelInsertDepthMode.Middle,
            "every upper and lower insert must keep its own depth mode");

        string[] fields = typeof(FachwerkColumnRigelInsertConnectionPluginData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(field => field.CustomAttributes)
            .Where(attribute => string.Equals(
                attribute.AttributeType.Name,
                "StructuresFieldAttribute",
                StringComparison.Ordinal))
            .Select(attribute => Convert.ToString(
                attribute.ConstructorArguments[0].Value,
                CultureInfo.InvariantCulture) ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(fields.Length == 18, "rigel insert component field count");
        Require(fields.All(name => name.Length <= 19),
            "rigel insert fields must satisfy Tekla 2020's 19-character limit");

        string[] controls = typeof(FachwerkColumnRigelInsertConnectionForm)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => typeof(Control).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name)
            .ToArray();
        Require(
            controls.Count(name =>
                name.StartsWith("_attributeFkri", StringComparison.Ordinal)) ==
            fields.Length,
            "rigel insert form must register every component attribute");
        Require(
            controls.Count(name =>
                name.StartsWith("_filterFkri", StringComparison.Ordinal)) ==
            fields.Length,
            "rigel insert form must register every attribute filter");

        var comboStorage = new TextBox();
        FachwerkColumnRigelInsertConnectionForm.AssignComboStorageText(
            comboStorage,
            "RIGHT");
        Require(
            comboStorage.Text == "RIGHT",
            "rigel insert combo must update the Tekla attribute storage");
        FachwerkColumnRigelInsertConnectionForm.AssignComboStorageText(
            comboStorage,
            "BEHIND");
        Require(
            comboStorage.Text == "BEHIND",
            "rigel insert depth combo must update the Tekla attribute storage");

        Require(
            FachwerkColumnRigelInsertConnectionTeklaAdapter.InsertPrefix ==
            "515-60.ВС-" &&
            FachwerkColumnRigelInsertConnectionTeklaAdapter.Material ==
            "C355-5" &&
            FachwerkColumnRigelInsertConnectionTeklaAdapter.InsertClass == "7" &&
            FachwerkColumnRigelInsertConnectionTeklaAdapter.InsertDepth ==
            Position.DepthEnum.MIDDLE,
            "rigel insert fabrication attributes");
        var clockwiseBoundary = new[]
        {
            new Point(0, 0, 0),
            new Point(100, 0, 0),
            new Point(100, 50, 0),
            new Point(0, 50, 0),
        };
        var counterClockwiseBoundary = clockwiseBoundary.Reverse().ToArray();
        double clockwiseOffset =
            FachwerkColumnRigelInsertConnectionTeklaAdapter.ResolveInsertDepthOffset(
                clockwiseBoundary,
                new Vector(0, 0, 1),
                50);
        double counterClockwiseOffset =
            FachwerkColumnRigelInsertConnectionTeklaAdapter.ResolveInsertDepthOffset(
                counterClockwiseBoundary,
                new Vector(0, 0, 1),
                50);
        RequireNear(clockwiseOffset, 25,
            "rigel insert depth along outward contour normal");
        RequireNear(counterClockwiseOffset, -25,
            "rigel insert depth must compensate for reversed contour winding");
        FachwerkColumnRigelInsertConnectionTeklaAdapter.ResolveInsertDepth(
            clockwiseBoundary,
            new Vector(0, 0, 1),
            50,
            FachwerkRigelInsertDepthMode.Front,
            out Position.DepthEnum frontDepth,
            out double frontOffset);
        Require(
            frontDepth == Position.DepthEnum.FRONT &&
            Math.Abs(frontOffset) < 1e-9,
            "manual front alignment must not inherit the automatic offset");
        FachwerkColumnRigelInsertConnectionTeklaAdapter.ResolveInsertDepth(
            clockwiseBoundary,
            new Vector(0, 0, 1),
            50,
            FachwerkRigelInsertDepthMode.Behind,
            out Position.DepthEnum behindDepth,
            out double behindOffset);
        Require(
            behindDepth == Position.DepthEnum.BEHIND &&
            Math.Abs(behindOffset) < 1e-9,
            "manual behind alignment must not inherit the automatic offset");
        var exactLongitudinalBevel = new FachwerkBevelSection(
            "longitudinal-test",
            "outer-web",
            null,
            new Point(0, 0, 0),
            new Vector(1, 0, 0),
            new Vector(0, 0, 1),
            new Vector(0, 1, 0),
            25,
            500,
            0);
        RequireNear(
            exactLongitudinalBevel.CrossOverrun,
            0,
            "vertical web bevel must end at its exact calculated planes");
    }

    private static void CheckFormValueContract()
    {
        Require(FachwerkColumnForm.NormalizeFieldText("Double", string.Empty, "0") == "0",
            "blank Tekla double must fall back to its UI default");
        Require(FachwerkColumnForm.NormalizeFieldText("Double", int.MinValue.ToString(CultureInfo.InvariantCulture), "0") == "0",
            "Tekla unset double sentinel must fall back to its UI default");
        Require(FachwerkColumnForm.NormalizeFieldText("Double", "-15,5", "0") == "-15.5",
            "localized valid double must be normalized without changing its value");
        Require(FachwerkColumnForm.NormalizeFieldText("String", string.Empty, "C355-5") == "C355-5",
            "blank required string must fall back to its UI default");
        Require(FachwerkColumnForm.NormalizeFieldText("String", "custom", "default") == "custom",
            "valid string must survive UI normalization");
        RequireNear(
            FachwerkColumnForm.ResolveAutomaticStiffenerSpacing(string.Empty, "1500", false),
            1500,
            "an unset legacy automatic-stiffener spacing must use the UI default while the form loads");
        RequireNear(
            FachwerkColumnForm.ResolveAutomaticStiffenerSpacing("1250", "1500", true),
            1250,
            "an explicitly entered positive automatic-stiffener spacing must survive validation");
        var invalidAutomaticSpacingRejected = false;
        try
        {
            FachwerkColumnForm.ResolveAutomaticStiffenerSpacing("0", "1500", true);
        }
        catch (InvalidOperationException)
        {
            invalidAutomaticSpacingRejected = true;
        }
        Require(
            invalidAutomaticSpacingRejected,
            "Apply and Modify must still reject a non-positive automatic-stiffener spacing");
        Require(
            !FachwerkColumnForm.HasLegacyJointOffsetStorage(
                string.Empty,
                null,
                " ",
                string.Empty),
            "empty legacy storage must never overwrite native direct offset controls");
        Require(
            FachwerkColumnForm.HasLegacyJointOffsetStorage(
                "0;0;-75;0;0;0;0;0",
                string.Empty,
                string.Empty,
                string.Empty),
            "a persisted legacy offset vector must still be eligible for one-time migration");
        Require(
            FachwerkColumnForm.UsesTeklaBoundValueWithoutSecondRead(
                "fk_break_3_web_hi"),
            "direct joint offsets must keep the value already loaded by Tekla");
        Require(
            !FachwerkColumnForm.UsesTeklaBoundValueWithoutSecondRead(
                "fk_profile_key") &&
            !FachwerkColumnForm.UsesTeklaBoundValueWithoutSecondRead(
                "fk_stiff_enabled") &&
            !FachwerkColumnForm.UsesTeklaBoundValueWithoutSecondRead(
                "fk_stiff_inner_gap"),
            "ordinary and derived component attributes must be read from the selected instance");
    }

    private static void CheckInpAttributeContract()
    {
        var inpPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "cad",
            "tekla",
            "bridge-plugin",
            "deploy",
            "component",
            "FachwerkColumnPlugin.inp");
        Require(File.Exists(inpPath), "FachwerkColumnPlugin.inp was not found: " + inpPath);
        var inp = File.ReadAllText(inpPath);
        var missing = typeof(FachwerkColumnPluginData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(field => field.CustomAttributes)
            .Where(attribute =>
                string.Equals(
                    attribute.AttributeType.Name,
                    "StructuresFieldAttribute",
                    StringComparison.Ordinal))
            .Select(attribute =>
                Convert.ToString(
                    attribute.ConstructorArguments[0].Value,
                    CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(name =>
                inp.IndexOf(
                    "attribute(\"" + name + "\"",
                    StringComparison.Ordinal) < 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(
            missing.Length == 0,
            "FachwerkColumnPlugin.inp is missing persistent component attributes: " +
            string.Join(", ", missing));
    }

    private static void CheckBreakAttributeContract()
    {
        var data = new FachwerkColumnPluginData();
        Require(FachwerkColumnGeometry.ReadBreaks(data).Count == 0,
            "zero elevations must not create splitting");

        data.Break1Elevation = 25000;
        data.Break1Mode = "ALL_FOUR";
        data.FlangeLowerOffsets = "-10;0;0;0;0;0;0;0";
        data.FlangeUpperOffsets = "20;0;0;0;0;0;0;0";
        data.WebLowerOffsets = "-30;0;0;0;0;0;0;0";
        data.WebUpperOffsets = "40;0;0;0;0;0;0;0";
        var enabled = FachwerkColumnGeometry.ReadBreaks(data);
        Require(enabled.Count == 1 && Math.Abs(enabled[0].Elevation - 25000) < 1e-9,
            "non-zero elevation must pass the absolute split level to geometry");
        Require(
            Math.Abs(enabled[0].FlangeLowerOffset + 10) < 1e-9 &&
            Math.Abs(enabled[0].FlangeUpperOffset - 20) < 1e-9 &&
            Math.Abs(enabled[0].WebLowerOffset + 30) < 1e-9 &&
            Math.Abs(enabled[0].WebUpperOffset - 40) < 1e-9,
            "legacy packed joint offsets must remain readable");

        data.OffsetSchema = FachwerkColumnGeometry.DirectOffsetSchema;
        data.Break1FlangeLowerOffset = -11;
        data.Break1FlangeUpperOffset = 21;
        data.Break1WebLowerOffset = -31;
        data.Break1WebUpperOffset = 41;
        var direct = FachwerkColumnGeometry.ReadBreaks(data);
        Require(
            direct.Count == 1 &&
            Math.Abs(direct[0].FlangeLowerOffset + 11) < 1e-9 &&
            Math.Abs(direct[0].FlangeUpperOffset - 21) < 1e-9 &&
            Math.Abs(direct[0].WebLowerOffset + 31) < 1e-9 &&
            Math.Abs(direct[0].WebUpperOffset - 41) < 1e-9,
            "direct Tekla joint-offset attributes must override legacy packed strings");

        data.Break1FlangeLowerOffset = 0;
        data.Break1FlangeUpperOffset = 0;
        data.Break1WebLowerOffset = 0;
        data.Break1WebUpperOffset = 0;
        var directZeros = FachwerkColumnGeometry.ReadBreaks(data);
        Require(
            directZeros.Count == 1 &&
            Math.Abs(directZeros[0].FlangeLowerOffset) < 1e-9 &&
            Math.Abs(directZeros[0].FlangeUpperOffset) < 1e-9 &&
            Math.Abs(directZeros[0].WebLowerOffset) < 1e-9 &&
            Math.Abs(directZeros[0].WebUpperOffset) < 1e-9,
            "explicit direct zeros must not fall back to stale legacy offsets");

        var serialized = FachwerkColumnGeometry.SerializeOffsetValues(
            new[] { -10d, 20d, -30d, 40d });
        var parsed = FachwerkColumnGeometry.ParseOffsetValues(serialized);
        Require(
            parsed.Length == 8 &&
            Math.Abs(parsed[0] + 10) < 1e-9 &&
            Math.Abs(parsed[1] - 20) < 1e-9 &&
            Math.Abs(parsed[2] + 30) < 1e-9 &&
            Math.Abs(parsed[3] - 40) < 1e-9,
            "offset serialization must preserve values and normalize to eight joints");
        Require(
            FachwerkColumnGeometry.SerializeOffsetValues(new double[8]) == string.Empty,
            "legacy zero-offset components must keep an empty persisted value");

        var unset = new FachwerkColumnPluginData
        {
            Break1Elevation = int.MinValue,
            Break1Mode = "ALL_FOUR",
        };
        Require(FachwerkColumnGeometry.ReadBreaks(unset).Count == 0,
            "Tekla unset sentinel must not create splitting");
    }

    private static void CheckStiffenerContract(
        FachwerkColumnProfileDefinition profile,
        FachwerkColumnFrame frame)
    {
        var disabled = FachwerkColumnGeometry.ReadStiffenerSettings(
            new FachwerkColumnPluginData
            {
                StiffenerEnabled = "NO",
            });
        Require(!disabled.Enabled && disabled.Elevations.Count == 0,
            "legacy components must keep independent stiffeners disabled");

        var defaults = FachwerkColumnGeometry.ReadStiffenerSettings(
            new FachwerkColumnPluginData());
        Require(
            defaults.Enabled &&
            defaults.Profile == "PL8" &&
            Math.Abs(defaults.ChamferSize - 20) < 1e-9 &&
            defaults.IncrementalDistances.Count == 0 &&
            Math.Abs(defaults.InnerFlangeGap - 20) < 1e-9 &&
            !defaults.AlternateFlangeGap &&
            defaults.PlacementMode ==
                FachwerkColumnStiffenerPlacementMode.Elevations,
            "new components must enable stiffeners with PL8, 20 mm chamfers, 20 mm inner gap, no alternation and elevation mode");
        Require(
            Math.Abs(new FachwerkColumnPluginData().StiffenerSpacing - 1500) < 1e-9 &&
            new FachwerkColumnPluginData().StiffenerDistances == "1500",
            "new components must expose a 1500 mm default stiffener spacing");
        Require(
            string.Equals(
                new FachwerkColumnPluginData().BevelProfile,
                "TRI_A14*14",
                StringComparison.Ordinal),
            "new components must enable TRI_A14*14 web bevels by default");
        var legacyDefaults = FachwerkColumnGeometry.ReadStiffenerSettings(
            new FachwerkColumnPluginData
            {
                StiffenerEnabled = "YES",
                StiffenerPlacementMode = "ELEVATIONS",
                StiffenerSpacing = 0,
                StiffenerElevations = "12000",
            });
        Require(
            legacyDefaults.IncrementalDistances.Count == 0,
            "elevation components must ignore path-distance fields");
        var parsedDistances =
            FachwerkColumnGeometry.ParseStiffenerDistances(
                "1000 2500;500",
                0);
        Require(
            parsedDistances.SequenceEqual(
                new[] { 1000d, 2500d, 500d }),
            "path-distance mode must preserve each finite incremental distance");
        var automaticSettings = FachwerkColumnGeometry.ReadStiffenerSettings(
            new FachwerkColumnPluginData
            {
                StiffenerPlacementMode = "AUTO",
                StiffenerSpacing = 1500,
                StiffenerDistances = "100 200",
            });
        Require(
            automaticSettings.PlacementMode ==
                FachwerkColumnStiffenerPlacementMode.Automatic &&
            automaticSettings.IncrementalDistances.SequenceEqual(
                new[] { 1500d }),
            "automatic placement must use the auto spacing instead of stale manual distances");
        var automaticDistances =
            FachwerkColumnGeometry.BuildAutomaticStiffenerDistances(
                10000,
                new[] { 4200d, 7000d },
                1500);
        Require(
            automaticDistances.SequenceEqual(
                new[] { 1500d, 1500d, 2700d, 2800d }),
            "automatic placement must restart at virtual ribs and serialize only real rib distances");
        Require(
            FachwerkColumnGeometry.SerializeStiffenerDistances(
                automaticDistances) == "1500 1500 2700 2800",
            "automatic distances must serialize as an editable space-separated sequence");
        var balancedShortRemainder =
            FachwerkColumnGeometry.BuildAutomaticStiffenerDistances(
                3900,
                Array.Empty<double>(),
                1500);
        Require(
            balancedShortRemainder.SequenceEqual(
                new[] { 1500d, 1200d }),
            "an end gap below 1000 mm must share its length with the preceding spacing interval");
        var legacyDistance =
            FachwerkColumnGeometry.ParseStiffenerDistances(
                string.Empty,
                1250);
        Require(
            legacyDistance.Count == 1 &&
            Math.Abs(legacyDistance[0] - 1250) < 1e-9,
            "one legacy spacing must migrate to exactly one path distance");

        var manyLevels = FachwerkColumnGeometry.ParseStiffenerElevations(
            "1000;2000;3000;4000;5000;6000;7000;8000;9000;10000");
        Require(manyLevels.Count == 10,
            "independent stiffener elevations must not have an eight-level limit");

        var sourceLine = profile.RequirePath("outer-flange")
            .Primitives
            .First(item =>
                item.NormalizedKind() == "line" &&
                Math.Abs(item.End.Y - item.Start.Y) > 500 &&
                Math.Max(item.Start.Y, item.End.Y) <
                    profile.SectionTransition.StartY - 1);
        var localElevation = (sourceLine.Start.Y + sourceLine.End.Y) * 0.5;
        var absoluteElevation = frame.GlobalInsertionZ + localElevation;
        var automaticWithoutUserBreaks =
            FachwerkColumnGeometry.CalculateAutomaticStiffenerDistances(
                profile,
                frame,
                Array.Empty<FachwerkColumnBreak>(),
                1500);
        var automaticWithErectionBreak =
            FachwerkColumnGeometry.CalculateAutomaticStiffenerDistances(
                profile,
                frame,
                new[]
                {
                    new FachwerkColumnBreak(
                        absoluteElevation,
                        FachwerkColumnBreak.ErectionSpliceMode,
                        1),
                },
                1500);
        Require(
            automaticWithoutUserBreaks.SequenceEqual(
                automaticWithErectionBreak),
            "erection splices must not reset automatic stiffener spacing");
        var automaticWithPerpendicularBreak =
            FachwerkColumnGeometry.CalculateAutomaticStiffenerDistances(
                profile,
                frame,
                new[]
                {
                    new FachwerkColumnBreak(
                        absoluteElevation,
                        FachwerkColumnBreak.AllFourMode,
                        1),
                },
                1500);
        Require(
            !automaticWithoutUserBreaks.SequenceEqual(
                automaticWithPerpendicularBreak),
            "perpendicular joints must reset automatic stiffener spacing");
        var stiffenerReference = new FachwerkColumnBreak(
            absoluteElevation,
            FachwerkColumnBreak.StiffenerReferenceMode,
            1);
        Require(
            stiffenerReference.IsStiffenerReference(),
            "the stiffener-reference break mode must survive normalization");
        var automaticWithStiffenerReference =
            FachwerkColumnGeometry.CalculateAutomaticStiffenerDistances(
                profile,
                frame,
                new[] { stiffenerReference },
                1500);
        Require(
            automaticWithStiffenerReference.SequenceEqual(
                automaticWithPerpendicularBreak),
            "stiffener-reference points must reset automatic spacing like perpendicular joints");
        var sectionWithoutStiffenerReference =
            FachwerkColumnGeometry.BuildSectionParts(
                profile,
                frame,
                Array.Empty<FachwerkColumnBreak>());
        var sectionWithStiffenerReference =
            FachwerkColumnGeometry.BuildSectionParts(
                profile,
                frame,
                new[] { stiffenerReference });
        Require(
            sectionWithStiffenerReference.Count ==
                sectionWithoutStiffenerReference.Count &&
            sectionWithStiffenerReference
                .GroupBy(item => item.Role)
                .OrderBy(item => item.Key)
                .Select(item => item.Count())
                .SequenceEqual(
                    sectionWithoutStiffenerReference
                        .GroupBy(item => item.Role)
                        .OrderBy(item => item.Key)
                        .Select(item => item.Count())),
            "stiffener-reference points must not split physical column parts");
        var settings = new FachwerkColumnStiffenerSettings(
            true,
            "PL20",
            "C355-5",
            15,
            new[] { absoluteElevation });
        var specs = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>(),
            settings);
        Require(specs.Count == 1,
            "one independent elevation must create one stiffener spec");
        var spec = specs[0];
        Require(
            spec.Profile == "PL20" &&
            spec.Material == "C355-5" &&
            Math.Abs(spec.ChamferSize - 15) < 1e-9,
            "stiffener profile, material and chamfer must survive the contract");
        Require(spec.Contour.Count == 4,
            "stiffener must have a four-corner inner-box contour");
        Require(spec.Contour.Count(item => item.HasChamfer) == 2,
            "stiffener must chamfer exactly two outer-flange-side corners");
        Require(
            Math.Abs(spec.Contour[0].Z + 65) < 1e-9 &&
            Math.Abs(spec.Contour[1].Z + 65) < 1e-9 &&
            Math.Abs(spec.Contour[2].Z - 65) < 1e-9 &&
            Math.Abs(spec.Contour[3].Z - 65) < 1e-9,
            "stiffener must touch the two web inner faces at +/-65 mm");
        foreach (var point in spec.Contour)
        {
            var planeResidual =
                spec.PlaneNormal.X * (point.X - spec.OuterAxisAnchor.X) +
                spec.PlaneNormal.Y * (point.Y - spec.OuterAxisAnchor.Y);
            Require(Math.Abs(planeResidual) < 1e-4,
                "every stiffener contour point must stay in the outer-axis perpendicular plane");
        }
        Require(spec.AssemblySegmentIndex == 0,
            "stiffener below every user joint must belong to assembly segment zero");

        var zeroGapSpec = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>(),
            new FachwerkColumnStiffenerSettings(
                true,
                "PL8",
                "C355-5",
                20,
                FachwerkColumnStiffenerPlacementMode.Elevations,
                1000,
                0,
                new[] { absoluteElevation })).Single();
        var zeroGapDepth = Math.Sqrt(
            Math.Pow(
                zeroGapSpec.Contour[1].X -
                zeroGapSpec.Contour[0].X,
                2) +
            Math.Pow(
                zeroGapSpec.Contour[1].Y -
                zeroGapSpec.Contour[0].Y,
                2));
        var defaultGapDepth = Math.Sqrt(
            Math.Pow(spec.Contour[1].X - spec.Contour[0].X, 2) +
            Math.Pow(spec.Contour[1].Y - spec.Contour[0].Y, 2));
        Require(Math.Abs(zeroGapDepth - defaultGapDepth - 20) < 1e-4,
            "the stiffener contour must keep a 20 mm gap from the inner flange only");

        var upperSourceLine = profile.RequirePath("outer-flange")
            .Primitives
            .First(item =>
                item.NormalizedKind() == "line" &&
                Math.Abs(item.End.Y - item.Start.Y) > 500 &&
                Math.Min(item.Start.Y, item.End.Y) >
                    profile.SectionTransition.EndY + 1);
        var upperElevation = frame.GlobalInsertionZ +
            (upperSourceLine.Start.Y + upperSourceLine.End.Y) * 0.5;
        var upperSpec = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>(),
            new FachwerkColumnStiffenerSettings(
                true,
                "PL8",
                "C355-5",
                20,
                new[] { upperElevation })).Single();
        var upperDepth = Math.Sqrt(
            Math.Pow(
                upperSpec.Contour[1].X - upperSpec.Contour[0].X,
                2) +
            Math.Pow(
                upperSpec.Contour[1].Y - upperSpec.Contour[0].Y,
                2));
        Require(
            Math.Abs(defaultGapDepth - upperDepth - 70) < 1e-4,
            "stiffener depth must follow the physical 450/380 section height");

        var alternatingSpecs = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>(),
            new FachwerkColumnStiffenerSettings(
                true,
                "PL8",
                "C355-5",
                20,
                FachwerkColumnStiffenerPlacementMode.Elevations,
                Array.Empty<double>(),
                20,
                true,
                new[] { absoluteElevation, upperElevation }));
        Require(
            alternatingSpecs.Count == 2 &&
            alternatingSpecs[0].Elevation > alternatingSpecs[1].Elevation,
            "absolute stiffener elevations must also enumerate from top to bottom");
        Require(
            alternatingSpecs[0].GapAtOuterFlange &&
            !alternatingSpecs[1].GapAtOuterFlange,
            "alternating stiffeners must start with the outer-flange gap and then use the inner-flange gap");
        var firstOuterClearance = Math.Sqrt(
            Math.Pow(
                alternatingSpecs[0].Contour[0].X -
                alternatingSpecs[0].OuterAxisAnchor.X,
                2) +
            Math.Pow(
                alternatingSpecs[0].Contour[0].Y -
                alternatingSpecs[0].OuterAxisAnchor.Y,
                2));
        var secondOuterClearance = Math.Sqrt(
            Math.Pow(
                alternatingSpecs[1].Contour[0].X -
                alternatingSpecs[1].OuterAxisAnchor.X,
                2) +
            Math.Pow(
                alternatingSpecs[1].Contour[0].Y -
                alternatingSpecs[1].OuterAxisAnchor.Y,
                2));
        Require(
            Math.Abs(firstOuterClearance - 45) < 1e-4 &&
            Math.Abs(secondOuterClearance - 25) < 1e-4,
            "alternating geometry must move the first rib 20 mm from the outer flange and keep the next rib flush there");
        Require(
            !alternatingSpecs[0].Contour[0].HasChamfer &&
            alternatingSpecs[0].Contour[1].HasChamfer &&
            alternatingSpecs[0].Contour[2].HasChamfer &&
            !alternatingSpecs[0].Contour[3].HasChamfer,
            "an outer-flange gap must place both chamfers at the opposite inner flange");
        Require(
            alternatingSpecs[1].Contour[0].HasChamfer &&
            !alternatingSpecs[1].Contour[1].HasChamfer &&
            !alternatingSpecs[1].Contour[2].HasChamfer &&
            alternatingSpecs[1].Contour[3].HasChamfer,
            "an inner-flange gap must place both chamfers at the opposite outer flange");

        var dividedSpecs = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>(),
            new FachwerkColumnStiffenerSettings(
                true,
                "PL8",
                "C355-5",
                20,
                FachwerkColumnStiffenerPlacementMode.Spacing,
                new[] { 1000d, 1500d },
                20,
                Array.Empty<double>()));
        Require(dividedSpecs.Count == 2,
            "two incremental distances must create exactly two stiffeners");
        Require(
            dividedSpecs
                .Zip(dividedSpecs.Skip(1), (left, right) =>
                    right.Elevation < left.Elevation)
                .All(value => value),
            "incremental distances must enumerate stiffeners downward from the upper outer-axis end");
        var transitionCrossingSpecs =
            FachwerkColumnGeometry.BuildStiffenerSpecs(
                profile,
                frame,
                Array.Empty<FachwerkColumnBreak>(),
                new FachwerkColumnStiffenerSettings(
                    true,
                    "PL8",
                    "C355-5",
                    20,
                    FachwerkColumnStiffenerPlacementMode.Spacing,
                    Enumerable.Repeat(250d, 140).ToArray(),
                    20,
                    Array.Empty<double>()));
        Require(
            transitionCrossingSpecs.Count == 140,
            "path-distance stiffeners must continue through the intrinsic 450/380 transition");
        Require(
            transitionCrossingSpecs.Any(item =>
                item.Elevation - frame.GlobalInsertionZ >
                profile.SectionTransition.EndY) &&
            transitionCrossingSpecs.Any(item =>
                item.Elevation - frame.GlobalInsertionZ <
                profile.SectionTransition.StartY),
            "one top-down stiffener sequence must contain ribs above and below the intrinsic transition");
        var transitionDepths = transitionCrossingSpecs
            .Where(item =>
            {
                var localY = item.Elevation - frame.GlobalInsertionZ;
                return localY > profile.SectionTransition.EndY + 1000 ||
                    localY < profile.SectionTransition.StartY - 1000;
            })
            .Select(item => Math.Sqrt(
                Math.Pow(item.Contour[1].X - item.Contour[0].X, 2) +
                Math.Pow(item.Contour[1].Y - item.Contour[0].Y, 2)))
            .ToArray();
        Require(
            transitionDepths.Max() - transitionDepths.Min() > 60,
            "stiffener depth must change after the sequence crosses from the 380 mm section to the 450 mm section");

        var lowerBreak = new FachwerkColumnBreak(
            absoluteElevation - 100,
            FachwerkColumnBreak.AllFourMode,
            1);
        var upperSegmentSpec = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            new[] { lowerBreak },
            settings).Single();
        Require(upperSegmentSpec.AssemblySegmentIndex == 1,
            "stiffener above one user joint must join assembly segment one");
        var sectionWithJoint = FachwerkColumnGeometry.BuildSectionParts(
            profile,
            frame,
            new[] { lowerBreak });
        Require(
            FachwerkColumnAssemblyPlanner.Build(
                profile.Mark,
                sectionWithJoint).Segments.Count == 2,
            "independent stiffener specs must not add assembly segments");

        var transitionSpec = FachwerkColumnGeometry.BuildStiffenerSpecs(
            profile,
            frame,
            Array.Empty<FachwerkColumnBreak>(),
            new FachwerkColumnStiffenerSettings(
                true,
                "PL20",
                "C355-5",
                15,
                new[]
                {
                    frame.GlobalInsertionZ +
                    (profile.SectionTransition.StartY +
                     profile.SectionTransition.EndY) * 0.5,
                })).Single();
        Require(
            transitionSpec.Contour.Count == 4,
            "a stiffener in the 450/380 transition must use the physical extended flange paths");
    }

    private static void CheckBevelGeometryContract()
    {
        var source = new[]
        {
            new FachwerkColumnPrimitiveDefinition
            {
                Kind = "line",
                Start = new FachwerkColumnLocalPoint { X = 100, Y = 200 },
                End = new FachwerkColumnLocalPoint { X = 100, Y = 1200 },
            },
        };
        var innerWeb = FachwerkColumnPartSpec.Web(
            "inner-web",
            source,
            new FachwerkColumnPathDefinition
            {
                Role = "inner-web",
                Profile = "PL25*350",
                Primitives = source.ToList(),
            },
            "PL25*350",
            1,
            -FachwerkColumnGeometry.WebNormalOffset);
        var outerWeb = FachwerkColumnPartSpec.Web(
            "outer-web",
            source,
            new FachwerkColumnPathDefinition
            {
                Role = "outer-web",
                Profile = "PL25*350",
                Primitives = source.ToList(),
            },
            "PL25*350",
            1,
            FachwerkColumnGeometry.WebNormalOffset);

        var inner = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
            "СФ1", innerWeb, "piece-01");
        var repeated = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
            "СФ1", innerWeb, "piece-01");
        var outer = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
            "СФ1", outerWeb, "piece-01");

        Require(inner.Count == 2 && outer.Count == 2,
            "each physical web fragment must have two longitudinal bevels");
        Require(inner[0].EdgeRole == "inner-flange-edge" &&
                inner[1].EdgeRole == "outer-flange-edge",
            "web bevels must preserve the two physical edge roles");
        Require(inner[0].CutterProfile == "TRI_A14.0*14" &&
                inner[1].CutterProfile == "TRI_A14*14.0",
            "inner web must use the two mirrored legacy TRI profiles");
        Require(outer[0].CutterProfile == "TRI_A14*14.0" &&
                outer[1].CutterProfile == "TRI_A14.0*14",
            "outer web must use the two mirrored legacy TRI profiles");
        Require(Math.Abs(inner[0].NominalRemoval - 12) < 1e-9, "bevel removal must remain 12 mm");
        Require(Math.Abs(inner[0].BevelAngleDeg - 45) < 1e-9, "bevel angle must remain 45 degrees");
        Require(Math.Abs(inner[0].EndOverrun - 1000) < 1e-9,
            "bevel cutter must extend 1000 mm beyond both physical web ends");
        Require(Math.Abs(FachwerkColumnBevelGeometry.LowerCutterRotationOffset - 90.0) < 1e-9,
            "PL25*350 bevel cutters must be rotated 180 degrees from the prior 270-degree position");
        Require(Math.Abs(FachwerkColumnBevelGeometry.UpperCutterRotationOffset - 270.0) < 1e-9,
            "PL25*280 bevel cutters must preserve their accepted 270-degree position");
        Require(inner.All(spec =>
                Math.Abs(spec.CutterRotationOffset -
                    FachwerkColumnBevelGeometry.LowerCutterRotationOffset) < 1e-9) &&
                outer.All(spec =>
                    Math.Abs(spec.CutterRotationOffset -
                        FachwerkColumnBevelGeometry.LowerCutterRotationOffset) < 1e-9),
            "PL25*350 bevel cutters must retain the verified lower-section rotation");
        Require(Math.Abs(inner[0].ExternalSurfaceLocalZ - 90) < 1e-9,
            "inner-web bevels must be placed on the external plate surface");
        Require(Math.Abs(outer[0].ExternalSurfaceLocalZ + 90) < 1e-9,
            "outer-web bevels must be placed on the opposite external plate surface");
        Require(inner[0].CutterPlane == FachwerkBevelPlane.Right &&
                inner[0].CutterDepth == FachwerkBevelDepth.Behind &&
                inner[0].CutterRotation == FachwerkBevelRotation.Top &&
                inner[1].CutterPlane == FachwerkBevelPlane.Right &&
                inner[1].CutterDepth == FachwerkBevelDepth.Front &&
                inner[1].CutterRotation == FachwerkBevelRotation.Front,
            "lower inner web placements must match the verified legacy cuts");
        Require(outer[0].CutterPlane == FachwerkBevelPlane.Left &&
                outer[0].CutterDepth == FachwerkBevelDepth.Behind &&
                outer[0].CutterRotation == FachwerkBevelRotation.Back &&
                outer[1].CutterPlane == FachwerkBevelPlane.Left &&
                outer[1].CutterDepth == FachwerkBevelDepth.Front &&
                outer[1].CutterRotation == FachwerkBevelRotation.Below,
            "lower outer web placements must match the verified legacy cuts");
        Require(inner[0].StableId == repeated[0].StableId &&
                inner[0].GeometrySignature == repeated[0].GeometrySignature,
            "identical bevel input must produce stable idempotency keys");
        Require(inner[0].StableId != inner[1].StableId,
            "the two physical web edges must have independent identities");
        var cutterNames = inner.Concat(outer)
            .Select(spec => spec.CutterName)
            .ToArray();
        Require(cutterNames.All(name =>
                name.Length <= FachwerkColumnBevelGeometry.TeklaPartNameLimit),
            "bevel identity must fit the Tekla 2020 Part.Name limit");
        Require(cutterNames.Distinct(StringComparer.Ordinal).Count() == cutterNames.Length,
            "independent bevel edges must retain unique Tekla-safe names");
        Require(inner[0].CutterName == repeated[0].CutterName,
            "identical bevel input must retain the same Tekla-safe name");
        Require(Math.Abs(source[0].Start.Y - 200) < 1e-9 && Math.Abs(source[0].End.Y - 1200) < 1e-9,
            "bevel construction must not mutate the accepted web path");
        Require(Math.Abs(inner[0].ExternalEdge[0].Start.X + 75) < 1e-9 &&
                Math.Abs(inner[1].ExternalEdge[0].Start.X - 275) < 1e-9,
            "PL25*350 bevel axes must be offset by exactly +/-175 mm");
        Require(Math.Abs(inner[0].CutterPath[0].Start.Y - 2200) < 1e-9 &&
                Math.Abs(inner[0].CutterPath[0].End.Y + 800) < 1e-9,
            "bevel path must be top-to-bottom and overrun both fragment ends by 1000 mm");

        var arcSource = new[]
        {
            new FachwerkColumnPrimitiveDefinition
            {
                Kind = "arc",
                Start = new FachwerkColumnLocalPoint { X = 0, Y = 0 },
                End = new FachwerkColumnLocalPoint { X = 1000, Y = 1000 },
                Center = new FachwerkColumnLocalPoint { X = 0, Y = 1000 },
                SweepDeg = 90,
            },
        };
        var arcWeb = FachwerkColumnPartSpec.Web(
            "inner-web",
            arcSource,
            new FachwerkColumnPathDefinition
            {
                Role = "inner-web",
                Profile = "PL25*350",
                Primitives = arcSource.ToList(),
            },
            "PL25*350",
            1,
            -FachwerkColumnGeometry.WebNormalOffset);
        var arcBevel = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
            "СФ1", arcWeb, "piece-arc")[0];
        var arcExternal = arcBevel.ExternalEdge[0];
        var arcCutter = arcBevel.CutterPath[0];
        var arcRadius = Math.Sqrt(
            Math.Pow(arcExternal.Start.X - arcExternal.Center.X, 2) +
            Math.Pow(arcExternal.Start.Y - arcExternal.Center.Y, 2));
        var arcOverrun = (
            Math.Abs(arcCutter.SweepDeg) - Math.Abs(arcExternal.SweepDeg)) *
            Math.PI / 180.0 *
            arcRadius;
        Require(arcCutter.NormalizedKind() == "arc" &&
                Math.Abs(arcOverrun - 2000) < 1e-6,
            "curved bevel cutters must continue the same arc by 1000 mm at each end");

        var upperInnerWeb = FachwerkColumnPartSpec.Web(
            "inner-web",
            source,
            new FachwerkColumnPathDefinition
            {
                Role = "inner-web",
                Profile = "PL25*280",
                Primitives = source.ToList(),
            },
            "PL25*280",
            1,
            -FachwerkColumnGeometry.WebNormalOffset);
        var upperOuterWeb = FachwerkColumnPartSpec.Web(
            "outer-web",
            source,
            new FachwerkColumnPathDefinition
            {
                Role = "outer-web",
                Profile = "PL25*280",
                Primitives = source.ToList(),
            },
            "PL25*280",
            1,
            FachwerkColumnGeometry.WebNormalOffset);
        var upperInner = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
            "СФ1", upperInnerWeb, "piece-02");
        var upperOuter = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
            "СФ1", upperOuterWeb, "piece-02");

        Require(Math.Abs(upperInner[0].ExternalEdge[0].Start.X + 40) < 1e-9 &&
                Math.Abs(upperInner[1].ExternalEdge[0].Start.X - 240) < 1e-9,
            "PL25*280 bevel axes must be offset by exactly +/-140 mm");
        Require(upperInner.All(spec =>
                Math.Abs(spec.CutterRotationOffset -
                    FachwerkColumnBevelGeometry.UpperCutterRotationOffset) < 1e-9) &&
                upperOuter.All(spec =>
                    Math.Abs(spec.CutterRotationOffset -
                        FachwerkColumnBevelGeometry.UpperCutterRotationOffset) < 1e-9),
            "PL25*280 bevel cutters must retain the verified upper-section rotation");
        Require(upperOuter[0].CutterPlane == FachwerkBevelPlane.Right &&
                upperOuter[0].CutterDepth == FachwerkBevelDepth.Front &&
                upperOuter[0].CutterRotation == FachwerkBevelRotation.Front &&
                upperOuter[1].CutterPlane == FachwerkBevelPlane.Right &&
                upperOuter[1].CutterDepth == FachwerkBevelDepth.Behind &&
                upperOuter[1].CutterRotation == FachwerkBevelRotation.Top,
            "upper outer web placements must match the verified legacy cuts");
        Require(upperInner[0].CutterPlane == FachwerkBevelPlane.Left &&
                upperInner[0].CutterDepth == FachwerkBevelDepth.Front &&
                upperInner[0].CutterRotation == FachwerkBevelRotation.Below &&
                upperInner[1].CutterPlane == FachwerkBevelPlane.Left &&
                upperInner[1].CutterDepth == FachwerkBevelDepth.Behind &&
                upperInner[1].CutterRotation == FachwerkBevelRotation.Back,
            "upper inner web placements must match the verified legacy cuts");
    }

    private static void CheckAttributeContract()
    {
        var acceptedColumn = new FachwerkAcceptedPartProperties(
            "PL25*350",
            "C355-5",
            "3");
        var column = FachwerkAttributeMapper.ForColumn(
            FachwerkPartRole.LeftWeb,
            "СФ27",
            acceptedColumn);
        Require(FachwerkPartRoleContract.FromColumnGeometryRole("outer-web") ==
                FachwerkPartRole.LeftWeb,
            "positive-NormalOffset outer-web must be the semantic left-web");
        Require(FachwerkPartRoleContract.FromColumnGeometryRole("inner-web") ==
                FachwerkPartRole.RightWeb,
            "negative-NormalOffset inner-web must be the semantic right-web");
        Require(column.PartName == "СТЕНКА", "column web part name must be СТЕНКА");
        Require(column.PartPrefix == "515-60.", "column part prefix must be 515-60.");
        Require(column.AssemblyName == "СТОЙКА", "column assembly name must be СТОЙКА");
        Require(column.AssemblyPrefix == "515-60.СФ27-",
            "column assembly prefix must use the existing KM mark");

        var current = new FachwerkPartAttributeSnapshot(
            "tekla-guid-27",
            "fachwerk/part/СФ27/left-web/0/1000/1",
            "PL25*350",
            "C355-5",
            "3",
            "OLD",
            "OLD.",
            17);
        var plan = FachwerkPartAttributeApplyContract.Build(current, column);
        Require(plan.Count == 2,
            "part attribute plan must change only the name and part prefix");
        Require(plan.All(item => item.Field != FachwerkPartAttributeField.PartStartNumber),
            "unspecified start numbers must be preserved");

        var applied = new FachwerkPartAttributeSnapshot(
            current.TeklaGuid,
            current.SemanticId,
            current.Profile,
            current.Material,
            current.ClassName,
            column.PartName,
            column.PartPrefix,
            current.PartStartNumber);
        Require(FachwerkPartAttributeApplyContract.Build(applied, column).Count == 0,
            "repeated attribute mapping must be idempotent");

        var assemblyPlan = FachwerkAssemblyAttributeApplyContract.Build(
            new FachwerkAssemblyAttributeSnapshot("OLD ASSEMBLY", "OLD-ASSEMBLY-", 29),
            column);
        Require(assemblyPlan.Count == 2,
            "assembly attributes must be emitted once and preserve an unspecified start number");
        Require(assemblyPlan.All(item => item.Field != FachwerkAssemblyAttributeField.StartNumber),
            "unspecified assembly start number must be preserved");

        var rigel = FachwerkAttributeMapper.ForRigel(
            "РС-4",
            new FachwerkAcceptedPartProperties("PL70*510", "C355-5", "3"));
        Require(rigel.PartName == "РИГЕЛЬ", "rigel part name must be РИГЕЛЬ");
        Require(rigel.AssemblyName == "РИГЕЛЬ", "rigel assembly name must be РИГЕЛЬ");
        Require(rigel.AssemblyPrefix == "515-60.РС-4",
            "rigel assembly prefix must preserve the existing KM mark");
        Require(
            FachwerkAttributeMapper.BuildRigelAssemblyPrefix("101") ==
            "515-60.РС3-",
            "rigel 101 assembly prefix must map to РС3 without section suffix");
        Require(
            FachwerkAttributeMapper.BuildRigelAssemblyPrefix("103") ==
            "515-60.РС2-",
            "rigel 103 assembly prefix must map to РС2 without section suffix");
        Require(
            FachwerkAttributeMapper.BuildRigelAssemblyPrefix("RS2") ==
            "515-60.РС2-",
            "rigel RS2 assembly prefix must use the canonical РС2 mark");

        var mismatchRejected = false;
        try
        {
            FachwerkPartAttributeApplyContract.Build(
                current,
                FachwerkAttributeMapper.ForColumn(
                    FachwerkPartRole.LeftWeb,
                    "СФ27",
                    new FachwerkAcceptedPartProperties("PL25*280", "C355-5", "3")));
        }
        catch (InvalidOperationException)
        {
            mismatchRejected = true;
        }
        Require(mismatchRejected,
            "attribute mapping must reject an unconfirmed profile replacement");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static double Distance(FachwerkColumnLocalPoint first, FachwerkColumnLocalPoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static int InspectFachwerk(Model model)
    {
        var selector = model.GetModelObjectSelector();
        var all = selector.GetAllObjects();
        var count = 0;
        while (all.MoveNext())
        {
            if (!(all.Current is Part part)) continue;
            string owner = string.Empty;
            part.GetUserProperty("FK_OWNER", ref owner);
            if (!string.IsNullOrWhiteSpace(owner) || (part.Name ?? string.Empty).Contains(".inner-") || (part.Name ?? string.Empty).Contains(".outer-"))
            {
                count++;
                WritePart(part, model);
            }
        }
        Console.WriteLine("INSPECT_OK parts=" + count.ToString(CultureInfo.InvariantCulture));
        return count > 0 ? 0 : 30;
    }

    private static int InspectFachwerkComponents(Model model)
    {
        var components = model.GetModelObjectSelector()
            .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
        var externalIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var columns = 0;
        var rigels = 0;
        var children = 0;
        var childTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var childRoles = new Dictionary<string, int>(StringComparer.Ordinal);
        var invalidColumnRoles = 0;

        while (components.MoveNext())
        {
            if (!(components.Current is Component component)) continue;
            var isColumn = string.Equals(component.Name, "FachwerkColumnPlugin", StringComparison.OrdinalIgnoreCase);
            var isRigel = string.Equals(component.Name, "FachwerkRigelPlugin", StringComparison.OrdinalIgnoreCase);
            if (!isColumn && !isRigel) continue;

            if (isColumn) columns++;
            if (isRigel) rigels++;

            var externalId = string.Empty;
            component.GetAttribute("fk_external_id", ref externalId);
            if (!string.IsNullOrWhiteSpace(externalId))
            {
                externalIds.TryGetValue(externalId, out var occurrences);
                externalIds[externalId] = occurrences + 1;
            }

            var componentChildren = component.GetChildren();
            var componentRoles = new HashSet<string>(StringComparer.Ordinal);
            while (componentChildren.MoveNext())
            {
                if (!(componentChildren.Current is Part part)) continue;

                children++;
                var typeName = part.GetType().Name;
                childTypes.TryGetValue(typeName, out var typeOccurrences);
                childTypes[typeName] = typeOccurrences + 1;

                var role = string.Empty;
                part.GetUserProperty("FK_ROLE", ref role);
                role = string.IsNullOrWhiteSpace(role) ? "<empty>" : role.Trim();
                childRoles.TryGetValue(role, out var roleOccurrences);
                childRoles[role] = roleOccurrences + 1;
                componentRoles.Add(role);
            }

            if (isColumn && new[] { "inner-web", "outer-web", "inner-flange", "outer-flange" }
                .Any(role => !componentRoles.Contains(role)))
            {
                invalidColumnRoles++;
            }
        }

        var duplicateIds = externalIds.Count(pair => pair.Value > 1);
        Console.WriteLine(
            "FACHWERK_COMPONENTS columns=" + columns.ToString(CultureInfo.InvariantCulture) +
            " rigels=" + rigels.ToString(CultureInfo.InvariantCulture) +
            " externalIds=" + externalIds.Count.ToString(CultureInfo.InvariantCulture) +
            " duplicateIds=" + duplicateIds.ToString(CultureInfo.InvariantCulture) +
            " children=" + children.ToString(CultureInfo.InvariantCulture) +
            " invalidColumnRoles=" + invalidColumnRoles.ToString(CultureInfo.InvariantCulture) +
            " childTypes=" + FormatCounts(childTypes) +
            " childRoles=" + FormatCounts(childRoles));

        childTypes.TryGetValue(nameof(ContourPlate), out var contourPlates);
        childTypes.TryGetValue(nameof(SpiralBeam), out var spiralBeams);
        var valid = duplicateIds == 0 && invalidColumnRoles == 0 && contourPlates == 0 && spiralBeams == 0;
        return valid ? 0 : 32;
    }

    private static int InspectFachwerkColumn(Model model, string requestedMark)
    {
        var components = model.GetModelObjectSelector()
            .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
        var matches = 0;
        var parts = 0;

        while (components.MoveNext())
        {
            if (!(components.Current is Component component) ||
                !string.Equals(component.Name, "FachwerkColumnPlugin", StringComparison.OrdinalIgnoreCase))
                continue;

            var mark = string.Empty;
            component.GetAttribute("fk_mark", ref mark);
            if (!string.Equals(mark?.Trim(), requestedMark.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            matches++;
            var profileKey = string.Empty;
            var externalId = string.Empty;
            component.GetAttribute("fk_profile_key", ref profileKey);
            component.GetAttribute("fk_external_id", ref externalId);
            Console.WriteLine(
                "FACHWERK_COLUMN component=" + component.Identifier.ID.ToString(CultureInfo.InvariantCulture) +
                " mark=" + mark +
                " profile=" + profileKey +
                " externalId=" + externalId);

            var children = component.GetChildren();
            while (children.MoveNext())
            {
                if (!(children.Current is Part part)) continue;
                parts++;
                var role = string.Empty;
                var owner = string.Empty;
                part.GetUserProperty("FK_ROLE", ref role);
                part.GetUserProperty("FK_OWNER", ref owner);
                Console.WriteLine("  ROLE=" + role + " OWNER=" + owner);
                WritePart(part, model);
            }
        }

        Console.WriteLine(
            "FACHWERK_COLUMN_OK mark=" + requestedMark +
            " components=" + matches.ToString(CultureInfo.InvariantCulture) +
            " parts=" + parts.ToString(CultureInfo.InvariantCulture));
        return matches == 1 && parts > 0 ? 0 : 34;
    }

    private static int InspectFachwerkBevels(Model model, string requestedMark)
    {
        var components = model.GetModelObjectSelector()
            .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
        var matches = 0;
        var bevels = 0;

        while (components.MoveNext())
        {
            if (!(components.Current is Component component) ||
                !string.Equals(component.Name, "FachwerkColumnPlugin", StringComparison.OrdinalIgnoreCase))
                continue;

            var mark = string.Empty;
            component.GetAttribute("fk_mark", ref mark);
            if (!string.Equals(mark?.Trim(), requestedMark.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            matches++;
            var children = component.GetChildren();
            while (children.MoveNext())
            {
                if (!(children.Current is Part father)) continue;

                var role = string.Empty;
                var breakIndex = 0;
                father.GetUserProperty("FK_ROLE", ref role);
                father.GetUserProperty("FK_BREAK", ref breakIndex);
                var booleans = father.GetBooleans();
                while (booleans.MoveNext())
                {
                    if (!(booleans.Current is BooleanPart booleanPart) ||
                        !(booleanPart.OperativePart is Part cutter))
                        continue;

                    try { cutter.Select(); }
                    catch { }
                    var cutterBevelId = string.Empty;
                    var cutterBevelKey = string.Empty;
                    var cutterBevelSignature = string.Empty;
                    var cutterBevelRole = string.Empty;
                    cutter.GetUserProperty("FK_BEVEL_ID", ref cutterBevelId);
                    cutter.GetUserProperty("FK_BEVEL_KEY", ref cutterBevelKey);
                    cutter.GetUserProperty("FK_BEVEL_SIG", ref cutterBevelSignature);
                    cutter.GetUserProperty("FK_ROLE", ref cutterBevelRole);

                    var booleanBevelId = string.Empty;
                    var booleanBevelKey = string.Empty;
                    var booleanBevelSignature = string.Empty;
                    var booleanBevelRole = string.Empty;
                    booleanPart.GetUserProperty("FK_BEVEL_ID", ref booleanBevelId);
                    booleanPart.GetUserProperty("FK_BEVEL_KEY", ref booleanBevelKey);
                    booleanPart.GetUserProperty("FK_BEVEL_SIG", ref booleanBevelSignature);
                    booleanPart.GetUserProperty("FK_ROLE", ref booleanBevelRole);
                    bevels++;
                    Console.WriteLine(string.Join(" | ", new[]
                    {
                        "BEVEL",
                        "mark=" + mark,
                        "father=" + (father.Identifier?.ID ?? 0).ToString(CultureInfo.InvariantCulture),
                        "fatherProfile=" + (father.Profile?.ProfileString ?? string.Empty),
                        "role=" + role,
                        "break=" + breakIndex.ToString(CultureInfo.InvariantCulture),
                        "boolean=" + (booleanPart.Identifier?.ID ?? 0).ToString(CultureInfo.InvariantCulture),
                        "cutter=" + (cutter.Identifier?.ID ?? 0).ToString(CultureInfo.InvariantCulture),
                        "name=" + (cutter.Name ?? string.Empty),
                        "nameLength=" + (cutter.Name ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture),
                        "profile=" + (cutter.Profile?.ProfileString ?? string.Empty),
                        "plane=" + cutter.Position.Plane + ":" + F(cutter.Position.PlaneOffset),
                        "depth=" + cutter.Position.Depth + ":" + F(cutter.Position.DepthOffset),
                        "rotation=" + cutter.Position.Rotation + ":" + F(cutter.Position.RotationOffset),
                        "cutterBevelId=" + cutterBevelId,
                        "cutterBevelKey=" + cutterBevelKey,
                        "cutterBevelSignature=" + cutterBevelSignature,
                        "cutterBevelRole=" + cutterBevelRole,
                        "booleanBevelId=" + booleanBevelId,
                        "booleanBevelKey=" + booleanBevelKey,
                        "booleanBevelSignature=" + booleanBevelSignature,
                        "booleanBevelRole=" + booleanBevelRole,
                    }));
                }
            }
        }

        Console.WriteLine(
            "FACHWERK_BEVELS_OK mark=" + requestedMark +
            " components=" + matches.ToString(CultureInfo.InvariantCulture) +
            " bevels=" + bevels.ToString(CultureInfo.InvariantCulture));
        return matches == 1 && bevels > 0 ? 0 : 35;
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        return string.Join(",", counts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + pair.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static int InspectFachwerkRigelParts(Model model)
    {
        var all = model.GetModelObjectSelector().GetAllObjects();
        var externalIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var profiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var taggedRigels = 0;
        var nonStraight = 0;
        var vertical = 0;
        var invalidPosition = 0;
        var printedSectionOffsets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (all.MoveNext())
        {
            if (!(all.Current is Part part)) continue;

            var componentType = string.Empty;
            part.GetUserProperty("STRUCTURA_COMPONENT_TYPE", ref componentType);
            if (!string.Equals(componentType, "FachwerkRigelPart", StringComparison.Ordinal)) continue;

            taggedRigels++;
            var externalId = string.Empty;
            part.GetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", ref externalId);
            if (!string.IsNullOrWhiteSpace(externalId))
            {
                externalIds.TryGetValue(externalId, out var occurrences);
                externalIds[externalId] = occurrences + 1;
            }

            var profile = part.Profile?.ProfileString ?? string.Empty;
            profiles.TryGetValue(profile, out var profileOccurrences);
            profiles[profile] = profileOccurrences + 1;

            if (!(part is Beam beam))
            {
                nonStraight++;
                continue;
            }

            var dx = beam.EndPoint.X - beam.StartPoint.X;
            var dy = beam.EndPoint.Y - beam.StartPoint.Y;
            var dz = beam.EndPoint.Z - beam.StartPoint.Z;
            var horizontalLength = Math.Sqrt(dx * dx + dy * dy);
            if (horizontalLength <= Math.Abs(dz)) vertical++;

            if (beam.Position.Plane != Position.PlaneEnum.MIDDLE ||
                beam.Position.Depth != Position.DepthEnum.MIDDLE ||
                beam.Position.Rotation != Position.RotationEnum.FRONT)
            {
                invalidPosition++;
            }

            if (printedSectionOffsets.Add(profile))
            {
                var offsets = ReadHorizontalSectionOffsets(beam);
                Console.WriteLine(
                    "RIGEL_SECTION_OFFSET profile=" + profile +
                    " min=" + offsets.Min.ToString("0.###", CultureInfo.InvariantCulture) +
                    " max=" + offsets.Max.ToString("0.###", CultureInfo.InvariantCulture) +
                    " center=" + offsets.Center.ToString("0.###", CultureInfo.InvariantCulture));
            }

            WritePart(beam, model);
        }

        var legacyRigelComponents = 0;
        var components = model.GetModelObjectSelector()
            .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
        while (components.MoveNext())
        {
            if (components.Current is Component component &&
                string.Equals(component.Name, "FachwerkRigelPlugin", StringComparison.OrdinalIgnoreCase))
            {
                legacyRigelComponents++;
            }
        }

        profiles.TryGetValue("PL70*760", out var profile101);
        profiles.TryGetValue("PL70*510", out var profile103AndRs2);
        var duplicateIds = externalIds.Count(pair => pair.Value > 1);
        Console.WriteLine(
            "FACHWERK_RIGEL_PARTS total=" + taggedRigels.ToString(CultureInfo.InvariantCulture) +
            " profile_PL70x760=" + profile101.ToString(CultureInfo.InvariantCulture) +
            " profile_PL70x510=" + profile103AndRs2.ToString(CultureInfo.InvariantCulture) +
            " externalIds=" + externalIds.Count.ToString(CultureInfo.InvariantCulture) +
            " duplicateIds=" + duplicateIds.ToString(CultureInfo.InvariantCulture) +
            " nonStraight=" + nonStraight.ToString(CultureInfo.InvariantCulture) +
            " vertical=" + vertical.ToString(CultureInfo.InvariantCulture) +
            " invalidPosition=" + invalidPosition.ToString(CultureInfo.InvariantCulture) +
            " legacyComponents=" + legacyRigelComponents.ToString(CultureInfo.InvariantCulture));

        var valid = taggedRigels == 31 &&
                    profile101 == 15 &&
                    profile103AndRs2 == 16 &&
                    externalIds.Count == 31 &&
                    duplicateIds == 0 &&
                    nonStraight == 0 &&
                    vertical == 0 &&
                    invalidPosition == 0 &&
                    legacyRigelComponents == 0;
        return valid ? 0 : 33;
    }

    private static (double Min, double Max, double Center) ReadHorizontalSectionOffsets(Beam beam)
    {
        var dx = beam.EndPoint.X - beam.StartPoint.X;
        var dy = beam.EndPoint.Y - beam.StartPoint.Y;
        var horizontalLength = Math.Sqrt(dx * dx + dy * dy);
        if (horizontalLength < 1e-9) return (double.NaN, double.NaN, double.NaN);
        var normalX = -dy / horizontalLength;
        var normalY = dx / horizontalLength;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var faces = beam.GetSolid().GetFaceEnumerator();
        while (faces.MoveNext())
        {
            if (!(faces.Current is Face face)) continue;
            var loops = face.GetLoopEnumerator();
            while (loops.MoveNext())
            {
                if (!(loops.Current is Loop loop)) continue;
                var vertices = loop.GetVertexEnumerator();
                while (vertices.MoveNext())
                {
                    if (!(vertices.Current is Point point)) continue;
                    var offset =
                        (point.X - beam.StartPoint.X) * normalX +
                        (point.Y - beam.StartPoint.Y) * normalY;
                    min = Math.Min(min, offset);
                    max = Math.Max(max, offset);
                }
            }
        }
        return !double.IsNaN(min) && !double.IsInfinity(min) &&
               !double.IsNaN(max) && !double.IsInfinity(max)
            ? (min, max, (min + max) / 2.0)
            : (double.NaN, double.NaN, double.NaN);
    }

    private static int InspectProfile(Model model, string requestedProfile)
    {
        var all = model.GetModelObjectSelector().GetAllObjects();
        var count = 0;
        while (all.MoveNext())
        {
            if (!(all.Current is Part part)) continue;
            var profile = part.Profile?.ProfileString ?? string.Empty;
            if (!string.Equals(profile, requestedProfile, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            WritePart(part, model);
        }
        Console.WriteLine("INSPECT_PROFILE_OK profile=" + requestedProfile + " parts=" + count.ToString(CultureInfo.InvariantCulture));
        return count > 0 ? 0 : 31;
    }

    private static int Inspect(ModelObjectEnumerator objects, Model model)
    {
        var count = 0;
        while (objects.MoveNext())
        {
            if (!(objects.Current is Part part)) continue;
            count++;
            WritePart(part, model);
        }
        Console.WriteLine("INSPECT_OK parts=" + count.ToString(CultureInfo.InvariantCulture));
        return count > 0 ? 0 : 30;
    }

    private static int InspectSelectedNode(Model model, bool includeOperativeGeometry)
    {
        var info = model.GetInfo();
        Console.WriteLine("MODEL name=" + info.ModelName + " path=" + info.ModelPath);

        var selected = new List<ModelObject>();
        var selection = new Tekla.Structures.Model.UI.ModelObjectSelector().GetSelectedObjects();
        while (selection.MoveNext())
        {
            if (selection.Current is ModelObject current) selected.Add(current);
        }

        var parts = selected.OfType<Part>().ToArray();
        var directlySelectedFeatureIds = selected
            .Where(item => !(item is Part))
            .Select(item => item.Identifier?.ID ?? 0)
            .Where(id => id != 0)
            .ToHashSet();
        var features = new Dictionary<int, ModelObject>();
        foreach (var selectedObject in selected)
        {
            if (!(selectedObject is Part)) AddFeature(features, selectedObject);
        }
        foreach (var part in parts)
        {
            AddFeatures(features, TryEnumerate(part.GetBolts));
            AddFeatures(features, TryEnumerate(part.GetBooleans));
            AddFeatures(features, TryEnumerate(part.GetWelds));
        }

        Console.WriteLine(
            "SELECTION objects=" + selected.Count.ToString(CultureInfo.InvariantCulture) +
            " parts=" + parts.Length.ToString(CultureInfo.InvariantCulture) +
            " features=" + features.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var part in parts.OrderBy(item => item.Identifier?.ID ?? 0)) WritePart(part, model);
        foreach (var feature in features.Values.OrderBy(item => item.Identifier?.ID ?? 0))
            WriteFeature(
                feature,
                model,
                directlySelectedFeatureIds.Contains(feature.Identifier?.ID ?? 0),
                includeOperativeGeometry);

        return parts.Length > 0 ? 0 : 30;
    }

    private static int InspectBooleanOperative(Model model, string requestedIdentifier)
    {
        if (!int.TryParse(requestedIdentifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var identifier))
        {
            Console.Error.WriteLine("INVALID_IDENTIFIER " + requestedIdentifier);
            return 35;
        }

        var booleanPart = model.SelectModelObject(new Identifier(identifier)) as BooleanPart;
        if (booleanPart == null)
        {
            Console.Error.WriteLine("BOOLEAN_NOT_FOUND " + identifier.ToString(CultureInfo.InvariantCulture));
            return 36;
        }

        Console.WriteLine(
            "BOOLEAN id=" + identifier.ToString(CultureInfo.InvariantCulture) +
            " type=" + booleanPart.Type +
            " father=" + ObjectId(booleanPart.Father) +
            " operative=" + ObjectId(booleanPart.OperativePart));
        if (!(booleanPart.OperativePart is Part operativePart))
        {
            Console.Error.WriteLine("OPERATIVE_PART_MISSING " + identifier.ToString(CultureInfo.InvariantCulture));
            return 37;
        }

        WritePart(operativePart, model);
        return 0;
    }

    private static int UpsertTopSpliceNode(Model model, string mark)
    {
        var result = FachwerkTopSplicePrototype.Upsert(model, mark);
        Console.WriteLine(
            "TOP_SPLICE_UPSERT_OK mark=" + result.Mark +
            " parts=" + result.PartCount.ToString(CultureInfo.InvariantCulture) +
            " welds=" + result.WeldCount.ToString(CultureInfo.InvariantCulture) +
            " booleans=" + result.BooleanCount.ToString(CultureInfo.InvariantCulture) +
            " removed=" + result.RemovedPartCount.ToString(CultureInfo.InvariantCulture) +
            " residual=" + F(result.MaxFrameResidualMm));
        return 0;
    }

    private static int InspectTopSpliceNode(Model model, string mark)
    {
        var result = FachwerkTopSplicePrototype.Inspect(model, mark);
        Console.WriteLine(
            "TOP_SPLICE_INSPECT mark=" + result.Mark +
            " parts=" + result.PartCount.ToString(CultureInfo.InvariantCulture) +
            " welds=" + result.WeldCount.ToString(CultureInfo.InvariantCulture) +
            " booleans=" + result.BooleanCount.ToString(CultureInfo.InvariantCulture) +
            " roles=" + string.Join(",", result.Roles.OrderBy(value => value, StringComparer.Ordinal)));
        return result.IsComplete ? 0 : 38;
    }

    private static IEnumerable<ModelObject> TryEnumerate(Func<ModelObjectEnumerator> factory)
    {
        ModelObjectEnumerator? enumerator;
        try { enumerator = factory(); }
        catch { yield break; }
        while (enumerator.MoveNext())
        {
            if (enumerator.Current is ModelObject current) yield return current;
        }
    }

    private static void AddFeatures(
        IDictionary<int, ModelObject> target,
        IEnumerable<ModelObject> features)
    {
        foreach (var feature in features) AddFeature(target, feature);
    }

    private static void AddFeature(IDictionary<int, ModelObject> target, ModelObject feature)
    {
        var id = feature.Identifier?.ID ?? 0;
        if (id != 0 && !target.ContainsKey(id)) target[id] = feature;
    }

    private static void WriteFeature(
        ModelObject feature,
        Model model,
        bool directlySelected,
        bool includeOperativeGeometry)
    {
        var id = feature.Identifier?.ID ?? 0;
        var guid = id == 0 ? string.Empty : model.GetGUIDByIdentifier(feature.Identifier);
        Console.WriteLine(
            "FEATURE id=" + id.ToString(CultureInfo.InvariantCulture) +
            " guid=" + guid +
            " type=" + feature.GetType().Name +
            " direct=" + directlySelected);

        switch (feature)
        {
            case BoltGroup bolt:
                Console.WriteLine(
                    "  BOLT physical=" + bolt.Bolt +
                    " size=" + F(bolt.BoltSize) +
                    " standard=" + bolt.BoltStandard +
                    " tolerance=" + F(bolt.Tolerance) +
                    " holeType=" + bolt.HoleType +
                    " first=" + P(bolt.FirstPosition) +
                    " second=" + P(bolt.SecondPosition) +
                    " partToBoltTo=" + ObjectId(bolt.PartToBoltTo) +
                    " partToBeBolted=" + ObjectId(bolt.PartToBeBolted));
                var boltIndex = 0;
                foreach (var point in bolt.BoltPositions.OfType<Point>())
                    Console.WriteLine("  BP[" + boltIndex++ + "]=" + P(point));
                break;

            case CutPlane cutPlane:
                Console.WriteLine("  CUT_PLANE father=" + ObjectId(cutPlane.Father));
                WritePlane(cutPlane.Plane);
                break;

            case Fitting fitting:
                Console.WriteLine("  FITTING father=" + ObjectId(fitting.Father));
                WritePlane(fitting.Plane);
                break;

            case EdgeChamfer edgeChamfer:
                Console.WriteLine(
                    "  EDGE_CHAMFER father=" + ObjectId(edgeChamfer.Father) +
                    " first=" + P(edgeChamfer.FirstEnd) +
                    " second=" + P(edgeChamfer.SecondEnd) +
                    " type=" + edgeChamfer.Chamfer.Type +
                    " x=" + F(edgeChamfer.Chamfer.X) +
                    " y=" + F(edgeChamfer.Chamfer.Y));
                break;

            case BooleanPart booleanPart:
                Console.WriteLine(
                    "  BOOLEAN type=" + booleanPart.Type +
                    " father=" + ObjectId(booleanPart.Father) +
                    " operative=" + ObjectId(booleanPart.OperativePart));
                if (booleanPart.OperativePart is Part operativePart)
                {
                    Console.WriteLine(
                        "  OPERATIVE type=" + operativePart.GetType().Name +
                        " name=" + operativePart.Name +
                        " profile=" + operativePart.Profile.ProfileString +
                        " material=" + operativePart.Material.MaterialString +
                        " class=" + operativePart.Class);
                    if (includeOperativeGeometry && directlySelected)
                        WritePart(operativePart, model);
                }
                break;

            case BaseWeld weld:
                Console.WriteLine(
                    "  WELD main=" + ObjectId(weld.MainObject) +
                    " secondary=" + ObjectId(weld.SecondaryObject) +
                    " typeAbove=" + weld.TypeAbove +
                    " sizeAbove=" + F(weld.SizeAbove) +
                    " typeBelow=" + weld.TypeBelow +
                    " sizeBelow=" + F(weld.SizeBelow) +
                    " preparation=" + weld.Preparation +
                    " placement=" + weld.Placement +
                    " shop=" + weld.ShopWeld);
                break;
        }
    }

    private static void WritePlane(Plane plane)
    {
        Console.WriteLine(
            "  PLANE origin=" + P(plane.Origin) +
            " axisX=" + V(plane.AxisX) +
            " axisY=" + V(plane.AxisY));
    }

    private static string ObjectId(ModelObject? value) =>
        (value?.Identifier?.ID ?? 0).ToString(CultureInfo.InvariantCulture);

    private static void WritePart(Part part, Model model)
    {
        var id = part.Identifier?.ID ?? 0;
        var guid = id == 0 ? string.Empty : model.GetGUIDByIdentifier(part.Identifier);
        var cs = part.GetCoordinateSystem();
        Console.WriteLine(string.Join(" | ", new[]
        {
            "PART id=" + id,
            "guid=" + guid,
            "type=" + part.GetType().Name,
            "name=" + part.Name,
            "profile=" + part.Profile.ProfileString,
            "material=" + part.Material.MaterialString,
            "class=" + part.Class,
            "plane=" + part.Position.Plane + ":" + F(part.Position.PlaneOffset),
            "rotation=" + part.Position.Rotation + ":" + F(part.Position.RotationOffset),
            "depth=" + part.Position.Depth + ":" + F(part.Position.DepthOffset),
            "csO=" + P(cs.Origin),
            "csX=" + V(cs.AxisX),
            "csY=" + V(cs.AxisY),
        }));

        if (part is Beam beam)
            Console.WriteLine("  BEAM start=" + P(beam.StartPoint) + " end=" + P(beam.EndPoint));
        if (part is PolyBeam polyBeam)
        {
            var points = polyBeam.Contour.ContourPoints.Cast<ContourPoint>().ToArray();
            for (var index = 0; index < points.Length; index++)
            {
                var point = points[index];
                Console.WriteLine("  CP[" + index + "]=" + P(point) + " chamfer=" + point.Chamfer.Type + " x=" + F(point.Chamfer.X) + " y=" + F(point.Chamfer.Y));
            }
        }

        var centerLine = part.GetCenterLine(false);
        for (var index = 0; index < centerLine.Count; index++)
        {
            if (centerLine[index] is Point point)
                Console.WriteLine("  CL[" + index + "]=" + P(point));
        }
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string P(Point point) => F(point.X) + "," + F(point.Y) + "," + F(point.Z);
    private static string V(Vector vector) => F(vector.X) + "," + F(vector.Y) + "," + F(vector.Z);
}
