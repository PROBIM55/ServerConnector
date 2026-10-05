#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

[Plugin("FachwerkLowerRigelNodePlugin")]
[PluginUserInterface(
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodeForm")]
[PluginName("ENU", "Узел нижнего ригеля")]
[PluginDescription(
    "ENU",
    "Узел нижнего ригеля по исходному Grasshopper-определению")]
[InputObjectDependency(InputObjectDependency.NOT_DEPENDENT_MODIFIABLE)]
public sealed class FachwerkLowerRigelNodePlugin : PluginBase
{
    private const double MontageBevelAngleDeg = 40.0;
    private const double MontageBevelRootFace = 2.0;

    private static readonly string[] InputRoles =
    {
        "left-tube",
        "right-tube",
        "outer-flange",
        "inner-flange",
        "left-web",
        "right-web",
    };

    private readonly FachwerkLowerRigelNodePluginData _data;

    public FachwerkLowerRigelNodePlugin(
        FachwerkLowerRigelNodePluginData data)
    {
        _data = data ?? new FachwerkLowerRigelNodePluginData();
    }

    public override List<InputDefinition> DefineInput()
    {
        var picker = new Picker();
        Beam leftTube = RequireBeam(
            picker.PickObject(
                Picker.PickObjectEnum.PICK_ONE_PART,
                "Выберите левую трубу"),
            "Левая труба");
        Beam rightTube = RequireBeam(
            picker.PickObject(
                Picker.PickObjectEnum.PICK_ONE_PART,
                "Выберите правую трубу"),
            "Правая труба");
        Part outerFlange = RequirePart(
            picker.PickObject(
                Picker.PickObjectEnum.PICK_ONE_PART,
                "Выберите наружный пояс верхней секции"),
            "Наружный пояс");
        Part innerFlange = RequirePart(
            picker.PickObject(
                Picker.PickObjectEnum.PICK_ONE_PART,
                "Выберите внутренний пояс верхней секции"),
            "Внутренний пояс");
        Part leftWeb = RequirePart(
            picker.PickObject(
                Picker.PickObjectEnum.PICK_ONE_PART,
                "Выберите левую стенку"),
            "Левая стенка");
        Part rightWeb = RequirePart(
            picker.PickObject(
                Picker.PickObjectEnum.PICK_ONE_PART,
                "Выберите правую стенку"),
            "Правая стенка");

        ValidateUnique(
            leftTube,
            rightTube,
            outerFlange,
            innerFlange,
            leftWeb,
            rightWeb);
        return new List<InputDefinition>
        {
            new(leftTube.Identifier),
            new(rightTube.Identifier),
            new(outerFlange.Identifier),
            new(innerFlange.Identifier),
            new(leftWeb.Identifier),
            new(rightWeb.Identifier),
        };
    }

    public override bool Run(List<InputDefinition> input)
    {
        var created = new List<ModelObject>();
        var weldParts = new List<KeyValuePair<string, Part>>();
        Beam leftTube = null;
        Beam rightTube = null;
        BeamState leftState = null;
        BeamState rightState = null;
        var tubesModified = false;
        var model = new Model();
        WorkPlaneHandler workPlaneHandler = null;
        TransformationPlane previousPlane = null;
        try
        {
            if (!model.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Нет соединения с открытой моделью Tekla.");
            }

            workPlaneHandler = model.GetWorkPlaneHandler();
            previousPlane = workPlaneHandler.GetCurrentTransformationPlane();
            if (!workPlaneHandler.SetCurrentTransformationPlane(
                    new TransformationPlane()))
            {
                throw new InvalidOperationException(
                    "Tekla не установила глобальную рабочую плоскость.");
            }

            IReadOnlyList<Part> parts = ReadInputs(model, input);
            leftTube = RequireBeam(parts[0], "Левая труба");
            rightTube = RequireBeam(parts[1], "Правая труба");
            Part outerFlange = parts[2];
            Part innerFlange = parts[3];
            Part leftWeb = parts[4];
            Part rightWeb = parts[5];
            leftState =
                FachwerkLowerRigelNodeTeklaAdapter.CaptureBeam(leftTube);
            rightState =
                FachwerkLowerRigelNodeTeklaAdapter.CaptureBeam(rightTube);

            Trace(
                "INPUT gap=" + Invariant(_data.JointGap) +
                " diameter=" + Invariant(_data.TubeDiameter) +
                " width=" + Invariant(_data.TransitionPlateWidth) +
                " length=" +
                Invariant(
                    CommonLength(
                        _data.LeftTransitionLength,
                        _data.RightTransitionLength,
                        500)) +
                " axisCorrection=" + (_data.AxisCorrectionEnabled ?? "<null>") +
                " bottomClosure=" + (_data.BottomClosureEnabled ?? "<null>") +
                " outerFlangeTubeCut=" +
                (_data.OuterFlangeTubeCutEnabled ?? "<null>") +
                " controlLine=" + (_data.ControlLineEnabled ?? "<null>"));

            double gap = Positive(
                _data.JointGap,
                25,
                "Зазор");
            double diameter = Positive(
                _data.TubeDiameter,
                530,
                "Диаметр трубы");
            double width = Positive(
                _data.TransitionPlateWidth,
                350,
                "Ширина переходной пластины");
            double transitionLength = CommonLength(
                _data.LeftTransitionLength,
                _data.RightTransitionLength,
                500);
            string profile = Normalize(
                _data.PlateProfile,
                "PL25");
            string material = Normalize(
                _data.Material,
                FachwerkLowerRigelNodeTeklaAdapter.NodeMaterial);
            string className = Normalize(
                _data.ClassName,
                "20");
            bool correctAxes = Enabled(
                _data.AxisCorrectionEnabled,
                false);
            bool automaticLength = Enabled(
                _data.AutomaticLengthEnabled,
                false);
            bool createBottomClosure = Enabled(
                _data.BottomClosureEnabled,
                false);
            bool createOuterFlangeTubeCut = Enabled(
                _data.OuterFlangeTubeCutEnabled,
                true);
            bool createControlLine = Enabled(
                _data.ControlLineEnabled,
                true);
            int componentId = Identifier == null ? 0 : Identifier.ID;

            IReadOnlyList<Point> leftTubeAxis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(leftTube);
            IReadOnlyList<Point> rightTubeAxis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(rightTube);
            IReadOnlyList<Point> leftWebAxis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(leftWeb);
            IReadOnlyList<Point> rightWebAxis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(rightWeb);
            IReadOnlyList<Point> outerFlangeAxis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(outerFlange);
            IReadOnlyList<Point> innerFlangeAxis =
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(innerFlange);
            if (automaticLength)
            {
                transitionLength =
                    FachwerkLowerRigelNodeAutoLength.Calculate(
                        leftTubeAxis,
                        rightTubeAxis,
                        leftWebAxis,
                        rightWebAxis,
                        outerFlangeAxis,
                        innerFlangeAxis,
                        gap,
                        diameter,
                        width,
                        transitionLength);
                _data.LeftTransitionLength = transitionLength;
                _data.RightTransitionLength = transitionLength;
                Trace(
                    "AUTO_LENGTH length=" + Invariant(transitionLength) +
                    " shortEdge=" +
                    Invariant(
                        FachwerkLowerRigelNodeAutoLength.Measure(
                            leftTubeAxis,
                            rightTubeAxis,
                            leftWebAxis,
                            rightWebAxis,
                            outerFlangeAxis,
                            innerFlangeAxis,
                            gap,
                            diameter,
                            width,
                            transitionLength)));
            }

            FachwerkLowerRigelLayout layout =
                FachwerkLowerRigelNodeGeometry.BuildLayout(
                    leftTubeAxis,
                    rightTubeAxis,
                    leftWebAxis,
                    rightWebAxis,
                    gap,
                    diameter,
                    width,
                    transitionLength,
                    transitionLength);
            FachwerkLowerRigelBottomClosure bottomClosure =
                createBottomClosure
                    ? FachwerkLowerRigelNodeGeometry.BuildBottomClosure(layout)
                    : null;
            FachwerkLowerRigelMontageLayout montage =
                FachwerkLowerRigelNodeGeometry.BuildMontageLayout(layout);
            Vector centralTubeDirection =
                FachwerkLowerRigelNodeGeometry.VectorBetween(
                    layout.LeftCentralTubePoint,
                    layout.RightCentralTubePoint);
            FachwerkLowerRigelFlangeSplice outerFlangeSplice =
                FachwerkLowerRigelNodeGeometry.BuildFlangeSplice(
                    "outer-flange",
                    outerFlangeAxis,
                    innerFlangeAxis,
                    montage,
                    layout.LeftCentralTubePoint,
                    centralTubeDirection,
                    diameter * 0.5);
            FachwerkLowerRigelFlangeSplice innerFlangeSplice =
                FachwerkLowerRigelNodeGeometry.BuildFlangeSplice(
                    "inner-flange",
                    innerFlangeAxis,
                    outerFlangeAxis,
                    montage,
                    layout.LeftCentralTubePoint,
                    centralTubeDirection,
                    diameter * 0.5);

            if (correctAxes)
            {
                FachwerkLowerRigelNodeTeklaAdapter.ApplyCorrectedJointPoint(
                    leftTube,
                    leftState,
                    layout.LeftTubeIntersection);
                tubesModified = true;
                FachwerkLowerRigelNodeTeklaAdapter.ApplyCorrectedJointPoint(
                    rightTube,
                    rightState,
                    layout.RightTubeIntersection);
            }

            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateFitting(
                    leftTube,
                    layout.LeftOuterPlane,
                    FachwerkLowerRigelNodeTeklaAdapter.FarBeamEndpoint(
                        leftState,
                        layout.LeftTubeIntersection),
                    "left-tube-fitting",
                    componentId));
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateFitting(
                    rightTube,
                    layout.RightOuterPlane,
                    FachwerkLowerRigelNodeTeklaAdapter.FarBeamEndpoint(
                        rightState,
                        layout.RightTubeIntersection),
                    "right-tube-fitting",
                    componentId));

            Beam centralTube =
                FachwerkLowerRigelNodeTeklaAdapter.CreateCentralTube(
                    leftTube,
                    layout,
                    material,
                    className,
                    componentId);
            created.Add(centralTube);
            Point centralTubeAxisPoint =
                Midpoint(centralTube.StartPoint, centralTube.EndPoint);
            Vector centralTubeAxisDirection =
                FachwerkLowerRigelNodeGeometry.VectorBetween(
                    centralTube.StartPoint,
                    centralTube.EndPoint);
            weldParts.Add(
                new KeyValuePair<string, Part>(
                    "central-tube",
                    centralTube));
            Point centralKeep = Midpoint(
                layout.LeftInnerPlane.Origin,
                layout.RightInnerPlane.Origin);
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateCutPlane(
                    centralTube,
                    layout.LeftInnerPlane,
                    centralKeep,
                    "central-left-cut",
                    componentId));
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateCutPlane(
                    centralTube,
                    layout.RightInnerPlane,
                    centralKeep,
                    "central-right-cut",
                    componentId));

            ContourPlate leftTransition =
                FachwerkLowerRigelNodeTeklaAdapter.CreateTransitionPlate(
                    layout.LeftTransition,
                    leftWeb,
                    profile,
                    material,
                    className,
                    componentId);
            created.Add(leftTransition);
            weldParts.Add(
                new KeyValuePair<string, Part>(
                    "left-transition",
                    leftTransition));
            ContourPlate rightTransition =
                FachwerkLowerRigelNodeTeklaAdapter.CreateTransitionPlate(
                    layout.RightTransition,
                    rightWeb,
                    profile,
                    material,
                    className,
                    componentId);
            created.Add(rightTransition);
            weldParts.Add(
                new KeyValuePair<string, Part>(
                    "right-transition",
                    rightTransition));

            foreach (BooleanPart bevel in
                     FachwerkLowerRigelNodeTeklaAdapter.CreateTransitionBevels(
                         leftTransition,
                         layout.LeftTransition,
                         material,
                         componentId))
            {
                created.Add(bevel);
            }
            foreach (BooleanPart bevel in
                     FachwerkLowerRigelNodeTeklaAdapter.CreateTransitionBevels(
                         rightTransition,
                         layout.RightTransition,
                         material,
                         componentId))
            {
                created.Add(bevel);
            }

            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.AddTrimmedEndPlate(
                    leftTransition,
                    layout.LeftTransition,
                    leftWeb,
                    layout.LeftEndPlateBoundary,
                    centralTubeAxisPoint,
                    centralTubeAxisDirection,
                    diameter,
                    profile,
                    material,
                    "left-end-addition",
                    componentId));
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.AddTrimmedEndPlate(
                    rightTransition,
                    layout.RightTransition,
                    rightWeb,
                    layout.RightEndPlateBoundary,
                    centralTubeAxisPoint,
                    centralTubeAxisDirection,
                    diameter,
                    profile,
                    material,
                    "right-end-addition",
                    componentId));

            if (bottomClosure != null)
            {
                created.Add(
                    FachwerkLowerRigelNodeTeklaAdapter.CreateBottomClosure(
                        bottomClosure,
                        className,
                        componentId));
                Trace(
                    "BOTTOM_CLOSURE vertices=" +
                    bottomClosure.Boundary.Count.ToString(
                        CultureInfo.InvariantCulture));
            }

            ContourPlate outerLowerFlange =
                FachwerkLowerRigelNodeTeklaAdapter.CreateLowerFlangeFragment(
                    outerFlange,
                    outerFlangeSplice,
                    componentId);
            created.Add(outerLowerFlange);
            weldParts.Add(
                new KeyValuePair<string, Part>(
                    "outer-lower-flange",
                    outerLowerFlange));
            if (createOuterFlangeTubeCut)
            {
                created.Add(
                    FachwerkLowerRigelNodeTeklaAdapter.CutByExtendedCentralTube(
                        outerLowerFlange,
                        centralTube,
                        "outer-lower-flange-tube-cut",
                        componentId));
            }
            ContourPlate innerLowerFlange =
                FachwerkLowerRigelNodeTeklaAdapter.CreateLowerFlangeFragment(
                    innerFlange,
                    innerFlangeSplice,
                    componentId);
            created.Add(innerLowerFlange);
            weldParts.Add(
                new KeyValuePair<string, Part>(
                    "inner-lower-flange",
                    innerLowerFlange));
            ContourPlate outerInsert =
                FachwerkLowerRigelNodeTeklaAdapter.CreateFlangeInsert(
                    outerFlange,
                    outerFlangeSplice,
                    componentId);
            created.Add(outerInsert);
            ContourPlate innerInsert =
                FachwerkLowerRigelNodeTeklaAdapter.CreateFlangeInsert(
                    innerFlange,
                    innerFlangeSplice,
                    componentId);
            created.Add(innerInsert);

            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateFitting(
                    outerFlange,
                    montage.UpperFlangePlane,
                    outerFlangeSplice.InsertEndAxisPoint,
                    "outer-upper-flange-fitting",
                    componentId));
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateFitting(
                    innerFlange,
                    montage.UpperFlangePlane,
                    innerFlangeSplice.InsertEndAxisPoint,
                    "inner-upper-flange-fitting",
                    componentId));

            AddUpperWebFittings(
                created,
                leftWeb,
                rightWeb,
                montage,
                componentId);
            AddMontageBevels(
                created,
                leftWeb,
                rightWeb,
                outerFlange,
                innerFlange,
                outerInsert,
                innerInsert,
                montage,
                componentId);

            AddTubeWelds(
                created,
                weldParts,
                leftTube,
                rightTube,
                componentId);

            if (createControlLine)
            {
                created.Add(
                    FachwerkLowerRigelNodeTeklaAdapter.CreateControlLine(
                        layout,
                        componentId));
            }

            Trace(
                "OK component=" +
                componentId.ToString(CultureInfo.InvariantCulture) +
                " centralAxis=source-intersections" +
                " inputs=" +
                string.Join(
                    ",",
                    new[]
                    {
                        ObjectId(leftTube),
                        ObjectId(rightTube),
                        ObjectId(outerFlange),
                        ObjectId(innerFlange),
                        ObjectId(leftWeb),
                        ObjectId(rightWeb),
                    }) +
                " children=" +
                created.Count.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception exception)
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            if (tubesModified)
            {
                try
                {
                    FachwerkLowerRigelNodeTeklaAdapter.RestoreBeam(
                        leftTube,
                        leftState);
                }
                catch { }
                try
                {
                    FachwerkLowerRigelNodeTeklaAdapter.RestoreBeam(
                        rightTube,
                        rightState);
                }
                catch { }
            }
            Trace("ERROR " + exception);
            return false;
        }
        finally
        {
            if (workPlaneHandler != null && previousPlane != null)
            {
                try
                {
                    workPlaneHandler.SetCurrentTransformationPlane(
                        previousPlane);
                }
                catch { }
            }
        }
    }

    private static void AddTubeWelds(
        ICollection<ModelObject> created,
        IEnumerable<KeyValuePair<string, Part>> weldParts,
        Part leftTube,
        Part rightTube,
        int componentId)
    {
        foreach (KeyValuePair<string, Part> item in weldParts)
        {
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateShopWeld(
                    leftTube,
                    item.Value,
                    item.Key + "-left-tube-weld",
                    componentId));
            created.Add(
                FachwerkLowerRigelNodeTeklaAdapter.CreateShopWeld(
                    rightTube,
                    item.Value,
                    item.Key + "-right-tube-weld",
                    componentId));
        }
    }

    private static void AddUpperWebFittings(
        ICollection<ModelObject> created,
        Part leftWeb,
        Part rightWeb,
        FachwerkLowerRigelMontageLayout montage,
        int componentId)
    {
        Point keepPoint = FachwerkLowerRigelNodeGeometry.Add(
            montage.LowerFlangePlane.Origin,
            montage.UpwardDirection,
            100.0);
        created.Add(
            FachwerkLowerRigelNodeTeklaAdapter.CreateFitting(
                leftWeb,
                montage.LowerFlangePlane,
                keepPoint,
                "left-upper-web-fitting",
                componentId));
        created.Add(
            FachwerkLowerRigelNodeTeklaAdapter.CreateFitting(
                rightWeb,
                montage.LowerFlangePlane,
                keepPoint,
                "right-upper-web-fitting",
                componentId));
    }

    private static void AddMontageBevels(
        ICollection<ModelObject> created,
        Part leftWeb,
        Part rightWeb,
        Part outerFlange,
        Part innerFlange,
        Part outerInsert,
        Part innerInsert,
        FachwerkLowerRigelMontageLayout montage,
        int componentId)
    {
        AddPairBevels(
            created,
            montage.LowerFlangePlane,
            new FachwerkConnectionPart(
                "left-upper-web",
                "left-web",
                leftWeb),
            new FachwerkConnectionPart(
                "right-upper-web",
                "right-web",
                rightWeb),
            componentId);
        AddPairBevels(
            created,
            montage.UpperFlangePlane,
            new FachwerkConnectionPart(
                "outer-upper-flange",
                "outer-flange",
                outerFlange),
            new FachwerkConnectionPart(
                "inner-upper-flange",
                "inner-flange",
                innerFlange),
            componentId);
        AddPairBevels(
            created,
            montage.LowerFlangePlane,
            new FachwerkConnectionPart(
                "outer-insert",
                "outer-flange",
                outerInsert),
            new FachwerkConnectionPart(
                "inner-insert",
                "inner-flange",
                innerInsert),
            componentId);
    }

    private static void AddPairBevels(
        ICollection<ModelObject> created,
        FachwerkLowerRigelPlane plane,
        FachwerkConnectionPart first,
        FachwerkConnectionPart second,
        int componentId)
    {
        var fittingPlane = new FachwerkRigelFacePlane(
            new Point(plane.Origin),
            new Vector(plane.AxisX),
            new Vector(plane.AxisY),
            new Vector(plane.Normal),
            Array.Empty<Point>());
        IReadOnlyList<FachwerkBevelSection> sections =
            FachwerkColumnRigelConnectionGeometry.BuildBevelSections(
                fittingPlane,
                new[] { first, second });
        foreach (FachwerkBevelSection section in sections)
        {
            created.Add(
                FachwerkColumnRigelConnectionTeklaAdapter.CreateBevel(
                    section,
                    MontageBevelAngleDeg,
                    MontageBevelRootFace,
                    componentId));
        }
    }

    private static IReadOnlyList<Part> ReadInputs(
        Model model,
        IReadOnlyList<InputDefinition> input)
    {
        if (input == null || input.Count != InputRoles.Length)
        {
            throw new InvalidOperationException(
                "Компоненту нужны две трубы, два пояса и две стенки.");
        }

        var result = new List<Part>(input.Count);
        var ids = new HashSet<int>();
        for (var index = 0; index < input.Count; index++)
        {
            if (!(input[index].GetInput() is Identifier identifier) ||
                identifier.ID <= 0)
            {
                throw new InvalidOperationException(
                    "Не удалось восстановить вход '" +
                    InputRoles[index] + "'.");
            }
            if (!ids.Add(identifier.ID))
            {
                throw new InvalidOperationException(
                    "Одна деталь выбрана для нескольких ролей.");
            }
            if (!(model.SelectModelObject(identifier) is Part part))
            {
                throw new InvalidOperationException(
                    "Вход '" + InputRoles[index] +
                    "' отсутствует в модели.");
            }
            result.Add(part);
        }
        return result;
    }

    private static Beam RequireBeam(
        ModelObject modelObject,
        string label)
    {
        if (modelObject is Beam beam) return beam;
        throw new InvalidOperationException(
            label + " должна быть обычной балкой Tekla.");
    }

    private static Part RequirePart(
        ModelObject modelObject,
        string label)
    {
        if (modelObject is Part part) return part;
        throw new InvalidOperationException(
            label + " должна быть деталью Tekla.");
    }

    private static void ValidateUnique(params Part[] parts)
    {
        var identifiers = new HashSet<int>();
        foreach (Part part in parts)
        {
            if (!identifiers.Add(part.Identifier.ID))
            {
                throw new InvalidOperationException(
                    "Одна деталь выбрана несколько раз.");
            }
        }
    }

    private static double Positive(
        double value,
        double fallback,
        string label)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value <= -1_000_000 ||
            Math.Abs(value) < 1e-9)
        {
            return fallback;
        }
        if (value < 0)
        {
            throw new InvalidOperationException(
                label + " должен быть больше нуля.");
        }
        return value;
    }

    private static double CommonLength(
        double canonical,
        double legacy,
        double fallback)
    {
        if (!IsUnsetNumber(canonical))
        {
            return Positive(
                canonical,
                fallback,
                "Длина переходных пластин");
        }
        return Positive(
            legacy,
            fallback,
            "Длина переходных пластин");
    }

    private static bool IsUnsetNumber(double value)
    {
        return double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value <= -1_000_000 ||
            Math.Abs(value) < 1e-9;
    }

    private static string Normalize(
        string value,
        string fallback)
    {
        return IsUnsetText(value)
            ? fallback
            : value.Trim();
    }

    private static bool Enabled(
        string value,
        bool fallback)
    {
        if (IsUnsetText(value)) return fallback;
        string normalized = value.Trim();
        return
            string.Equals(
                normalized,
                "YES",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                normalized,
                "ДА",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "1", StringComparison.Ordinal);
    }

    private static bool IsUnsetText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            string.Equals(
                value.Trim(),
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static string Invariant(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Point Midpoint(Point first, Point second)
    {
        return new Point(
            (first.X + second.X) * 0.5,
            (first.Y + second.Y) * 0.5,
            (first.Z + second.Z) * 0.5);
    }

    private static string ObjectId(ModelObject modelObject)
    {
        return modelObject?.Identifier == null
            ? "0"
            : modelObject.Identifier.ID.ToString(
                CultureInfo.InvariantCulture);
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(
                    Path.GetTempPath(),
                    "fachwerk_lower_rigel_node_trace.txt"),
                DateTime.Now.ToString(
                    "s",
                    CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}
