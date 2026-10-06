#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

[Plugin("FachwerkRigelPlugin")]
[PluginUserInterface("Structura.Tekla.Fachwerk.FachwerkRigelForm")]
[PluginName("ENU", "Ригели фахверка")]
[PluginDescription("ENU", "Автономные ригели 101, 103 и РС2 по утвержденным осям КМ")]
[InputObjectDependency(InputObjectDependency.NOT_DEPENDENT_MODIFIABLE)]
public sealed class FachwerkRigelPlugin : PluginBase
{
    private const double MinimumLengthMm = 1;
    private readonly FachwerkRigelPluginData _data;

    public FachwerkRigelPlugin(FachwerkRigelPluginData data)
    {
        _data = data ?? new FachwerkRigelPluginData();
    }

    public override List<InputDefinition> DefineInput() => new List<InputDefinition>();

    public override bool Run(List<InputDefinition> input)
    {
        var created = new List<Beam>();
        try
        {
            var model = new Model();
            if (!model.GetConnectionStatus())
                throw new InvalidOperationException("Нет соединения с открытой моделью Tekla.");

            var catalog = FachwerkRigelCatalog.LoadEmbedded();
            FachwerkRigelGeometry.ValidateDefaultGeometry(catalog);
            var workPlaneHandler = model.GetWorkPlaneHandler();
            var previousPlane = workPlaneHandler.GetCurrentTransformationPlane();
            try
            {
                if (!workPlaneHandler.SetCurrentTransformationPlane(new TransformationPlane()))
                    throw new InvalidOperationException("Tekla не установила глобальную рабочую плоскость.");

                foreach (var layout in catalog.Layouts)
                {
                    var points = FachwerkRigelGeometry.ResolveLayout(catalog, layout, _data);
                    var layoutBeams = new List<Beam>();
                    for (var ordinal = 1; ordinal < points.Count; ordinal++)
                    {
                        var start = points[ordinal - 1];
                        var end = points[ordinal];
                        if (Distance(start, end) < MinimumLengthMm)
                            throw new InvalidOperationException(
                                "Сегмент " + layout.Code + "/" + (ordinal - 1) + " короче 1 мм.");
                        var beam = CreateBeam(
                            layout,
                            ordinal - 1,
                            start,
                            end,
                            FirstNonEmpty(_data.Material, layout.Material, "C355-5"),
                            FirstNonEmpty(_data.ClassName, layout.ClassName, "20"));
                        if (!beam.Insert())
                            throw new InvalidOperationException(
                                "Tekla не создала сегмент " + layout.Code + "/" + (ordinal - 1) + ".");
                        ApplyMetadata(beam, layout, ordinal - 1);
                        if (!beam.Modify())
                            throw new InvalidOperationException(
                                "Tekla не сохранила атрибуты сегмента " + layout.Code + "/" + (ordinal - 1) + ".");
                        ApplyAssemblyName(beam, layout, ordinal - 1);
                        created.Add(beam);
                        layoutBeams.Add(beam);
                    }
                    TryDrawVisualization(catalog, layout, points, layoutBeams);
                }
            }
            finally
            {
                workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
            }

            Trace("OK component=" + OwnerId() + " beams=" + created.Count);
            return true;
        }
        catch (Exception exception)
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); } catch { }
            }
            Trace("ERROR " + exception);
            return false;
        }
    }

    private static Beam CreateBeam(
        FachwerkRigelLayout layout,
        int ordinal,
        Point start,
        Point end,
        string material,
        string className)
    {
        var beam = new Beam(start, end)
        {
            Name = FachwerkAttributeMapper.RigelAssemblyName,
            Class = className,
        };
        beam.Profile.ProfileString = layout.Profile;
        beam.Material.MaterialString = material;
        beam.Position.Plane = Position.PlaneEnum.MIDDLE;
        // The authored chain is the top support line. BEHIND keeps the whole
        // plate section below that line instead of centering it on the line.
        beam.Position.Depth = Position.DepthEnum.BEHIND;
        beam.Position.Rotation = Position.RotationEnum.FRONT;
        beam.PartNumber.Prefix = FachwerkAttributeMapper.PartPrefix;
        beam.PartNumber.StartNumber = 1;
        beam.AssemblyNumber.Prefix =
            FachwerkAttributeMapper.BuildRigelAssemblyPrefix(layout.Code);
        beam.AssemblyNumber.StartNumber = ordinal + 1;
        return beam;
    }

    private void ApplyMetadata(Beam beam, FachwerkRigelLayout layout, int ordinal)
    {
        var owner = OwnerId();
        beam.SetUserProperty("STRUCTURA_COMPONENT_TYPE", "FachwerkRigelPart");
        beam.SetUserProperty(
            "STRUCTURA_EXTERNAL_OBJECT_ID",
            "fachwerk-rigel:" + owner + ":" + layout.Code + ":" + ordinal);
        beam.SetUserProperty("FK_OWNER", owner);
        beam.SetUserProperty("FK_ROLE", "rigel");
        beam.SetUserProperty("FK_RIGEL_CODE", layout.Code);
        beam.SetUserProperty("FK_SEGMENT_ORDINAL", ordinal);
        beam.SetUserProperty("FK_FROM_LABEL", layout.Points[ordinal].Label);
        beam.SetUserProperty("FK_TO_LABEL", layout.Points[ordinal + 1].Label);
        beam.SetUserProperty("FK_ELEVATION_REFERENCE", "top");
    }

    private static void ApplyAssemblyName(
        Beam beam,
        FachwerkRigelLayout layout,
        int ordinal)
    {
        Assembly assembly = beam.GetAssembly();
        if (assembly == null)
        {
            throw new InvalidOperationException(
                "Tekla не вернула сборку сегмента " + layout.Code + "/" + ordinal + ".");
        }

        assembly.Name = FachwerkAttributeMapper.RigelAssemblyName;
        if (!assembly.Modify())
        {
            throw new InvalidOperationException(
                "Tekla не сохранила имя сборки сегмента " + layout.Code + "/" + ordinal + ".");
        }
    }

    private string OwnerId() =>
        (Identifier == null ? 0 : Identifier.ID).ToString(CultureInfo.InvariantCulture);

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value.Trim(), int.MinValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return value.Trim();
            }
        }
        return string.Empty;
    }

    private static double Distance(Point first, Point second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        var dz = second.Z - first.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void TryDrawVisualization(
        FachwerkRigelCatalog catalog,
        FachwerkRigelLayout layout,
        IReadOnlyList<Point> points,
        IReadOnlyList<Beam> beams)
    {
        try
        {
            FachwerkRigelVisualization.DrawLayout(layout, points, _data);
            FachwerkRigelClearanceVisualization.DrawLayout(catalog, layout, beams);
        }
        catch (Exception exception)
        {
            Trace("VISUALIZATION_ERROR " + layout.Code + " " + exception);
        }
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "fachwerk_rigel_plugin_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }
        catch { }
    }
}
