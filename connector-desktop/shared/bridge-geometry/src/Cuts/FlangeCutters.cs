using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using Platform.Bridge.Geometry.Rules;

namespace Platform.Bridge.Geometry.Cuts;

/// <summary>
/// Pure-builders для cutter-полигонов на стыках поясов. Принимают
/// сегменты + BeamFrame и возвращают список <see cref="CutterPolygon"/>
/// с готовыми мировыми координатами вершин. Tekla-сторона инстанцирует
/// ContourPlate'ы и делает BooleanCut.
///
/// Mirrors decomp <c>ApplyBottom/TopThickness/WidthTransitionCuts</c>
/// (TeklaBridge.exe :1335-1860). Биты-в-биты бинарная парность —
/// требование Phase F.
/// </summary>
/// <remarks>
/// <b>Заметка о baseline offsets:</b> для bottom-flange cuts decomp НЕ
/// добавляет <c>num17 (BottomBaselineY)</c> к Z-координате — cutters
/// строятся как если бы baseline=0. Это работает для HeightMode=ToTop
/// (где BottomBaselineY=0) и может быть багом для ToBottom. Здесь
/// сохраняется поведение decomp'а для парности; если когда-то нужно
/// исправить — править вместе с прежним плагином.
/// </remarks>
public static class FlangeCutters
{
    private const double CutterMargin = 200.0;    // запас по ширине cutter поверх MaxW/MaxT
    // CutterExtent — насколько далеко cutter уходит "за плиту" в нормали (Ez).
    // Раньше было 5000 мм — cutter визуально торчал ~5 м после boolean cut.
    // Достаточно ~100 мм чтобы гарантированно пробить плиту (типичные толщины 20-100 мм).
    private const double CutterExtent = 100.0;

    /// <summary>
    /// Бевельные срезы на нижнем поясе на стыках разной толщины.
    /// Для каждого ThicknessCutSpec строится 4-вершинная трапеция в
    /// плоскости (ex, ez) с профилем PL{MaxW+200} в ey направлении.
    /// </summary>
    public static IReadOnlyList<CutterPolygon> BuildBottomThicknessCutters(
        IReadOnlyList<SegLwt> flangeSegs,
        double ratio,
        BeamFrame beam,
        FlangeRef bottomRef,
        int bottomPartCount,
        string namePrefix,
        double flangeTiltRad = 0.0,
        double transverseOffset = 0.0)
    {
        var cuts = FlangeTransitionRules.BuildThicknessCuts(flangeSegs, ratio);
        var maxW = SegmentMath.MaxWidth(flangeSegs);
        var cutterThickness = maxW + CutterMargin;
        var result = new List<CutterPolygon>(cuts.Count);
        // Pivot perp-вращения = baseline (= Ex axis в плоскости BottomBaselineY),
        // как и в PlaceFlangePlates. Для bottom decomp использует 0 (см. note в шапке).
        var place = MakeCutterVertexPlacer(beam, baselineY: 0.0, flangeTiltRad, transverseOffset);

        for (var j = 0; j < cuts.Count; j++)
        {
            var cut = cuts[j];
            if (cut.TargetDetailIndex < 0 || cut.TargetDetailIndex >= bottomPartCount)
                continue;

            // Z-offsets к плите. BOTTOM_LOCKED → положительные (плита над baseline);
            // TOP_LOCKED → отрицательные (плита под baseline).
            var lockedBottom = bottomRef == FlangeRef.BottomLocked;
            var thickZ = lockedBottom ? cut.ThickT : -cut.ThickT;
            var thinZ = lockedBottom ? cut.ThinT : -cut.ThinT;
            // Decrease=true: текущая плита толще, cut.Start на ней. Толстый конец слева.
            var zAtStart = cut.Decrease ? thickZ : thinZ;
            var zAtEnd = cut.Decrease ? thinZ : thickZ;
            // Far-side Z (где cutter полигон уходит "далеко" чтобы гарантированно пробить плиту).
            var zFar = lockedBottom
                ? Math.Max(zAtStart, zAtEnd) + CutterExtent
                : Math.Min(zAtStart, zAtEnd) - CutterExtent;

            var v1 = place(cut.Start, 0, zAtStart);
            var v2 = place(cut.End, 0, zAtEnd);
            var v3 = place(cut.End, 0, zFar);
            var v4 = place(cut.Start, 0, zFar);

            result.Add(new CutterPolygon(
                Vertices: new[] { v1, v2, v3, v4 },
                Thickness: cutterThickness,
                Name: $"{namePrefix}_CUT_BOT_T_{j + 1:D2}",
                TargetPartIndex: cut.TargetDetailIndex));
        }
        return result;
    }

