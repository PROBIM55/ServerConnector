#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

[Plugin("FachwerkColumnRigelConnectionPlugin")]
[PluginUserInterface(
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelConnectionForm")]
[PluginName("ENU", "Примыкание стойки к ригелю")]
[PluginDescription(
    "ENU",
    "Подгонка верхней и нижней секций стойки к фактическим граням ригеля")]
[InputObjectDependency(InputObjectDependency.NOT_DEPENDENT_MODIFIABLE)]
public sealed class FachwerkColumnRigelConnectionPlugin : PluginBase
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

    private readonly FachwerkColumnRigelConnectionPluginData _data;

    public FachwerkColumnRigelConnectionPlugin(
        FachwerkColumnRigelConnectionPluginData data)
    {
        _data = data ?? new FachwerkColumnRigelConnectionPluginData();
    }

    public override List<InputDefinition> DefineInput()
    {
        var picker = new Picker();
        var result = new List<InputDefinition>(InputRoles.Length);
        ModelObject selectedRigel = picker.PickObject(
            Picker.PickObjectEnum.PICK_ONE_PART,
            "Выберите ригель");
        if (!(selectedRigel is Part rigel))
        {
            throw new InvalidOperationException(
                "Выбранный ригель не является деталью Tekla.");
        }
        result.Add(new InputDefinition(rigel.Identifier));

        var usedIds = new HashSet<int> { rigel.Identifier.ID };
        AppendPickedSection(
            picker,
            result,
            usedIds,
            "верхней");
        AppendPickedSection(
            picker,
            result,
            usedIds,
            "нижней");
        return result;
    }

    private static void AppendPickedSection(
        Picker picker,
        ICollection<InputDefinition> result,
        ISet<int> usedIds,
        string sectionLabel)
    {
        ModelObjectEnumerator picked = picker.PickObjects(
            Picker.PickObjectsEnum.PICK_N_PARTS,
            "Рамкой или кликами выберите 4 детали " +
            sectionLabel +
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

        var pickedIds = new List<int>(parts.Count);
        foreach (Part part in parts)
            pickedIds.Add(part.Identifier.ID);
        ValidatePickedSectionIds(pickedIds, usedIds, sectionLabel);

        foreach (Part part in parts)
            result.Add(new InputDefinition(part.Identifier));
    }

    internal static void ValidatePickedSectionIds(
        IReadOnlyList<int> pickedIds,
        ISet<int> usedIds,
        string sectionLabel)
    {
        if (pickedIds == null)
            throw new ArgumentNullException(nameof(pickedIds));
        if (usedIds == null)
            throw new ArgumentNullException(nameof(usedIds));
        if (pickedIds.Count != SectionPartCount)
        {
            throw new InvalidOperationException(
                "Для " + sectionLabel + " секции выбрано " +
                pickedIds.Count.ToString(CultureInfo.InvariantCulture) +
                " деталей. Требуется ровно " +
                SectionPartCount.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var groupIds = new HashSet<int>();
        foreach (int id in pickedIds)
        {
            if (id <= 0 || !groupIds.Add(id))
            {
                throw new InvalidOperationException(
                    "В " + sectionLabel +
                    " секции одна деталь выбрана несколько раз.");
            }
            if (usedIds.Contains(id))
            {
                throw new InvalidOperationException(
                    "Деталь ID " +
                    id.ToString(CultureInfo.InvariantCulture) +
                    " уже выбрана как ригель или в другой секции.");
            }
        }

        foreach (int id in groupIds)
            usedIds.Add(id);
    }

    public override bool Run(List<InputDefinition> input)
    {
        var created = new List<ModelObject>();
        try
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                throw new InvalidOperationException(
                    "Нет соединения с открытой моделью Tekla.");
            }

            IReadOnlyList<FachwerkConnectionPart> parts =
                ReadInputParts(model, input);
            Part rigel = parts[0].Part;
            var upperParts = Slice(parts, 1, 4);
            var lowerParts = Slice(parts, 5, 4);
            int componentId = Identifier == null ? 0 : Identifier.ID;
            double penetration = ValidRange(
                _data.UpperPenetration,
                30,
                0,
                1000,
                "Заход верхних деталей");
            bool createBevels = !string.Equals(
                NormalizeString(_data.BevelEnabled, "YES"),
                "NO",
                StringComparison.OrdinalIgnoreCase);

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
                FachwerkRigelFacePlane upperFitPlane =
                    rigelFaces.Top.Offset(-penetration);
                FachwerkRigelFacePlane lowerFitPlane = rigelFaces.Bottom;

                FachwerkColumnRigelConnectionGeometry.ValidateSectionSide(
                    upperParts,
                    upperFitPlane,
                    "верхней");
                FachwerkColumnRigelConnectionGeometry.ValidateSectionSide(
                    lowerParts,
                    lowerFitPlane,
                    "нижней");

                foreach (FachwerkConnectionPart part in upperParts)
                {
                    created.Add(
                        FachwerkColumnRigelConnectionTeklaAdapter.CreateFitting(
                            part,
                            upperFitPlane,
                            componentId));
                }
                foreach (FachwerkConnectionPart part in lowerParts)
                {
                    created.Add(
                        FachwerkColumnRigelConnectionTeklaAdapter.CreateFitting(
                            part,
                            lowerFitPlane,
                            componentId));
                }

                if (createBevels)
                {
                    IReadOnlyList<FachwerkBevelSection> bevelSections =
                        FachwerkColumnRigelConnectionGeometry
                            .BuildLowerBevelSections(
                                lowerFitPlane,
                                lowerParts);
                    foreach (FachwerkBevelSection section in bevelSections)
                    {
                        FachwerkRigelConnectionBevelParameters parameters =
                            ResolveBevelParameters(
                                _data,
                                section.SemanticRole);
                        created.Add(
                            FachwerkColumnRigelConnectionTeklaAdapter
                                .CreateBevel(
                                    section,
                                    parameters.AngleDeg,
                                    parameters.RootFace,
                                    componentId));
                    }
                }

                Trace(
                    "OK component=" +
                    componentId.ToString(CultureInfo.InvariantCulture) +
                    " rigel=" + ObjectId(rigel) +
                    " penetration=" +
                    penetration.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " fittings=8 bevels=" +
                    (createBevels ? "4" : "0"));
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
            return false;
        }
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
                    "Не удалось восстановить вход '" +
                    InputRoles[index] + "'.");
            }
            if (!identifiers.Add(identifier.ID))
            {
                throw new InvalidOperationException(
                    "Одна деталь выбрана для нескольких ролей: ID " +
                    identifier.ID.ToString(CultureInfo.InvariantCulture) + ".");
            }
            if (!(model.SelectModelObject(identifier) is Part part))
            {
                throw new InvalidOperationException(
                    "Вход '" + InputRoles[index] +
                    "' больше не существует в модели.");
            }
            result.Add(new FachwerkConnectionPart(
                InputRoles[index],
                ReadSemanticRole(part),
                part));
        }
        return result;
    }

    internal static FachwerkRigelConnectionBevelParameters
        ResolveBevelParameters(
            FachwerkColumnRigelConnectionPluginData data,
            string semanticRole)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        double commonAngle = ValidRange(
            data.BevelAngle,
            40,
            1,
            89,
            "Угол разделки наружного пояса");
        double commonRoot = ValidRange(
            data.BevelRootFace,
            2,
            0,
            100,
            "Притупление наружного пояса");
        string role = string.IsNullOrWhiteSpace(semanticRole)
            ? string.Empty
            : semanticRole.Trim().ToLowerInvariant();

        switch (role)
        {
            case "outer-flange":
                return new FachwerkRigelConnectionBevelParameters(
                    commonAngle,
                    commonRoot);
            case "inner-flange":
                return ResolveDetailBevelParameters(
                    data.InnerFlangeBevelAngle,
                    data.InnerFlangeBevelRootFace,
                    commonAngle,
                    commonRoot,
                    "внутреннего пояса");
            case "outer-web":
                return ResolveDetailBevelParameters(
                    data.LeftWebBevelAngle,
                    data.LeftWebBevelRootFace,
                    commonAngle,
                    commonRoot,
                    "левой стенки");
            case "inner-web":
                return ResolveDetailBevelParameters(
                    data.RightWebBevelAngle,
                    data.RightWebBevelRootFace,
                    commonAngle,
                    commonRoot,
                    "правой стенки");
            default:
                throw new InvalidOperationException(
                    "У детали нижней секции не задан поддерживаемый FK_ROLE: '" +
                    role + "'. Ожидались outer-flange, inner-flange, " +
                    "outer-web или inner-web.");
        }
    }

    private static FachwerkRigelConnectionBevelParameters
        ResolveDetailBevelParameters(
            double angle,
            double rootFace,
            double fallbackAngle,
            double fallbackRootFace,
            string label)
    {
        if (IsUnsetDetailAngle(angle))
        {
            return new FachwerkRigelConnectionBevelParameters(
                fallbackAngle,
                fallbackRootFace);
        }

        return new FachwerkRigelConnectionBevelParameters(
            ValidRange(
                angle,
                fallbackAngle,
                1,
                89,
                "Угол разделки " + label),
            ValidRange(
                rootFace,
                fallbackRootFace,
                0,
                100,
                "Притупление " + label));
    }

    private static bool IsUnsetDetailAngle(double value)
    {
        return double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value == double.MinValue ||
            value == int.MinValue ||
            value <= 0;
    }

    private static string ReadSemanticRole(Part part)
    {
        string role = string.Empty;
        try
        {
            part.GetUserProperty("FK_ROLE", ref role);
        }
        catch
        {
            role = string.Empty;
        }
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
        {
            result.Add(source[start + index]);
        }
        return result;
    }

    private static double ValidRange(
        double value,
        double fallback,
        double minimum,
        double maximum,
        string label)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value == double.MinValue ||
            value == int.MinValue)
        {
            value = fallback;
        }
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                label + " должен находиться в диапазоне " +
                minimum.ToString(CultureInfo.InvariantCulture) +
                ".." +
                maximum.ToString(CultureInfo.InvariantCulture) +
                " мм/град.");
        }
        return value;
    }

    private static string NormalizeString(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(
                value.Trim(),
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return fallback;
        }
        return value.Trim();
    }

    private static string ObjectId(ModelObject value)
    {
        return (value?.Identifier?.ID ?? 0).ToString(
            CultureInfo.InvariantCulture);
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(
                    Path.GetTempPath(),
                    "fachwerk_column_rigel_connection_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}

internal sealed class FachwerkRigelConnectionBevelParameters
{
    internal FachwerkRigelConnectionBevelParameters(
        double angleDeg,
        double rootFace)
    {
        AngleDeg = angleDeg;
        RootFace = rootFace;
    }

    public double AngleDeg { get; }
    public double RootFace { get; }
}
