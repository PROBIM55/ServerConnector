using System.Collections.Generic;
using System.Globalization;
using Platform.Bridge.Geometry.Cuts;
using Tekla.Structures.Model;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Из pure <see cref="CutterPolygon"/> строит ContourPlate с профилем
/// PL{Thickness}, Class="BlOpCl" (Boolean Open Cutter), Insert + BooleanCut
/// по target-плите. Используется в Phase E4 для transition-cuts.
/// </summary>
internal static class CutterPartFactory
{
    /// <summary>
    /// Применить серию cutter-полигонов к плитам пояса. Возвращает количество
    /// успешно применённых cut'ов (тех, у которых Insert + BooleanCut вернули
    /// true). Mirrors decomp поведение: ошибки конкретного cutter не
    /// прерывают серию, неудачные тихо пропускаются.
    /// </summary>
    public static int ApplyCuts(IReadOnlyList<CutterPolygon> polygons, IReadOnlyList<Part> targets, string material)
    {
        var applied = 0;
        for (var i = 0; i < polygons.Count; i++)
        {
            var poly = polygons[i];
            if (poly.TargetPartIndex < 0 || poly.TargetPartIndex >= targets.Count) continue;
            var father = targets[poly.TargetPartIndex];

            var plate = new ContourPlate
            {
                Profile = { ProfileString = "PL" + poly.Thickness.ToString(CultureInfo.InvariantCulture) },
                Material = { MaterialString = material },
                Name = poly.Name,
                Class = "BlOpCl",
            };
            foreach (var v in poly.Vertices)
                plate.AddContourPoint(new ContourPoint(v.ToPoint(), null));

            if (plate.Insert() && BooleanOperations.Cut(father, plate))
                applied++;
        }
        return applied;
    }
}