    /// <summary>
    /// Бевельные срезы на верхнем поясе на стыках разной толщины.
    /// Геометрия зеркальна BuildBottomThicknessCutters, но плиты привязаны
    /// к <see cref="SystemFrame.TopBaselineY"/>.
    /// </summary>
    public static IReadOnlyList<CutterPolygon> BuildTopThicknessCutters(
        IReadOnlyList<SegLwt> topSegs,
        double ratio,
        BeamFrame beam,
        SystemFrame system,
        FlangeRef topRef,
        int topPartCount,
        string namePrefix,
        double flangeTiltRad = 0.0,
        double transverseOffset = 0.0)
    {
        var cuts = FlangeTransitionRules.BuildThicknessCuts(topSegs, ratio);
        var maxW = SegmentMath.MaxWidth(topSegs);
        var cutterThickness = maxW + CutterMargin;
        var result = new List<CutterPolygon>(cuts.Count);
        var zTopRef = system.TopBaselineY;
        var place = MakeCutterVertexPlacer(beam, baselineY: zTopRef, flangeTiltRad, transverseOffset);

        for (var j = 0; j < cuts.Count; j++)
        {
            var cut = cuts[j];
            if (cut.TargetDetailIndex < 0 || cut.TargetDetailIndex >= topPartCount)
                continue;

            var lockedBottom = topRef == FlangeRef.BottomLocked;
            // BOTTOM_LOCKED: плита над верхним baseline (zTopRef + T); TOP_LOCKED: под (zTopRef - T).
            var thickZ = lockedBottom ? zTopRef + cut.ThickT : zTopRef - cut.ThickT;
            var thinZ = lockedBottom ? zTopRef + cut.ThinT : zTopRef - cut.ThinT;
            var zAtStart = cut.Decrease ? thickZ : thinZ;
            var zAtEnd = cut.Decrease ? thinZ : thickZ;
            var zFar = lockedBottom
                ? Math.Max(zAtStart, zAtEnd) + CutterExtent
                : Math.Min(zAtStart, zAtEnd) - CutterExtent;

            var v1 = place(cut.Start, 0, zAtStart);
            var v2 = place(cut.End, 0, zAtEnd);
            var v3 = place(cut.End, 0, zFar);
            var v4 = place(cut.Start, 0, zFar);

            result.Add(new CutterPolygon(
                Vertices: new[] { v1, v2, v3, v4 },
                Thickness: cutterThickness,
                Name: $"{namePrefix}_CUT_TOP_T_{j + 1:D2}",
                TargetPartIndex: cut.TargetDetailIndex));
        }
        return result;
    }

    /// <summary>
    /// Трапециевидные срезы на нижнем поясе на стыках разной ширины.
    /// Для каждого стыка генерируется 2 треугольных cutter'а (left + right
    /// по ey). Внутренние стыки одинаковой ширины пропускаются.
    /// </summary>
    public static IReadOnlyList<CutterPolygon> BuildBottomWidthCutters(
        IReadOnlyList<SegLwt> flangeSegs,
        double ratio,
        BeamFrame beam,
        FlangeRef bottomRef,
        int bottomPartCount,
        string namePrefix,
        double flangeTiltRad = 0.0,
        double transverseOffset = 0.0)
        => BuildWidthCutters(flangeSegs, ratio, beam, bottomRef, bottomPartCount, namePrefix,
            isTopFlange: false, zTopRef: 0.0, label: "BOT",
            flangeTiltRad: flangeTiltRad, transverseOffset: transverseOffset);

    /// <summary>
    /// Трапециевидные срезы на верхнем поясе на стыках разной ширины.
    /// </summary>
    public static IReadOnlyList<CutterPolygon> BuildTopWidthCutters(
        IReadOnlyList<SegLwt> topSegs,
        double ratio,
        BeamFrame beam,
        SystemFrame system,
        FlangeRef topRef,
        int topPartCount,
        string namePrefix,
        double flangeTiltRad = 0.0,
        double transverseOffset = 0.0)
        => BuildWidthCutters(topSegs, ratio, beam, topRef, topPartCount, namePrefix,
            isTopFlange: true, zTopRef: system.TopBaselineY, label: "TOP",
            flangeTiltRad: flangeTiltRad, transverseOffset: transverseOffset);

