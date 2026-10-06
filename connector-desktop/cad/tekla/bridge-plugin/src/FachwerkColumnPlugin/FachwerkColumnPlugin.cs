#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

[Plugin("FachwerkColumnPlugin")]
[PluginUserInterface("Structura.Tekla.Fachwerk.FachwerkColumnForm")]
[PluginName("ENU", "Стойка фахверка")]
[PluginDescription("ENU", "Параметрическая стойка фахверка по траекториям КМ")]
[InputObjectDependency(InputObjectDependency.NOT_DEPENDENT_MODIFIABLE)]
public sealed class FachwerkColumnPlugin : PluginBase
{
    private const string ImplementationMarker = "split-beam-v11-erection-joints";
    private readonly FachwerkColumnPluginData _data;

    public FachwerkColumnPlugin(FachwerkColumnPluginData data)
    {
        _data = data ?? new FachwerkColumnPluginData();
    }

    public override List<InputDefinition> DefineInput()
    {
        var picker = new Picker();
        var insertion = picker.PickPoint("Укажите точку вставки стойки фахверка");
        var orientation = picker.PickPoint("Укажите направление фасада / локальной оси +X стойки");
        return new List<InputDefinition> { new InputDefinition(insertion, orientation) };
    }

    public override bool Run(List<InputDefinition> input)
    {
        var created = new List<ModelObject>();
        var physicalParts = new List<Part>();
        try
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
                throw new InvalidOperationException("Нет соединения с открытой моделью Tekla.");

            var workPlaneHandler = model.GetWorkPlaneHandler();
            var executionPlane = workPlaneHandler.GetCurrentTransformationPlane();
            var frame = FachwerkColumnGeometry.ResolveFrame(input, _data.RotationDeg);
            var globalInsertion = executionPlane.TransformationMatrixToGlobal.Transform(frame.Insertion);
            frame = frame.WithGlobalInsertionZ(globalInsertion.Z);
            var profileKey = FirstNonEmpty(_data.ProfileKey, "СФ1");
            var profile = FachwerkColumnProfileCatalog.Load(_data.CatalogPath).Require(profileKey);
            var breaks = FachwerkColumnGeometry.ReadBreaks(_data);
            var mark = FirstNonEmpty(_data.Mark, profile.Mark, profile.Key, "СФ1");
            var assemblyMark = FirstNonEmpty(profile.Mark, profile.Key, mark);
            var material = FirstNonEmpty(_data.Material, "C355-5");
            var className = FirstNonEmpty(_data.ClassName, "3");
            var componentId = Identifier == null ? 0 : Identifier.ID;
            var componentSemanticKey = FirstNonEmpty(
                _data.ExternalObjectId,
                mark,
                "component-" + componentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var bevelProfile = ResolveBevelProfile(_data.BevelProfile);
            var stiffenerSettings =
                FachwerkColumnGeometry.ReadStiffenerSettings(_data);
            var partSpecs = FachwerkColumnGeometry.BuildSectionParts(profile, frame, breaks);
            var erectionJointSpecs =
                FachwerkColumnGeometry.BuildErectionJointSpecs(
                    profile,
                    frame,
                    breaks,
                    partSpecs);
            var stiffenerSpecs = FachwerkColumnGeometry.BuildStiffenerSpecs(
                profile,
                frame,
                breaks,
                stiffenerSettings);
            if (partSpecs.Count == 0)
                throw new InvalidOperationException("Построитель стойки не создал ни одной физической детали.");
            Trace(
                "RUN impl=" + ImplementationMarker + " profile=" + profileKey +
                " rotation=" + _data.RotationDeg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                " localInsertion=" + FormatPoint(frame.Insertion) +
                " globalInsertion=" + FormatPoint(globalInsertion) +
                " splits=" + (breaks.Count > 0 ? "yes" : "no") +
                " rawBreaks=" + FormatRawBreaks(_data) +
                " localBreaks=" + FormatLocalBreaks(breaks, frame.GlobalInsertionZ) +
                " activeBreaks=" + breaks.Count +
                " bevels=" + (bevelProfile != null ? bevelProfile : "no") +
                " erectionJoints=" + erectionJointSpecs.Count +
                " stiffeners=" + stiffenerSpecs.Count +
                " expected=" + partSpecs.Count +
                " component=" + componentId);

            var previousPlane = executionPlane;
            try
            {
                if (!workPlaneHandler.SetCurrentTransformationPlane(FachwerkColumnGeometry.CreateFacadePlane(frame)))
                    throw new InvalidOperationException("Tekla не установила рабочую плоскость стойки.");

                var webPieceIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var bevelCount = 0;
                var removedDuplicateBevelCount = 0;
                for (var index = 0; index < partSpecs.Count; index++)
                {
                    var partSpec = partSpecs[index];
                    var part = FachwerkColumnGeometry.CreatePart(
                        partSpec,
                        material,
                        className,
                        mark,
                        componentId,
                        index + 1) as Part;
                    if (part == null)
                    {
                        throw new InvalidOperationException(
                            "Физическая деталь стойки не является Beam/PolyBeam Tekla.");
                    }
                    created.Add(part);
                    physicalParts.Add(part);

                    if (bevelProfile == null || partSpec.Kind != FachwerkColumnPartKind.Web)
                    {
                        continue;
                    }

                    int rolePieceIndex;
                    webPieceIndices.TryGetValue(partSpec.Role, out rolePieceIndex);
                    rolePieceIndex++;
                    webPieceIndices[partSpec.Role] = rolePieceIndex;
                    string fragmentSemanticKey = string.Concat(
                        "piece-",
                        rolePieceIndex.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                        "-break-",
                        partSpec.EndBreakIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    IReadOnlyList<FachwerkBevelSpec> bevelSpecs = FachwerkColumnBevelGeometry.BuildForWebPieceEdges(
                        componentSemanticKey,
                        partSpec,
                        fragmentSemanticKey);
                    foreach (FachwerkBevelSpec bevelSpec in bevelSpecs)
                    {
                        Trace(
                            "BEVEL_SPEC impl=" + ImplementationMarker +
                            " component=" + componentId +
                            " webProfile=" + partSpec.Profile +
                            " role=" + bevelSpec.WallRole +
                            " fragment=" + bevelSpec.FragmentSemanticKey +
                            " edge=" + bevelSpec.EdgeRole +
                            " cutter=" + bevelSpec.CutterProfile +
                            " plane=" + bevelSpec.CutterPlane +
                            " depth=" + bevelSpec.CutterDepth +
                            " rotation=" + bevelSpec.CutterRotation +
                            " rotationOffset=" + bevelSpec.CutterRotationOffset.ToString(
                                "0.###",
                                System.Globalization.CultureInfo.InvariantCulture) +
                            " name=" + bevelSpec.CutterName);
                        FachwerkBevelApplyResult bevelResult = FachwerkColumnBevelTeklaAdapter.Replace(
                            model,
                            part,
                            frame,
                            bevelSpec,
                            material);
                        bevelCount++;
                        removedDuplicateBevelCount += bevelResult.RemovedDuplicateCount;
                    }
                }

                Trace(
                    "BEVELS component=" + componentId +
                    " enabled=" + (bevelProfile != null ? "yes" : "no") +
                    " createdOrReused=" + bevelCount +
                    " removedDuplicates=" + removedDuplicateBevelCount);

                FachwerkColumnErectionJointApplyResult erectionResult =
                    FachwerkColumnErectionJointTeklaAdapter.Apply(
                        mark,
                        material,
                        className,
                        componentId,
                        erectionJointSpecs,
                        physicalParts);
                foreach (ModelObject erectionObject in
                    erectionResult.CreatedObjects)
                {
                    created.Add(erectionObject);
                }
                Trace(
                    "ERECTION_JOINTS component=" + componentId +
                    " count=" + erectionJointSpecs.Count +
                    " inserts=" + erectionResult.InsertCount +
                    " bevels=" + erectionResult.BevelCount +
                    " outputs=" + erectionResult.CreatedObjects.Count);

                FachwerkColumnAssemblyPlan assemblyPlan =
                    FachwerkColumnAssemblyPlanner.Build(assemblyMark, partSpecs);
                FachwerkColumnAssemblyApplyResult assemblyResult =
                    FachwerkColumnAssemblyTeklaAdapter.Apply(
                        assemblyMark,
                        partSpecs,
                        physicalParts,
                        assemblyPlan);
                foreach (ModelObject createdAssemblyObject in assemblyResult.CreatedObjects)
                {
                    created.Add(createdAssemblyObject);
                }
                Trace(
                    "ASSEMBLIES component=" + componentId +
                    " mark=" + assemblyMark +
                    " count=" + assemblyResult.AssemblyCount +
                    " weldsCreated=" + assemblyResult.CreatedObjects.Count +
                    " weldsReused=" + assemblyResult.ReusedWeldCount);

                FachwerkColumnStiffenerApplyResult stiffenerResult =
                    FachwerkColumnStiffenerTeklaAdapter.Apply(
                        assemblyMark,
                        className,
                        componentId,
                        stiffenerSpecs,
                        physicalParts,
                        assemblyPlan);
                foreach (ModelObject createdStiffenerObject in
                    stiffenerResult.CreatedObjects)
                {
                    created.Add(createdStiffenerObject);
                }
                Trace(
                    "STIFFENERS component=" + componentId +
                    " count=" + stiffenerResult.StiffenerCount +
                    " outputs=" + stiffenerResult.CreatedObjects.Count);
            }
            finally
            {
                workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
            }

            if (physicalParts.Count != partSpecs.Count)
                throw new InvalidOperationException("Создан не полный комплект деталей стойки.");

            Trace("OK mark=" + mark + " parts=" + physicalParts.Count + " component=" + componentId);
            return true;
        }
        catch (Exception ex)
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            Trace("ERROR " + ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            return false;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null) return null;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value.Trim(), "-2147483648", StringComparison.Ordinal))
            {
                return value;
            }
        }
        return null;
    }

    private static string ResolveBevelProfile(string rawValue)
    {
        var profile = FirstNonEmpty(rawValue);
        if (profile == null)
        {
            return null;
        }

        profile = profile.Trim();
        if (!string.Equals(
                profile,
                FachwerkColumnBevelGeometry.CutterProfile,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Неподдерживаемый профиль разделки '" + profile +
                "'. Для стойки КМ допускается только " +
                FachwerkColumnBevelGeometry.CutterProfile + ".");
        }
        return FachwerkColumnBevelGeometry.CutterProfile;
    }

    private static string FormatRawBreaks(FachwerkColumnPluginData data)
    {
        var values = new[]
        {
            data.Break1Elevation, data.Break2Elevation, data.Break3Elevation, data.Break4Elevation,
            data.Break5Elevation, data.Break6Elevation, data.Break7Elevation, data.Break8Elevation,
        };
        var formatted = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            formatted[index] = values[index].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        return "[" + string.Join(",", formatted) + "]";
    }

    private static string FormatLocalBreaks(IEnumerable<FachwerkColumnBreak> breaks, double globalInsertionZ)
    {
        var formatted = new List<string>();
        if (breaks != null)
        {
            foreach (var value in breaks)
            {
                formatted.Add((value.Elevation - globalInsertionZ).ToString(
                    "0.###",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return "[" + string.Join(",", formatted) + "]";
    }

    private static string FormatPoint(Point value) =>
        value.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," +
        value.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," +
        value.Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private void Trace(string message)
    {
        var tracePath = Path.Combine(Path.GetTempPath(), "fachwerk_column_plugin_trace.txt");
        try { File.AppendAllText(tracePath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); }
        catch { }
    }
}
