using System.Collections.Generic;
using System.Globalization;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using Tekla.Structures.Model;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Создание палубных ContourPlate'ов для DECK_SLOPES topMode. Каждый
/// strip — инклинированный 4-вершинный контур вдоль оси балки.
/// Mirrors decomp CreateDeckStrip :1945-1983: контур опущен на
/// -deckT/2 вдоль ez чтобы PL{deckT} с Position.Plane=MIDDLE/Depth=MIDDLE
/// разместил толщину сверху от контура.
/// </summary>
internal static class DeckStripFactory
{
    /// <summary>
    /// Применить серию палубных полос. Каждая полоса даёт один
    /// ContourPlate. Возвращает список созданных Part'ов (для последующего
    /// учёта в counters) или null если хоть один Insert провалился.
    /// </summary>
    public static List<Part>? CreateStrips(
        IReadOnlyList<DeckStripSpec> strips,
        BeamFrame beam,
        string material)
    {
        var result = new List<Part>(strips.Count);
        foreach (var strip in strips)
        {
            // Четыре угла контура: (Y_start, Z_start) и (Y_end, Z_end) на
            // обеих "торцах" балки (s=0 и s=axisLength).
            var aStart = beam.Origin
                + beam.Ey * strip.YStart + beam.Ez * strip.ZStart;
            var aEnd = beam.Origin + beam.Ex * beam.AxisLength
                + beam.Ey * strip.YStart + beam.Ez * strip.ZStart;
            var bEnd = beam.Origin + beam.Ex * beam.AxisLength
                + beam.Ey * strip.YEnd + beam.Ez * strip.ZEnd;
            var bStart = beam.Origin
                + beam.Ey * strip.YEnd + beam.Ez * strip.ZEnd;

            // Сдвиг контура вниз на половину толщины — после Position.Depth=MIDDLE
            // плита будет от Z до Z+T (поверх slope-линии).
            var zShift = beam.Ez * (-strip.Thickness / 2.0);

            var plate = new ContourPlate
            {
                Profile = { ProfileString = "PL" + strip.Thickness.ToString(CultureInfo.InvariantCulture) },
                Material = { MaterialString = material },
                Name = strip.Name,
                Class = strip.ClassId,
            };
            plate.Position.Plane = Position.PlaneEnum.MIDDLE;
            plate.Position.Rotation = Position.RotationEnum.TOP;
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            plate.AddContourPoint(new ContourPoint((aStart + zShift).ToPoint(), null));
            plate.AddContourPoint(new ContourPoint((aEnd + zShift).ToPoint(), null));
            plate.AddContourPoint(new ContourPoint((bEnd + zShift).ToPoint(), null));
            plate.AddContourPoint(new ContourPoint((bStart + zShift).ToPoint(), null));

            if (!plate.Insert()) return null;
            result.Add(plate);
        }
        return result;
    }
}
