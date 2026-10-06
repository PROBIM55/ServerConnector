using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using Platform.Bridge.Geometry.Rules;

namespace Platform.Bridge.Geometry.Cuts;

/// <summary>
/// Pure-builders для cutter-полигонов которые обрезают web-плиту до внутренней
/// поверхности поясов (и палубы для DECK/DECK_SLOPES). Каждый web-плит-сегмент
/// получает 2 cutter'а: bottom (отрезает всё ниже верхней грани нижнего пояса)
/// и top (отрезает всё выше нижней грани верхнего пояса / низа палубы).
/// Mirrors decomp <c>ApplyWebCutsForSegment</c>/<c>ApplySingleWebBoundaryCut</c>
/// (TeklaBridge.exe :1426-1532).
/// </summary>
/// <remarks>
/// Cutter — flat-полигон в плоскости (ex, ez), профиль <c>PL{maxW+2000}</c>
/// расширяет его в ey направлении (через всю ширину поясов плюс запас).
/// Контурные точки: верхний край вдоль zAt(break) для каждого break-point,
/// затем нижний край в reverse-order на zFar = min/max(zAt) ± 5000 мм
/// (гарантия пробивания всего web).
/// </remarks>
public static class WebCutters
{
    private const double CutWidthMargin = 2000.0;
    private const double FarSideExtent = 5000.0;

    /// <summary>
    /// Построить cutter'ы для всех web-плит-сегментов. На каждый segment
    /// добавляется 2 polygon'а (bottom + top), <see cref="CutterPolygon.TargetPartIndex"/>
    /// указывает на индекс web-плиты в списке.
    /// </summary>
    /// <param name="skipTopCut">Когда true, top-cut не добавляется. Нужно для
    /// box+DeckSlopes: стенка плагином уже размещена на slope-aware zTop,
    /// дополнительный cut к константному zTop центра обрежет её обратно к
    /// центру (= неправильно для наклонной деки).</param>
    public static IReadOnlyList<CutterPolygon> BuildWebCutters(
        IReadOnlyList<SegLwt> bottomSegs,
        IReadOnlyList<SegLwt> topSegs,
        double bottomRatio,
        double topRatio,
        FlangeRef bottomRef,
        FlangeRef topRef,
        TopMode topMode,
        double deckThickness,
        SystemFrame system,
        BeamFrame beam,
        IReadOnlyList<double> webBreakpoints,
        string namePrefix,
        bool skipTopCut = false)
    {
        var result = new List<CutterPolygon>();
        if (webBreakpoints.Count < 2) return result;

        var topMaxW = topMode == TopMode.TopFlange ? SegmentMath.MaxWidth(topSegs) : 0.0;
        var cutterThickness = Math.Max(SegmentMath.MaxWidth(bottomSegs), topMaxW) + CutWidthMargin;

        var bottomCuts = FlangeTransitionRules.BuildThicknessCuts(bottomSegs, bottomRatio);
        var topCuts = topMode == TopMode.TopFlange
            ? FlangeTransitionRules.BuildThicknessCuts(topSegs, topRatio)
            : new List<ThicknessCutSpec>();

        var webIdx = 0;
        for (var i = 0; i < webBreakpoints.Count - 1; i++)
        {
            var s0 = webBreakpoints[i];
            var s1 = webBreakpoints[i + 1];
            if (s1 - s0 < 1.0) continue;

            // Bottom cut: отрезает всё ниже верхней грани нижнего пояса (zAtBottom(s)).
            var bottomBreaks = SegmentMath.CollectBreaks(s0, s1, bottomCuts);
            result.Add(BuildBoundaryCutPolygon(
                bottomBreaks,
                s => BottomFlangeTopZ(bottomSegs, bottomCuts, bottomRef, s, system.BottomBaselineY),
                beam, cutterThickness,
                isTopBoundary: false,
                name: $"{namePrefix}_WEB{webIdx + 1:D2}_CUT_B",
                targetIndex: webIdx));

            if (!skipTopCut)
            {
                // Top cut: отрезает всё выше нижней грани верхнего пояса (zAtTop(s)).
                var topBreaks = topMode == TopMode.TopFlange
                    ? SegmentMath.CollectBreaks(s0, s1, topCuts)
                    : new List<double> { s0, s1 };
                result.Add(BuildBoundaryCutPolygon(
                    topBreaks,
                    s => TopZoneBottomZ(topSegs, topCuts, topMode, topRef, deckThickness, system, s),
                    beam, cutterThickness,
                    isTopBoundary: true,
                    name: $"{namePrefix}_WEB{webIdx + 1:D2}_CUT_T",
                    targetIndex: webIdx));
            }

            webIdx++;
        }
        return result;
    }