    private static IReadOnlyList<CutterPolygon> BuildWidthCutters(
        IReadOnlyList<SegLwt> segs,
        double ratio,
        BeamFrame beam,
        FlangeRef flangeRef,
        int partCount,
        string namePrefix,
        bool isTopFlange,
        double zTopRef,
        string label,
        double flangeTiltRad = 0.0,
        double transverseOffset = 0.0)
    {
        // BuildFlangeTransitionSpecs использует один ratio для обоих
        // (widthRatio = thicknessRatio = ratio) — это поведение decomp'а
        // (ApplyBottomWidthTransitionCuts передаёт num2 в оба аргумента).
        var specs = FlangeTransitionRules.BuildFlangeTransitionSpecs(segs, ratio, ratio);
        var maxT = SegmentMath.MaxThickness(segs);
        var cutterThickness = maxT + CutterMargin;
        var result = new List<CutterPolygon>(specs.Length * 2);
        // baselineY для pivot rotation: top — TopBaselineY, bottom — 0 (decomp parity).
        var baselineY = isTopFlange ? zTopRef : 0.0;
        var place = MakeCutterVertexPlacer(beam, baselineY, flangeTiltRad, transverseOffset);

        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            if (!spec.WidthChanged) continue;

            // ToNextForWide=true означает, что широкая плита — следующая. Cut на неё.
            // ToNextForWide=false — широкая плита текущая. Cut на текущую.
            var targetIdx = spec.ToNextForWide ? spec.BoundaryIndex + 1 : spec.BoundaryIndex;
            if (targetIdx < 0 || targetIdx >= partCount) continue;

            var current = segs[spec.BoundaryIndex];
            var next = segs[Math.Min(spec.BoundaryIndex + 1, segs.Count - 1)];
            var maxHalfW = Math.Max(current.W, next.W) / 2.0;
            var minHalfW = Math.Min(current.W, next.W) / 2.0;
            // Z-baseline cutter'а у нижней грани плиты.
            var zBase = isTopFlange
                ? (flangeRef == FlangeRef.BottomLocked ? zTopRef + current.T : zTopRef)
                : (flangeRef == FlangeRef.BottomLocked ? 0.0 : -current.T);

            for (var side = -1; side <= 1; side += 2)
            {
                var ySideMax = maxHalfW * side;
                var ySideMin = minHalfW * side;
                var widthDiff = Math.Abs(current.W - next.W);
                var thicknessDiff = Math.Abs(current.T - next.T);
                var widthTaperLen = widthDiff / 2.0 * ratio;
                var thicknessTaperLen = thicknessDiff * ratio;
                var transitionLen = Math.Max(widthTaperLen, thicknessTaperLen);
                // X-координата точки где cutter "схватывает" плиту.
                var xAtJoint = spec.Theoretical + (spec.ToNextForWide ? -transitionLen : transitionLen);
                var xDirection = spec.ToNextForWide ? 1.0 : -1.0;
                var xAtTip = xAtJoint + xDirection * widthTaperLen;

                var v1 = place(xAtJoint, ySideMax, zBase);
                var v2 = place(xAtJoint, ySideMin, zBase);
                var v3 = place(xAtTip, ySideMax, zBase);
                // Note: v3 = v1 + ex*xDirection*widthTaperLen (mirror decomp val4 = val2 + ex*..*num8)

                var sideLabel = side < 0 ? "L" : "R";
                result.Add(new CutterPolygon(
                    Vertices: new[] { v1, v2, v3 },
                    Thickness: cutterThickness,
                    Name: $"{namePrefix}_CUT_{label}_W_{i + 1:D2}_{sideLabel}",
                    TargetPartIndex: targetIdx));
            }
        }
        return result;
    }

    /// <summary>
    /// Возвращает функцию, которая мапит локальные (x, yLocal, zLocal) в мировой
    /// Vec3 с учётом flangeTiltRad (поворот вокруг Ex в pivot baselineY) и
    /// transverseOffset (сдвиг по Ey).
    ///
    /// Convention identical to <see cref="BeamPlacement.PlaceFlangePlates"/>:
    ///   widthAxis_rot  = cos·Ey - sin·Ez
    ///   normalAxis_rot = sin·Ey + cos·Ez
    /// (= R_Ex(-flangeTilt) применённый к Ey/Ez; положительный flangeTilt поворачивает Ey в сторону -Ez).
    ///
    /// Локальная точка (yLocal, zLocal) при pivot=(0, baselineY) и transverseOffset δ_y:
    ///   yWorld = cos·yLocal + sin·(zLocal - baselineY) + δ_y
    ///   zWorld = baselineY  - sin·yLocal + cos·(zLocal - baselineY)
    /// При flangeTilt=0, δ_y=0 это сводится к (yLocal, zLocal) → backward compat
    /// со старым кодом cutter'ов (Origin + Ex·x + Ey·yLocal + Ez·zLocal).
    /// </summary>
    private static Func<double, double, double, Vec3> MakeCutterVertexPlacer(
        BeamFrame beam, double baselineY, double flangeTiltRad, double transverseOffset)
    {
        var cosT = Math.Cos(flangeTiltRad);
        var sinT = Math.Sin(flangeTiltRad);
        return (x, yLocal, zLocal) =>
        {
            var zFromBaseline = zLocal - baselineY;
            var yWorld = cosT * yLocal + sinT * zFromBaseline + transverseOffset;
            var zWorld = baselineY - sinT * yLocal + cosT * zFromBaseline;
            return beam.Origin + beam.Ex * x + beam.Ey * yWorld + beam.Ez * zWorld;
        };
    }
}
