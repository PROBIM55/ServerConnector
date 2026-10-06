#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Tekla.Structures;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

[Plugin("FachwerkColumnRigelInsertConnectionPlugin")]
[PluginUserInterface(
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionForm")]
[PluginName("ENU", "Примыкание стойки к ригелю со вставками")]
[PluginDescription(
    "ENU",
    "Подгонка двух секций стойки к ригелю, вставки по поясам и одной стенке")]
[InputObjectDependency(InputObjectDependency.NOT_DEPENDENT_MODIFIABLE)]
public sealed class FachwerkColumnRigelInsertConnectionPlugin : PluginBase
{
    private const int SectionPartCount = 4;
    private static readonly string[] InputRoles =
    {
        "rigel",
        "upper-part-1",
        "upper-part-2",
        "upper-part-3",
        "upper-part-4",
        "lower-part-1",
        "lower-part-2",
        "lower-part-3",
        "lower-part-4",
    };

    private readonly FachwerkColumnRigelInsertConnectionPluginData _data;

    public FachwerkColumnRigelInsertConnectionPlugin(
        FachwerkColumnRigelInsertConnectionPluginData data)
    {
        _data = data ?? new FachwerkColumnRigelInsertConnectionPluginData();
    }

    public override List<InputDefinition> DefineInput()
    {
        var picker = new Picker();
        var result = new List<InputDefinition>(InputRoles.Length);
        ModelObject selectedRigel = picker.PickObject(
            Picker.PickObjectEnum.PICK_ONE_PART,
            "Выберите ригель");
        if (!(selectedRigel is Part rigel))
            throw new InvalidOperationException("Выбранный ригель не является деталью Tekla.");
        result.Add(new InputDefinition(rigel.Identifier));

        var usedIds = new HashSet<int> { rigel.Identifier.ID };
        AppendPickedSection(picker, result, usedIds, "верхней");
        AppendPickedSection(picker, result, usedIds, "нижней");
        return result;
    }

    public override bool Run(List<InputDefinition> input)
    {
        var created = new List<ModelObject>();
        try
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
                throw new InvalidOperationException("Нет соединения с открытой моделью Tekla.");

            IReadOnlyList<FachwerkConnectionPart> inputParts =
                ReadInputParts(model, input);
            Part rigel = inputParts[0].Part;
            IReadOnlyList<FachwerkConnectionPart> upper = Slice(inputParts, 1, 4);
            IReadOnlyList<FachwerkConnectionPart> lower = Slice(inputParts, 5, 4);
            int componentId = Identifier == null ? 0 : Identifier.ID;
            string selectedWebRole =
                FachwerkColumnRigelInsertConnectionGeometry.NormalizeSelectedWebRole(
                    _data.InsertSide);
            string upperControlFlange =
                FachwerkColumnRigelInsertConnectionGeometry.NormalizeControlFlangeRole(
                    _data.UpperControlFlange);
            string lowerControlFlange =
                FachwerkColumnRigelInsertConnectionGeometry.NormalizeControlFlangeRole(
                    _data.LowerControlFlange);
            double flangeHeight = Positive(
                _data.MinimumFlangeHeight,
                250,
                50,
                2000,
                "Минимальная высота вставки пояса");
            double webExtra = NonNegative(
                _data.WebExtraHeight,
                150,
                0,
                2000,
                "Превышение вставки стенки");
            double overlap = NonNegative(
                _data.PartOverlap,
                30,
                0,
                500,
                "Заход детали во вставку");
            FachwerkRigelInsertBevelParameters bevels = ResolveBevelParameters(_data);
            FachwerkRigelInsertDepthModes depthModes = ResolveDepthModes(_data);

            WorkPlaneHandler workPlaneHandler = model.GetWorkPlaneHandler();
            TransformationPlane previousPlane =
                workPlaneHandler.GetCurrentTransformationPlane();
            try
            {
                if (!workPlaneHandler.SetCurrentTransformationPlane(
                        new TransformationPlane()))
                {
                    throw new InvalidOperationException(
                        "Tekla не установила глобальную рабочую плоскость.");
                }

                FachwerkRigelFacePair rigelFaces =
                    FachwerkColumnRigelConnectionGeometry.ReadRigelFaces(rigel);
                FachwerkRigelInsertConnectionLayout layout =
                    FachwerkColumnRigelInsertConnectionGeometry.Build(
                        rigelFaces,
                        upper,
                        lower,
                        selectedWebRole,
                        upperControlFlange,
                        lowerControlFlange,
                        flangeHeight,
                        webExtra,
                        overlap);
                CreateSection(
                    layout.Upper,
                    bevels,
                    depthModes,
                    componentId,
                    created);
                CreateSection(
                    layout.Lower,
                    bevels,
                    depthModes,
                    componentId,
                    created);

                Trace(
                    "OK component=" +
                    componentId.ToString(CultureInfo.InvariantCulture) +
                    " rigel=" + rigel.Identifier.ID +
                    " side=" + selectedWebRole +
                    " upperControl=" + upperControlFlange +
                    " lowerControl=" + lowerControlFlange +
                    " flangeHeight=" +
                    flangeHeight.ToString("0.###", CultureInfo.InvariantCulture) +
                    " webExtra=" +
                    webExtra.ToString("0.###", CultureInfo.InvariantCulture) +
                    " overlap=" +
                    overlap.ToString("0.###", CultureInfo.InvariantCulture));
            }
            finally
            {
                workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
            }
            return true;
        }
        catch (Exception exception)
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            Trace("ERROR " + exception);
            MessageBox.Show(
                exception.Message,
                "Примыкание стойки к ригелю со вставками",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    private static void CreateSection(
        FachwerkRigelInsertSectionLayout layout,
        FachwerkRigelInsertBevelParameters parameters,
        FachwerkRigelInsertDepthModes depthModes,
        int componentId,
        ICollection<ModelObject> created)
    {
        foreach (KeyValuePair<string, FachwerkRigelInsertPartFrame> item in layout.Frames)
        {
            FachwerkRigelFacePlane fittingPlane;
            if (item.Key == "outer-flange" || item.Key == "inner-flange")
                fittingPlane = layout.FlangeFitPlane;
            else if (item.Key == layout.SelectedWebRole)
                fittingPlane = layout.WebFitPlane;
            else
                fittingPlane = layout.RigelPlane;
            created.Add(
                FachwerkColumnRigelConnectionTeklaAdapter.CreateFitting(
                    item.Value.Source,
                    fittingPlane,
                    componentId));
        }

        var insertedParts = new Dictionary<string, Part>(
            StringComparer.OrdinalIgnoreCase);
        foreach (FachwerkRigelInsertPlateSpec insert in layout.Inserts)
        {
            ContourPlate plate =
                FachwerkColumnRigelInsertConnectionTeklaAdapter.CreateInsert(
                    insert,
                    depthModes.For(layout.SectionRole, insert.SemanticRole),
                    componentId);
            created.Add(plate);
            insertedParts.Add(insert.OutputRole, plate);
        }

        IReadOnlyList<FachwerkRigelInsertBevelSpec> bevelSpecs =
            FachwerkColumnRigelInsertConnectionGeometry.BuildBevels(
                layout,
                insertedParts);
        foreach (FachwerkRigelInsertBevelSpec bevel in bevelSpecs)
        {
            double angle = parameters.Angle(bevel.Kind);
            double rootFace = bevel.Kind == FachwerkRigelInsertBevelKind.LongitudinalWeb
                ? FachwerkColumnRigelInsertConnectionGeometry.LongitudinalRootFace(
                    bevel.Section.Thickness)
                : parameters.RootFace;
            created.Add(
                FachwerkColumnRigelConnectionTeklaAdapter.CreateBevel(
                    bevel.Section,
                    angle,
                    rootFace,
                    componentId));
        }
    }

    private static void AppendPickedSection(
        Picker picker,
        ICollection<InputDefinition> result,
        ISet<int> usedIds,
        string sectionLabel)
    {
        ModelObjectEnumerator picked = picker.PickObjects(
            Picker.PickObjectsEnum.PICK_N_PARTS,
            "Рамкой или кликами выберите 4 детали " + sectionLabel +
            " секции, затем нажмите среднюю кнопку мыши");
        var parts = new List<Part>();
        while (picked.MoveNext())
        {
            if (!(picked.Current is Part part))
            {
                throw new InvalidOperationException(
                    "В выбор " + sectionLabel +
                    " секции попал объект, который не является деталью Tekla.");
            }
            parts.Add(part);
        }
        var ids = new List<int>(parts.Count);
        foreach (Part part in parts)
            ids.Add(part.Identifier.ID);
        FachwerkColumnRigelConnectionPlugin.ValidatePickedSectionIds(
            ids,
            usedIds,
            sectionLabel);
        foreach (Part part in parts)
            result.Add(new InputDefinition(part.Identifier));
    }

    private static IReadOnlyList<FachwerkConnectionPart> ReadInputParts(
        Model model,
        IReadOnlyList<InputDefinition> input)
    {
        if (input == null || input.Count != InputRoles.Length)
        {
            throw new InvalidOperationException(
                "Компоненту нужны ригель и восемь деталей стойки.");
        }
        var result = new List<FachwerkConnectionPart>(InputRoles.Length);
        var identifiers = new HashSet<int>();
        for (var index = 0; index < input.Count; index++)
        {
            if (!(input[index].GetInput() is Identifier identifier))
            {
                throw new InvalidOperationException(
                    "Не удалось восстановить вход '" + InputRoles[index] + "'.");
            }
            if (!identifiers.Add(identifier.ID))
            {
                throw new InvalidOperationException(
                    "Одна деталь выбрана для нескольких ролей: ID " +
                    identifier.ID + ".");
            }
            if (!(model.SelectModelObject(identifier) is Part part))
            {
                throw new InvalidOperationException(
                    "Вход '" + InputRoles[index] + "' больше не существует в модели.");
            }
            result.Add(new FachwerkConnectionPart(
                InputRoles[index],
                index == 0 ? "rigel" : ReadSemanticRole(part),
                part));
        }
        return result;
    }

    internal static FachwerkRigelInsertBevelParameters ResolveBevelParameters(
        FachwerkColumnRigelInsertConnectionPluginData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return new FachwerkRigelInsertBevelParameters(
            Positive(data.InsertFlangeAngle, 40, 1, 89, "Угол вставки пояса"),
            Positive(data.PartFlangeAngle, 40, 1, 89, "Угол пояса стойки"),
            Positive(data.InsertWebAngle, 40, 1, 89, "Угол вставки стенки"),
            Positive(data.PartWebAngle, 40, 1, 89, "Угол стенки со вставкой"),
            Positive(data.DirectWebAngle, 40, 1, 89, "Угол стенки без вставки"),
            NonNegative(data.RootFace, 2, 0, 24.9, "Притупление"));
    }

    internal static FachwerkRigelInsertDepthModes ResolveDepthModes(
        FachwerkColumnRigelInsertConnectionPluginData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return new FachwerkRigelInsertDepthModes(
            data.UpperOuterFlangeDepth,
            data.UpperInnerFlangeDepth,
            data.UpperWebDepth,
            data.LowerOuterFlangeDepth,
            data.LowerInnerFlangeDepth,
            data.LowerWebDepth);
    }

    private static string ReadSemanticRole(Part part)
    {
        string role = string.Empty;
        try { part.GetUserProperty("FK_ROLE", ref role); }
        catch { role = string.Empty; }
        return string.IsNullOrWhiteSpace(role)
            ? string.Empty
            : role.Trim().ToLowerInvariant();
    }

    private static IReadOnlyList<FachwerkConnectionPart> Slice(
        IReadOnlyList<FachwerkConnectionPart> source,
        int start,
        int count)
    {
        var result = new List<FachwerkConnectionPart>(count);
        for (var index = 0; index < count; index++)
            result.Add(source[start + index]);
        return result;
    }

    private static double Positive(
        double value,
        double fallback,
        double minimum,
        double maximum,
        string label)
    {
        if (IsUnset(value) || value == 0)
            value = fallback;
        return InRange(value, minimum, maximum, label);
    }

    private static double NonNegative(
        double value,
        double fallback,
        double minimum,
        double maximum,
        string label)
    {
        if (IsUnset(value))
            value = fallback;
        return InRange(value, minimum, maximum, label);
    }

    private static double InRange(
        double value,
        double minimum,
        double maximum,
        string label)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                label + " должно быть в диапазоне " + minimum + "..." + maximum + ".");
        }
        return value;
    }

    private static bool IsUnset(double value) =>
        double.IsNaN(value) ||
        double.IsInfinity(value) ||
        value == double.MinValue ||
        value == int.MinValue;

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(
                    Path.GetTempPath(),
                    "fachwerk_column_rigel_insert_connection_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}

internal sealed class FachwerkRigelInsertBevelParameters
{
    internal FachwerkRigelInsertBevelParameters(
        double insertFlangeAngle,
        double partFlangeAngle,
        double insertWebAngle,
        double partWebAngle,
        double directWebAngle,
        double rootFace)
    {
        InsertFlangeAngle = insertFlangeAngle;
        PartFlangeAngle = partFlangeAngle;
        InsertWebAngle = insertWebAngle;
        PartWebAngle = partWebAngle;
        DirectWebAngle = directWebAngle;
        RootFace = rootFace;
    }

    public double InsertFlangeAngle { get; }
    public double PartFlangeAngle { get; }
    public double InsertWebAngle { get; }
    public double PartWebAngle { get; }
    public double DirectWebAngle { get; }
    public double RootFace { get; }

    internal double Angle(FachwerkRigelInsertBevelKind kind)
    {
        switch (kind)
        {
            case FachwerkRigelInsertBevelKind.InsertFlange:
                return InsertFlangeAngle;
            case FachwerkRigelInsertBevelKind.PartFlange:
                return PartFlangeAngle;
            case FachwerkRigelInsertBevelKind.InsertWeb:
                return InsertWebAngle;
            case FachwerkRigelInsertBevelKind.PartWeb:
                return PartWebAngle;
            case FachwerkRigelInsertBevelKind.DirectWeb:
                return DirectWebAngle;
            case FachwerkRigelInsertBevelKind.LongitudinalWeb:
                return 45;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}

internal sealed class FachwerkRigelInsertDepthModes
{
    internal FachwerkRigelInsertDepthModes(
        string upperOuterFlange,
        string upperInnerFlange,
        string upperWeb,
        string lowerOuterFlange,
        string lowerInnerFlange,
        string lowerWeb)
    {
        UpperOuterFlange = Normalize(upperOuterFlange);
        UpperInnerFlange = Normalize(upperInnerFlange);
        UpperWeb = Normalize(upperWeb);
        LowerOuterFlange = Normalize(lowerOuterFlange);
        LowerInnerFlange = Normalize(lowerInnerFlange);
        LowerWeb = Normalize(lowerWeb);
    }

    public FachwerkRigelInsertDepthMode UpperOuterFlange { get; }
    public FachwerkRigelInsertDepthMode UpperInnerFlange { get; }
    public FachwerkRigelInsertDepthMode UpperWeb { get; }
    public FachwerkRigelInsertDepthMode LowerOuterFlange { get; }
    public FachwerkRigelInsertDepthMode LowerInnerFlange { get; }
    public FachwerkRigelInsertDepthMode LowerWeb { get; }

    internal FachwerkRigelInsertDepthMode For(
        string sectionRole,
        string semanticRole)
    {
        bool upper = string.Equals(
            sectionRole,
            "upper",
            StringComparison.OrdinalIgnoreCase);
        bool lower = string.Equals(
            sectionRole,
            "lower",
            StringComparison.OrdinalIgnoreCase);
        if (!upper && !lower)
            throw new ArgumentOutOfRangeException(nameof(sectionRole), sectionRole, null);

        if (string.Equals(
                semanticRole,
                "outer-flange",
                StringComparison.OrdinalIgnoreCase))
        {
            return upper ? UpperOuterFlange : LowerOuterFlange;
        }
        if (string.Equals(
                semanticRole,
                "inner-flange",
                StringComparison.OrdinalIgnoreCase))
        {
            return upper ? UpperInnerFlange : LowerInnerFlange;
        }
        if (string.Equals(
                semanticRole,
                "outer-web",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                semanticRole,
                "inner-web",
                StringComparison.OrdinalIgnoreCase))
        {
            return upper ? UpperWeb : LowerWeb;
        }
        throw new ArgumentOutOfRangeException(nameof(semanticRole), semanticRole, null);
    }

    private static FachwerkRigelInsertDepthMode Normalize(string value) =>
        FachwerkColumnRigelInsertConnectionTeklaAdapter.NormalizeDepthMode(value);
}