    /// <summary>
    /// Верхняя грань нижнего пояса в точке X=s, в АБСОЛЮТНЫХ системных
    /// координатах (с учётом <see cref="SystemFrame.BottomBaselineY"/>).
    /// Для BOTTOM_LOCKED: baseline + ThicknessAt (с chamfer-ramp).
    /// Для TOP_LOCKED: baseline (top пояса = baseline, низ под baseline).
    /// Используется при построении web ContourPlate'ов: outer-bottom edge
    /// стенки должен лежать на этой поверхности.
    /// </summary>
    /// <param name="bottomBaselineY">
    /// = <see cref="SystemFrame.BottomBaselineY"/>. Для TO_TOP heightMode = 0,
    /// для TO_BOTTOM = bottomMaxT. Раньше функция игнорировала baseline,
    /// возвращая значения относительно 0 — в TO_BOTTOM это давало wall bottom
    /// edge на bottomMaxT ниже фактического top face пояса (wall проходил сквозь пояс).
    /// </param>
    /// <param name="flangeTiltRad">
    /// Угол поворота пояса вокруг Ex (для PERPENDICULAR_TO_WALL ориентации
    /// I-girder). При flangeTiltRad ≠ 0 плита НЕ горизонтальна, её wall-facing
    /// плоскость пересекает wall midplane (Y(z) = z·tan(wallTilt)) на МЕНЬШЕЙ
    /// глубине: z = baseline + T·cos(flangeTilt) вместо z = baseline + T.
    /// При flangeTiltRad = wallTiltRad (типичный случай perp) это даёт точное
    /// касание стенки с плоскостью повёрнутого пояса. При flangeTiltRad = 0 —
    /// прежнее поведение (HORIZONTAL flange).
    /// </param>
    public static double BottomFlangeTopZ(
        IReadOnlyList<SegLwt> bottomSegs,
        IReadOnlyList<ThicknessCutSpec> bottomCuts,
        FlangeRef bottomRef,
        double s,
        double bottomBaselineY = 0.0,
        double flangeTiltRad = 0.0)
    {
        if (bottomRef == FlangeRef.TopLocked) return bottomBaselineY;
        var t = SegmentMath.ThicknessAt(bottomSegs, bottomCuts, s);
        var depth = Math.Abs(flangeTiltRad) > 1e-9 ? t * Math.Cos(flangeTiltRad) : t;
        return bottomBaselineY + depth;
    }

    /// <summary>
    /// Нижняя грань верхней зоны (топ-фланца или палубы) в точке X=s.
    /// Используется при построении web ContourPlate'ов: inner-top edge
    /// стенки должен лежать на этой поверхности.
    /// </summary>
    /// <param name="flangeTiltRad">
    /// Угол поворота верхнего пояса (PERPENDICULAR_TO_WALL). Аналогично
    /// <see cref="BottomFlangeTopZ"/>: при tilt ≠ 0 wall-facing плоскость
    /// пояса повёрнута, и стенка midplane пересекает её на меньшей глубине
    /// (T·cos вместо T от baseline).
    /// </param>
    public static double TopZoneBottomZ(
        IReadOnlyList<SegLwt> topSegs,
        IReadOnlyList<ThicknessCutSpec> topCuts,
        TopMode topMode,
        FlangeRef topRef,
        double deckThickness,
        SystemFrame system,
        double s,
        double flangeTiltRad = 0.0)
    {
        if (topMode != TopMode.TopFlange)
            return system.TopBaselineY - deckThickness;
        if (topRef == FlangeRef.BottomLocked)
            return system.TopBaselineY;
        var t = SegmentMath.ThicknessAt(topSegs, topCuts, s);
        var depth = Math.Abs(flangeTiltRad) > 1e-9 ? t * Math.Cos(flangeTiltRad) : t;
        return system.TopBaselineY - depth;
    }

    private static CutterPolygon BuildBoundaryCutPolygon(
        IReadOnlyList<double> sBreaks,
        Func<double, double> zAt,
        BeamFrame beam,
        double cutterThickness,
        bool isTopBoundary,
        string name,
        int targetIndex)
    {
        // Far-side Z: для top-boundary cutter уходит ВВЕРХ от плиты (max(z)+5000),
        // для bottom — ВНИЗ (min(z)-5000). Это гарантирует, что после BooleanCut
        // web будет ровно от верха нижнего пояса до низа верхнего.
        var minZ = double.MaxValue;
        var maxZ = double.MinValue;
        foreach (var s in sBreaks)
        {
            var z = zAt(s);
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        var zFar = isTopBoundary ? maxZ + FarSideExtent : minZ - FarSideExtent;

        var vertices = new Vec3[sBreaks.Count * 2];
        // Передняя кромка: каждый break-point, вершина на (s, 0, zAt(s)).
        for (var j = 0; j < sBreaks.Count; j++)
        {
            var s = sBreaks[j];
            vertices[j] = beam.Origin + beam.Ex * s + beam.Ez * zAt(s);
        }
        // Дальняя кромка: те же break-points в reverse, вершина на (s, 0, zFar).
        for (var j = 0; j < sBreaks.Count; j++)
        {
            var s = sBreaks[sBreaks.Count - 1 - j];
            vertices[sBreaks.Count + j] = beam.Origin + beam.Ex * s + beam.Ez * zFar;
        }

        return new CutterPolygon(
            Vertices: vertices,
            Thickness: cutterThickness,
            Name: name,
            TargetPartIndex: targetIndex);
    }
}
