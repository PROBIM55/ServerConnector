#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Structura.Tekla.Fachwerk;

internal sealed class FachwerkColumnStiffenerApplyResult
{
    internal FachwerkColumnStiffenerApplyResult(
        IReadOnlyList<ModelObject> createdObjects,
        int stiffenerCount)
    {
        CreatedObjects = createdObjects;
        StiffenerCount = stiffenerCount;
    }

    public IReadOnlyList<ModelObject> CreatedObjects { get; }
    public int StiffenerCount { get; }
}

internal static class FachwerkColumnStiffenerTeklaAdapter
{
    private const string ManagedWeldPrefix = "FKR/";

    internal static FachwerkColumnStiffenerApplyResult Apply(
        string mark,
        string className,
        int componentId,
        IReadOnlyList<FachwerkColumnStiffenerSpec> specs,
        IReadOnlyList<Part> columnParts,
        FachwerkColumnAssemblyPlan assemblyPlan)
    {
        if (specs == null) throw new ArgumentNullException(nameof(specs));
        if (columnParts == null) throw new ArgumentNullException(nameof(columnParts));
        if (assemblyPlan == null) throw new ArgumentNullException(nameof(assemblyPlan));
        if (specs.Count == 0)
        {
            return new FachwerkColumnStiffenerApplyResult(
                Array.Empty<ModelObject>(),
                0);
        }

        var created = new List<ModelObject>();
        try
        {
            foreach (var spec in specs)
            {
                var plate = CreatePlate(
                    mark,
                    className,
                    componentId,
                    spec);
                created.Add(plate);

                var segment = assemblyPlan.Segments.SingleOrDefault(
                    item =>
                        item.AssemblySegmentIndex ==
                        spec.AssemblySegmentIndex);
                if (segment == null)
                {
                    throw new InvalidOperationException(
                        "Для внутреннего ребра " +
                        spec.Index.ToString(CultureInfo.InvariantCulture) +
                        " не найден сборочный сегмент " +
                        spec.AssemblySegmentIndex.ToString(
                            CultureInfo.InvariantCulture) + ".");
                }
                if (segment.MainPartIndex < 0 ||
                    segment.MainPartIndex >= columnParts.Count)
                {
                    throw new InvalidOperationException(
                        "Главная деталь сборочного сегмента внутреннего ребра " +
                        spec.Index.ToString(CultureInfo.InvariantCulture) +
                        " отсутствует.");
                }
                var main = columnParts[segment.MainPartIndex];
                var weld = new Weld
                {
                    MainObject = main,
                    SecondaryObject = plate,
                    ShopWeld = true,
                    ConnectAssemblies = false,
                    ReferenceText = BuildWeldKey(mark, spec),
                };
                if (!weld.Insert())
                {
                    throw new InvalidOperationException(
                        "Tekla не присоединила внутреннее ребро " +
                        spec.Index.ToString(CultureInfo.InvariantCulture) +
                        " к сборке стойки.");
                }
                created.Add(weld);
            }
            return new FachwerkColumnStiffenerApplyResult(
                new ReadOnlyCollection<ModelObject>(created),
                specs.Count);
        }
        catch
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                try { created[index].Delete(); }
                catch { }
            }
            throw;
        }
    }

    private static ContourPlate CreatePlate(
        string mark,
        string className,
        int componentId,
        FachwerkColumnStiffenerSpec spec)
    {
        var plate = new ContourPlate
        {
            Name = "Внутреннее ребро",
            Class = string.IsNullOrWhiteSpace(className)
                ? "3"
                : className.Trim(),
            PartNumber = new NumberingSeries(
                FachwerkAttributeMapper.PartPrefix,
                1),
        };
        plate.Profile.ProfileString = spec.Profile;
        plate.Material.MaterialString = spec.Material;
        plate.Position.Depth = Position.DepthEnum.MIDDLE;

        foreach (var point in spec.Contour)
        {
            var chamfer = point.HasChamfer && spec.ChamferSize > 0
                ? new Chamfer(
                    spec.ChamferSize,
                    spec.ChamferSize,
                    Chamfer.ChamferTypeEnum.CHAMFER_LINE)
                : new Chamfer();
            if (!plate.AddContourPoint(new ContourPoint(
                new Point(point.X, point.Y, point.Z),
                chamfer)))
            {
                throw new InvalidOperationException(
                    "Tekla отклонила вершину внутреннего ребра " +
                    spec.Index.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
        if (!plate.Insert())
        {
            throw new InvalidOperationException(
                "Tekla не создала внутреннее ребро " +
                spec.Index.ToString(CultureInfo.InvariantCulture) +
                " на отметке " +
                spec.Elevation.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) + ".");
        }
        plate.SetUserProperty("FK_ROLE", "stiffener");
        plate.SetUserProperty("FK_MARK", mark ?? string.Empty);
        plate.SetUserProperty("FK_OWNER", componentId.ToString(
            CultureInfo.InvariantCulture));
        plate.SetUserProperty("FK_STIFF_Z", spec.Elevation);
        if (!plate.Modify())
        {
            plate.Delete();
            throw new InvalidOperationException(
                "Tekla не сохранила атрибуты внутреннего ребра " +
                spec.Index.ToString(CultureInfo.InvariantCulture) + ".");
        }
        return plate;
    }

    private static string BuildWeldKey(
        string mark,
        FachwerkColumnStiffenerSpec spec) =>
        ManagedWeldPrefix +
        Uri.EscapeDataString(mark ?? string.Empty) +
        "/rib-" +
        spec.Index.ToString("D3", CultureInfo.InvariantCulture) +
        "/z-" +
        Math.Round(spec.Elevation, 3, MidpointRounding.AwayFromZero)
            .ToString("0.###", CultureInfo.InvariantCulture);
}
