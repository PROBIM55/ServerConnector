using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;

namespace Platform.Bridge.Geometry.Placement;

/// <summary>
/// Pure-функции размещения геометрии: получают DetailBoundary[] из
/// FlangeTransitionRules плюс BeamFrame/SystemFrame и возвращают
/// PlateSpec[] с готовыми мировыми координатами Start/End.
/// Phase E передаёт результат в TeklaShim для создания ContourPlate'ов.
/// </summary>
public static class BeamPlacement
{
    /// <summary>
    /// Размещение плит пояса (нижнего или верхнего).
    /// </summary>
    /// <param name="details">Список DetailBoundary из <see cref="Platform.Bridge.Geometry.Rules.FlangeTransitionRules.GroupDetails"/>.</param>
    /// <param name="beam">Локальная система балки.</param>
    /// <param name="system">Высотная схема балки.</param>
    /// <param name="flangeRef">Привязка центра плиты (TOP_LOCKED/BOTTOM_LOCKED).</param>
    /// <param name="isTopFlange">true — верхний пояс, false — нижний.</param>
    /// <param name="material">Tekla material string.</param>
    /// <param name="namePrefix">Префикс имени плиты. Финальное имя = "{prefix}_FLG_NN".</param>
    /// <param name="classId">Class string для Part.Class.</param>
    /// <param name="flangeTiltRad">
    /// Угол поворота плиты вокруг оси балки (Ex) для ориентации «перпендикулярно
    /// стенке» (042 §3 / П-3). 0 = HORIZONTAL (стандарт), wallTiltRad —
    /// PERPENDICULAR_TO_WALL для I-girder. Pivot на оси балки в плоскости
    /// baseline-Y (Ez direction) — поэтому baseline остаётся фиксированной.
    /// Применяется к ExtrudeAxis/NormalAxis и к центру плиты:
    ///   widthAxis_new  =  cos·Ey - sin·Ez
    ///   normalAxis_new =  sin·Ey + cos·Ez
    /// Сторона вращения подобрана так, что для I-girder с положительным
    /// wallTiltDeg верх стенки уходит в +Ey (transverse positive) — то же
    /// направление, что и web-side в graph.ts (с поправкой на rename осей).
    /// </param>
    public static IReadOnlyList<PlateSpec> PlaceFlangePlates(
        IReadOnlyList<DetailBoundary> details,
        BeamFrame beam,
        SystemFrame system,
        FlangeRef flangeRef,
        bool isTopFlange,
        string material,
        string namePrefix,
        string classId = "3",
        double flangeTiltRad = 0.0,
        double transverseOffset = 0.0)
    {
        var result = new List<PlateSpec>(details.Count);
        var cosT = Math.Cos(flangeTiltRad);
        var sinT = Math.Sin(flangeTiltRad);
        var baselineY = isTopFlange ? system.TopBaselineY : system.BottomBaselineY;
        var widthAxisRot = beam.Ey * cosT - beam.Ez * sinT;
        var normalAxisRot = beam.Ey * sinT + beam.Ez * cosT;
        for (var i = 0; i < details.Count; i++)
        {
            var d = details[i];
            if (d.End - d.Start < 1.0)
                continue; // skip degenerate plates; mirror Step4 filter
            var centerY = system.PlateCenterY(d.Thickness, flangeRef, isTopFlange);
            // Rotation of plate center around (Ex axis at baselineY in Ez):
            //   local offset = (0 in Ey, centerY - baselineY in Ez)
            //   after R_Ex(-flangeTilt): (centerYLocal·sin in Ey, centerYLocal·cos in Ez)
            //   + pivot (0, baselineY) → (centerYLocal·sin, baselineY + centerYLocal·cos)
            // transverseOffset смещает плиту в Ey direction — нужно для I_TWIN
            // где каждый из двух нижних поясов центрирован на ±twinSpacing/2.
            var centerYLocal = centerY - baselineY;
            var centerZWorld = baselineY + cosT * centerYLocal;
            var centerWWorld = sinT * centerYLocal + transverseOffset;
            var start = beam.LocalPoint(d.Start, centerZWorld, centerWWorld);
            var end = beam.LocalPoint(d.End, centerZWorld, centerWWorld);
            var idx = result.Count + 1;
            var label = isTopFlange ? "TOP" : "FLG";
            var name = $"{namePrefix}_{label}_{idx:D2}";
            result.Add(new PlateSpec(
                Start: start,
                End: end,
                ExtrudeAxis: widthAxisRot,
                NormalAxis: normalAxisRot,
                Width: d.Width,
                Thickness: d.Thickness,
                Material: material,
                Name: name,
                ClassId: classId));
        }
        return result;
    }
}
