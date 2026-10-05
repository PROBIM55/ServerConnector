using System.Collections.Generic;
using System.Globalization;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using Tekla.Structures.Model;
using TS = Tekla.Structures.Geometry3d;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Создание <see cref="ContourPlate"/> по нашей pure <see cref="PlateSpec"/>.
/// Mirrors decomp <c>CreatePlatePart</c> (TeklaBridge.exe :1867-1907):
/// 4 контурные точки, профиль <c>"PL{thickness}"</c>, Position.Plane=Middle /
/// Rotation=Top / Depth=Middle.
/// </summary>
internal static class PlatePartFactory
{
    /// <summary>
    /// Создаёт ContourPlate из спецификации и сразу делает Insert.
    /// Возвращает null если Insert не удался. После Insert НЕ делает
    /// CommitChanges — оркестратор сам решает когда коммитить (для
    /// корректной seq: bottom + web + top + cuts → один Commit).
    /// </summary>
    public static ContourPlate? Create(PlateSpec spec)
        => Create(spec.Start, spec.End, spec.ExtrudeAxis, spec.NormalAxis,
            spec.Width, centerOffset: 0.0, spec.Thickness,
            spec.Material, spec.Name, spec.ClassId);

    /// <summary>
    /// Low-level вариант. p1/p2 — точки на центральной линии плиты,
    /// widthAxis — поперечная ось (= BeamFrame.Ey), offsetAxis —
    /// перпендикуляр к плите (= BeamFrame.Ez). centerOffset позволяет
    /// сместить центр плиты вдоль offsetAxis (используется для палубных
    /// плит чтобы загнать их под верхний край).
    /// </summary>
    public static ContourPlate? Create(
        Vec3 p1, Vec3 p2,
        Vec3 widthAxis, Vec3 offsetAxis,
        double width, double centerOffset, double thickness,
        string material, string name, string partClass)
    {
        var halfW = width / 2.0;
        var basePt1 = p1 + offsetAxis * centerOffset;
        var basePt2 = p2 + offsetAxis * centerOffset;
        var v1 = basePt1 + widthAxis * (-halfW);
        var v2 = basePt2 + widthAxis * (-halfW);
        var v3 = basePt2 + widthAxis * halfW;
        var v4 = basePt1 + widthAxis * halfW;

        var plate = new ContourPlate
        {
            Profile = { ProfileString = "PL" + thickness.ToString(CultureInfo.InvariantCulture) },
            Material = { MaterialString = material },
            Name = name,
            Class = partClass,
        };
        plate.Position.Plane = Position.PlaneEnum.MIDDLE;
        plate.Position.Rotation = Position.RotationEnum.TOP;
        plate.Position.Depth = Position.DepthEnum.MIDDLE;

        plate.AddContourPoint(new ContourPoint(v1.ToPoint(), null));
        plate.AddContourPoint(new ContourPoint(v2.ToPoint(), null));
        plate.AddContourPoint(new ContourPoint(v3.ToPoint(), null));
        plate.AddContourPoint(new ContourPoint(v4.ToPoint(), null));

        return plate.Insert() ? plate : null;
    }

    /// <summary>
    /// Создаёт ContourPlate с произвольным набором контурных вершин (3+).
    /// Все точки должны лежать в одной плоскости (centerline-плоскости плиты);
    /// толщина <paramref name="thickness"/> расходится в обе стороны от
    /// этой плоскости (Position.Depth=MIDDLE).
    ///
    /// Используется для построения стенок, контур которых повторяет
    /// inner-face поясов: bottom edge на BottomFlangeTopZ(s)+sin·t/2,
    /// top edge на TopZoneBottomZ(s)-sin·t/2 — без BooleanCut'ов.
    /// </summary>
    public static ContourPlate? CreateContour(
        IReadOnlyList<Vec3> contour,
        double thickness,
        string material, string name, string partClass)
    {
        if (contour == null || contour.Count < 3) return null;
        var plate = new ContourPlate
        {
            Profile = { ProfileString = "PL" + thickness.ToString(CultureInfo.InvariantCulture) },
            Material = { MaterialString = material },
            Name = name,
            Class = partClass,
        };
        plate.Position.Plane = Position.PlaneEnum.MIDDLE;
        plate.Position.Rotation = Position.RotationEnum.TOP;
        plate.Position.Depth = Position.DepthEnum.MIDDLE;
        for (var i = 0; i < contour.Count; i++)
        {
            plate.AddContourPoint(new ContourPoint(contour[i].ToPoint(), null));
        }
        return plate.Insert() ? plate : null;
    }
}
