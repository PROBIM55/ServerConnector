using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Cuts;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Placement;
using Platform.Bridge.Geometry.Rules;
using Tekla.Structures.Model;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Создание стиффенеров: deck-ribs (продольные плиты под палубой для
/// DECK/DECK_SLOPES), web-long-ribs (продольные плиты по стенке),
/// web-trans-ribs (поперечные плиты по стенке с split'ом по long-ribs).
/// Mirrors decomp TeklaBridge.exe :2189-2312.
/// Class кодировка плит сохранена идентично декомпу: "6" deck-rib, "7" web-rib.
/// </summary>
internal static class RibFactory
{
    /// <summary>
    /// Одна полоса продольного стиффенера под палубой. Y=поперечный offset,
    /// zDeckBottom=z низа палубы в точке Y (учитывает DECK_SLOPES уклоны).
    /// Rib висит снизу палубы на полную высоту ribH. Mirrors :2189.
    /// </summary>
    public static bool CreateDeckRib(
        BeamFrame beam, double xStart, double xEnd, double y, double zDeckBottom,
        double ribH, double ribT, string material, string name)
    {
        if (xEnd - xStart <= 1.0 || ribH <= 0.0 || ribT <= 0.0) return false;
        var centerZ = zDeckBottom - ribH / 2.0;
        var p1 = beam.Origin + beam.Ex * xStart + beam.Ey * y + beam.Ez * centerZ;
        var p2 = beam.Origin + beam.Ex * xEnd   + beam.Ey * y + beam.Ez * centerZ;
        var part = PlatePartFactory.Create(p1, p2, beam.Ez, beam.Ey, ribH, 0.0, ribT, material, name, "6");
        return part != null;
    }

    /// <summary>
    /// Раскладывает deck-ribs по обеим сторонам (left = отрицательный Y,
    /// right = положительный Y). Шаги stepsLeft/Right — расстояния между
    /// последовательными ребрами от оси. Mirrors decomp Step5 :911-934.
    /// </summary>
    public static int CreateDeckRibs(
        BeamFrame beam,
        double axisLen,
        IReadOnlyList<double> stepsLeft, IReadOnlyList<double> stepsRight, bool bothSides,
        double ribH, double ribT,
        double startLeft, double lengthLeft, double startRight, double lengthRight,
        double zTopSystem, double deckT,
        TopMode topMode,
        IReadOnlyList<SlopeSeg> deckLeftSlopes, IReadOnlyList<SlopeSeg> deckRightSlopes,
        string material, string baseName)
    {
        if (ribH <= 0.0 || ribT <= 0.0) return 0;
        var runL = RibGeometry.NormalizeRun(axisLen, startLeft, lengthLeft);
        var runR = bothSides ? runL : RibGeometry.NormalizeRun(axisLen, startRight, lengthRight);
        var stepsR = bothSides ? stepsLeft : stepsRight;

        // Для DECK_SLOPES низ палубы = zTopSystem - deckT + DeckTopOffsetAtY(y) (доплюсуется верх).
        // Для DECK plain: низ палубы = zTopSystem - deckT для любого Y.
        double DeckBottomAtY(double y) =>
            topMode == TopMode.DeckSlopes
                ? zTopSystem - deckT + RibGeometry.DeckTopOffsetAtY(y, deckLeftSlopes, deckRightSlopes)
                : zTopSystem - deckT;

        var created = 0;

        // Left side: y идёт в минус (нарастающие отрицательные).
        var yCursor = 0.0;
        for (var i = 0; i < stepsLeft.Count; i++)
        {
            yCursor -= stepsLeft[i];
            if (CreateDeckRib(beam, runL.Start, runL.End, yCursor, DeckBottomAtY(yCursor),
                    ribH, ribT, material, $"{baseName}_RIB_L{i + 1:D2}"))
                created++;
        }

        // Right side: y нарастает в плюс.
        yCursor = 0.0;
        for (var i = 0; i < stepsR.Count; i++)
        {
            yCursor += stepsR[i];
            if (CreateDeckRib(beam, runR.Start, runR.End, yCursor, DeckBottomAtY(yCursor),
                    ribH, ribT, material, $"{baseName}_RIB_R{i + 1:D2}"))
                created++;
        }

        return created;
    }

    /// <summary>
    /// Одна полоса продольного стиффенера стенки. Не создаёт ребро если его
    /// центр выходит за inner-faces поясов (margin 1mm). Mirrors :2221.
    /// </summary>
    /// <param name="wallYOffset">Y-смещение оси стенки от центра балки.
    /// 0 для I-girder (стенка по центру), wallY центр (между YTop и YBottom)
    /// для box girder.</param>
    /// <param name="wallTiltRad">Наклон стенки (rad), 0 для I-girder.</param>
    /// <param name="wallOutwardSign">Внешняя нормаль по Ey: 0 для I-girder
    /// (используется legacy sideSign), +1 для правой box-стенки, -1 для левой.</param>
    /// <param name="webCenterZ">Z центра стенки (среднее zTop/zBottom) —
    /// нужно только для tilt-correction. Можно 0 для I-girder.</param>
    /// <param name="orientation">Ориентация ребра (актуально для box):
    /// Perpendicular = торчит перпендикулярно стенке (rotated), Horizontal =
    /// параллельно земле. Для I-girder любая = horizontal по умолчанию.</param>
    public static bool CreateWebLongRib(
        BeamFrame beam, double axisLen,
        IReadOnlyList<SegLwt> webSegs,
        IReadOnlyList<SegLwt> bottomSegs, IReadOnlyList<SegLwt> topSegs,
        TopMode topMode, double deckT, double zTopRef, double zBaseLower,
        FlangeRef bottomRef, FlangeRef topRef,
        string side, double offsetFromBottom, double start, double length,
        double ribHeight, double ribThickness, string material, string name,
        double wallYOffset = 0.0,
        double wallTiltRad = 0.0,
        int wallOutwardSign = 0,
        double webCenterZ = 0.0,
        RibOrientation orientation = RibOrientation.Perpendicular,
        int surfaceSign = 1,
        HeightMode heightMode = HeightMode.ToTop,
        IReadOnlyList<RibSegment>? segments = null)
    {
        if (offsetFromBottom <= 0.0 || ribHeight <= 0.0 || ribThickness <= 0.0) return false;
        var run = RibGeometry.NormalizeRun(axisLen, start, length);
        if (run.Span <= 1.0) return false;

        // Фаза 2: участки ребра по длине (своё H/T) + авто-скос на переходах
        // высоты (наклонная грань |Δh|·ratio). Каждый кусок профиля ЕЩЁ бьётся
        // по изменению толщины стенки (Фаза 1) — чтобы сохранить высоту ребра на
        // уступе стенки. Плоский кусок → прямоугольная плита, наклонный → трапеция.
        var sections = RibGeometry.ResolveRibSections(segments, run, ribHeight, ribThickness);
        var profile = RibGeometry.BuildRibProfile(sections, RibGeometry.RibTaperRatio);

        var any = false;
        foreach (var piece in profile)
        {
            if (piece.Thickness <= 0.0) continue;
            var subRuns = RibGeometry.SplitRunByWebThickness(
                new RibGeometry.RunRange(piece.Start, piece.End), webSegs);
            foreach (var sub in subRuns)
            {
                var hA = RibGeometry.RibHeightAt(piece, sub.Start);
                var hB = RibGeometry.RibHeightAt(piece, sub.End);
                if (Math.Max(hA, hB) <= 0.0) continue;
                if (CreateWebLongRibPiece(beam, sub, webSegs, bottomSegs, topSegs, topMode, deckT, zTopRef,
                        zBaseLower, bottomRef, topRef, side, offsetFromBottom, hA, hB, piece.Thickness,
                        material, name, wallYOffset, wallTiltRad, wallOutwardSign, webCenterZ, orientation,
                        surfaceSign, heightMode))
                    any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// Строит ОДНУ плиту продольного ребра на под-диапазоне run (постоянная
    /// толщина стенки). Высота меняется heightStart→heightEnd: равны → плоская
    /// прямоугольная плита; разные → наклонная трапециевидная ContourPlate (скос).
    /// </summary>
    private static bool CreateWebLongRibPiece(
        BeamFrame beam, RibGeometry.RunRange run,
        IReadOnlyList<SegLwt> webSegs,
        IReadOnlyList<SegLwt> bottomSegs, IReadOnlyList<SegLwt> topSegs,
        TopMode topMode, double deckT, double zTopRef, double zBaseLower,
        FlangeRef bottomRef, FlangeRef topRef,
        string side, double offsetFromBottom,
        double heightStart, double heightEnd, double ribThickness, string material, string name,
        double wallYOffset, double wallTiltRad, int wallOutwardSign, double webCenterZ,
        RibOrientation orientation, int surfaceSign, HeightMode heightMode)
    {
        if (run.Span <= 1.0) return false;

        var midX = (run.Start + run.End) / 2.0;
        var bottomTopZ = RibGeometry.BottomTopAtX(bottomSegs, midX, zBaseLower, bottomRef);
        var topBottomZ = RibGeometry.TopBottomAtX(topMode, topSegs, deckT, zTopRef, topRef, midX);
        // offsetFromBottom — SLANT-координата ВДОЛЬ ОСИ СТЕНКИ от ОПОРНОЙ
        // плоскости НИЖНЕГО ПОЯСА (per-user 2026-05-14, third pass):
        //   heightMode=TO_TOP: опора — ВЕРХ нижнего пояса (bottomTopZ).
        //     ribCenterZ = bottomTopZ + offset·cos(tilt).
        //   heightMode=TO_BOTTOM: опора — НИЗ нижнего пояса (bottomBottomZ).
        //     ribCenterZ = bottomBottomZ + offset·cos(tilt).
        // В обоих случаях offset идёт ВВЕРХ от плоскости НП (нижнего пояса).
        // Раньше TO_BOTTOM использовал topBottomZ (верх верхней зоны) ↓ — это
        // было неверно, теперь обе модели от плоскости НП, разница только
        // в том какая плоскость: верх или низ нижнего пояса.
        double bottomBottomZ;
        switch (bottomRef)
        {
            case FlangeRef.BottomLocked:
                bottomBottomZ = zBaseLower;
                break;
            default:  // TopLocked
                bottomBottomZ = zBaseLower - SegmentMath.ParamsAt(bottomSegs, midX).T;
                break;
        }
        var refZ = heightMode == HeightMode.ToBottom ? bottomBottomZ : bottomTopZ;
        var cosForOffset = Math.Cos(wallTiltRad);
        var cosAbs = Math.Abs(cosForOffset) > 1e-6 ? Math.Abs(cosForOffset) : 1.0;
        var ribCenterZ = refZ + offsetFromBottom * cosAbs;
        if (ribCenterZ <= bottomTopZ + 1.0) return false;
        if (ribCenterZ >= topBottomZ - 1.0) return false;

        var webT = SegmentMath.ParamsAt(webSegs, midX).T;

        // База ребра (точка на поверхности стенки, outward=0) + наружная
        // единичная нормаль nVec + offsetAxis (направление толщины). Центр
        // плоской плиты = base + (h/2)·nVec; наклонный участок строится
        // трапецией base→base+h·nVec (зеркало web-side base/n).
        double baseEy, baseEz;
        Vec3 nVec, offsetAxis;

        if (wallOutwardSign != 0)
        {
            // Box mode: стенка наклонная. surfaceSign выбирает поверхность
            // (+1 outer / -1 inner). faceY — точка крепления на грани стенки.
            var tanT = Math.Tan(wallTiltRad);
            var cosT = Math.Cos(wallTiltRad);
            var sinT = Math.Sin(wallTiltRad);
            var walY = wallYOffset + wallOutwardSign * tanT * (ribCenterZ - webCenterZ);
            var faceY = walY + surfaceSign * wallOutwardSign * (webT / 2.0);
            baseEy = faceY;
            baseEz = ribCenterZ;
            if (orientation == RibOrientation.Perpendicular)
            {
                // Outward normal в (Ey,Ez): (surfaceSign·outwardSign·cos, −surfaceSign·sin).
                // offsetAxis (up-slant) = (outwardSign·sin, cos), общий для inner/outer.
                nVec = beam.Ey * (surfaceSign * wallOutwardSign * cosT) + beam.Ez * (-surfaceSign * sinT);
                offsetAxis = beam.Ey * (wallOutwardSign * sinT) + beam.Ez * cosT;
            }
            else
            {
                // Horizontal: outward вдоль Ey со знаком surfaceSign·outwardSign,
                // толщина — по Ez.
                nVec = beam.Ey * (surfaceSign * wallOutwardSign);
                offsetAxis = beam.Ez;
            }
        }
        else
        {
            // I-girder: одна стенка по центру. n = sideSign·(cos, −sin) в (Ey,Ez)
            // (rib middle plane перпендикулярна стенке); base = поверхность стенки
            // на стороне sideSign. offsetAxis = wall slant axis (sin, cos),
            // ОДИНАКОВЫЙ для обеих сторон → middle planes совпадают.
            var sideSign = NormalizedSideSign(side);
            var tanTi = Math.Tan(wallTiltRad);
            var sinTi = Math.Sin(wallTiltRad);
            var cosTi = Math.Cos(wallTiltRad);
            var walYi = wallYOffset + tanTi * (ribCenterZ - webCenterZ);
            baseEy = walYi + sideSign * cosTi * (webT / 2.0);
            baseEz = ribCenterZ - sideSign * sinTi * (webT / 2.0);
            nVec = beam.Ey * (sideSign * cosTi) + beam.Ez * (-sideSign * sinTi);
            offsetAxis = beam.Ey * sinTi + beam.Ez * cosTi;
        }

        var baseP1 = beam.Origin + beam.Ex * run.Start + beam.Ey * baseEy + beam.Ez * baseEz;
        var baseP2 = beam.Origin + beam.Ex * run.End + beam.Ey * baseEy + beam.Ez * baseEz;

        if (Math.Abs(heightStart - heightEnd) <= 1e-6)
        {
            // Плоский участок → прямоугольная плита (центр на base + (h/2)·nVec).
            var h = heightStart;
            var c1 = baseP1 + nVec * (h / 2.0);
            var c2 = baseP2 + nVec * (h / 2.0);
            var flat = PlatePartFactory.Create(c1, c2, nVec, offsetAxis, h, 0.0, ribThickness, material, name, "7");
            return flat != null;
        }

        // Наклонный участок (скос высоты) → трапециевидная ContourPlate. Контур
        // лежит в плоскости (Ex, nVec); толщина расходится ±t/2 по нормали этой
        // плоскости (= offsetAxis, Position.Depth=MIDDLE) — как у прямоугольной плиты.
        var outerP1 = baseP1 + nVec * heightStart;
        var outerP2 = baseP2 + nVec * heightEnd;
        var ramp = PlatePartFactory.CreateContour(
            new[] { baseP1, baseP2, outerP2, outerP1 }, ribThickness, material, name, "7");
        return ramp != null;
    }

    /// <summary>
    /// Раскладывает web-long-ribs из specs (включая mirrored для BothSides=true).
    /// Mirrors decomp CreateWebLongRibs :2202.
    /// </summary>
    public static int CreateWebLongRibs(
        BeamFrame beam, double axisLen,
        IReadOnlyList<SegLwt> webSegs,
        IReadOnlyList<SegLwt> bottomSegs, IReadOnlyList<SegLwt> topSegs,
        TopMode topMode, double deckT, double zTopRef, double zBaseLower,
        FlangeRef bottomRef, FlangeRef topRef,
        IReadOnlyList<WebLongRibSpec> specs,
        string material, string baseName,
        double wallYOffset = 0.0,
        double wallTiltRad = 0.0,
        int wallOutwardSign = 0,
        double webCenterZ = 0.0,
        HeightMode heightMode = HeightMode.ToTop)
    {
        if (specs is null || specs.Count == 0) return 0;
        var created = 0;
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            // Outer surface (default).
            if (CreateWebLongRib(beam, axisLen, webSegs, bottomSegs, topSegs, topMode, deckT, zTopRef, zBaseLower,
                    bottomRef, topRef, spec.Side, spec.Offset, spec.Start, spec.Length, spec.Height, spec.Thickness,
                    material, $"{baseName}_WLONG_{i + 1:D2}",
                    wallYOffset, wallTiltRad, wallOutwardSign, webCenterZ, spec.Orientation, surfaceSign: 1,
                    heightMode: heightMode, segments: spec.Segments))
                created++;
            if (spec.BothSides)
            {
                if (wallOutwardSign == 0)
                {
                    // I-girder: одна стенка по центру. BothSides → отзеркалить
                    // сторону (LEFT ↔ RIGHT). surfaceSign не применяется
                    // (для I-girder используется legacy sideSign).
                    var mirroredSide = NormalizeSide(spec.Side) == "RIGHT" ? "LEFT" : "RIGHT";
                    if (CreateWebLongRib(beam, axisLen, webSegs, bottomSegs, topSegs, topMode, deckT, zTopRef, zBaseLower,
                            bottomRef, topRef, mirroredSide, spec.Offset, spec.Start, spec.Length, spec.Height, spec.Thickness,
                            material, $"{baseName}_WLONG_{i + 1:D2}_M",
                            wallYOffset, wallTiltRad, wallOutwardSign, webCenterZ, spec.Orientation, surfaceSign: 1,
                            heightMode: heightMode, segments: spec.Segments))
                        created++;
                }
                else
                {
                    // Box mode: одна стенка имеет outer + inner поверхности.
                    // BothSides=true → второе ребро на INNER surface (surfaceSign=-1).
                    // Это паритет с web-side (placeWebLongMeshes выставляет
                    // surfaceSign=-1 для mirror entries из expandWebLongPlacements).
                    if (CreateWebLongRib(beam, axisLen, webSegs, bottomSegs, topSegs, topMode, deckT, zTopRef, zBaseLower,
                            bottomRef, topRef, spec.Side, spec.Offset, spec.Start, spec.Length, spec.Height, spec.Thickness,
                            material, $"{baseName}_WLONG_{i + 1:D2}_I",
                            wallYOffset, wallTiltRad, wallOutwardSign, webCenterZ, spec.Orientation, surfaceSign: -1,
                            heightMode: heightMode, segments: spec.Segments))
                        created++;
                }
            }
        }
        return created;
    }

    /// <summary>
    /// Раскладывает web-trans-ribs. Step в спеке — расстояние между соседними
    /// поперечными плитами вдоль X. Каждая плита нарезается на куски по Z
    /// между продольными ребрами (через SplitByHoles), чтобы не пересекать
    /// long-ribs. Mirrors decomp CreateWebTransRibs :2259.
    /// </summary>
    /// <param name="wallTopZ">Box-mode: реальный верх стенки по Z (для
    /// box+DeckSlopes стенка дотягивается до низа деки в её Y, что выше
    /// центрального значения TopBottomAtX). 0 = использовать TopBottomAtX.</param>
    public static int CreateWebTransRibs(
        BeamFrame beam, double axisLen,
        IReadOnlyList<SegLwt> webSegs,
        IReadOnlyList<SegLwt> bottomSegs, IReadOnlyList<SegLwt> topSegs,
        TopMode topMode, double deckT, double zTopRef, double zBaseLower,
        FlangeRef bottomRef, FlangeRef topRef,
        IReadOnlyList<WebLongRibSpec> longSpecs,
        IReadOnlyList<WebTransRibSpec> transSpecs,
        string material, string baseName,
        double wallYOffset = 0.0,
        double wallTiltRad = 0.0,
        int wallOutwardSign = 0,
        double webCenterZ = 0.0,
        double wallTopZ = 0.0,
        IReadOnlyList<ThicknessCutSpec>? bottomCuts = null,
        IReadOnlyList<ThicknessCutSpec>? topCuts = null,
        IReadOnlyList<SlopeSeg>? deckLeft = null,
        IReadOnlyList<SlopeSeg>? deckRight = null,
        SystemFrame? system = null)
    {
        if (transSpecs is null || transSpecs.Count == 0) return 0;

        var bottomCutsResolved = bottomCuts ?? Array.Empty<ThicknessCutSpec>();
        var topCutsResolved = topCuts ?? Array.Empty<ThicknessCutSpec>();
        var deckLeftResolved = deckLeft ?? Array.Empty<SlopeSeg>();
        var deckRightResolved = deckRight ?? Array.Empty<SlopeSeg>();

        var created = 0;
        var xCursor = 0.0;
        for (var i = 0; i < transSpecs.Count; i++)
        {
            xCursor += transSpecs[i].Step;
            if (xCursor <= 0.0 || xCursor >= axisLen) continue;
            created += CreateSingleWebTransRibAt(beam, xCursor, webSegs, bottomSegs, topSegs, topMode,
                deckT, zTopRef, zBaseLower, bottomRef, topRef, transSpecs[i], longSpecs, material,
                $"{baseName}_WTR_{i + 1:D2}",
                wallYOffset, wallTiltRad, wallOutwardSign, webCenterZ, wallTopZ,
                bottomCutsResolved, topCutsResolved, deckLeftResolved, deckRightResolved, system);
        }
        return created;
    }

    private static int CreateSingleWebTransRibAt(
        BeamFrame beam, double x,
        IReadOnlyList<SegLwt> webSegs,
        IReadOnlyList<SegLwt> bottomSegs, IReadOnlyList<SegLwt> topSegs,
        TopMode topMode, double deckT, double zTopRef, double zBaseLower,
        FlangeRef bottomRef, FlangeRef topRef,
        WebTransRibSpec trans,
        IReadOnlyList<WebLongRibSpec> longSpecs,
        string material, string baseName,
        double wallYOffset,
        double wallTiltRad,
        int wallOutwardSign,
        double webCenterZ,
        double wallTopZ,
        IReadOnlyList<ThicknessCutSpec> bottomCuts,
        IReadOnlyList<ThicknessCutSpec> topCuts,
        IReadOnlyList<SlopeSeg> deckLeft,
        IReadOnlyList<SlopeSeg> deckRight,
        SystemFrame? system)
    {
        // Chamfer-aware Z поверхностей поясов (учитывает thickness transitions
        // в зоне ramp'а, иначе step-function промахивается). BottomBaselineY
        // нужен для TO_BOTTOM heightMode (там baseline != 0).
        var zBottomFlange = WebCutters.BottomFlangeTopZ(
            bottomSegs, bottomCuts, bottomRef, x,
            system is { } sysBL ? sysBL.BottomBaselineY : 0.0);
        double zTopOverall;
        if (wallOutwardSign != 0 && wallTopZ > 0.0)
        {
            zTopOverall = wallTopZ;
        }
        else if (system is { } sys)
        {
            zTopOverall = WebCutters.TopZoneBottomZ(topSegs, topCuts, topMode, topRef, deckT, sys, x);
        }
        else
        {
            zTopOverall = RibGeometry.TopBottomAtX(topMode, topSegs, deckT, zTopRef, topRef, x);
        }
        if (zTopOverall - zBottomFlange <= 1.0) return 0;

        var webT = SegmentMath.ParamsAt(webSegs, x).T;

        string[] sides;
        if (wallOutwardSign != 0)
        {
            sides = new[] { wallOutwardSign > 0 ? "RIGHT" : "LEFT" };
        }
        else
        {
            sides = trans.BothSides ? new[] { "LEFT", "RIGHT" } : new[] { NormalizeSide(trans.Side) };
        }

        var tanT = Math.Tan(wallTiltRad);
        var created = 0;

        foreach (var side in sides)
        {
            var sideSign = side == "RIGHT" ? 1.0 : -1.0;

            // Y inner/outer rib edges как функция вертикального Z. Inner =
            // outer face стенки на этой Y, outer = inner + sideSign·depth.
            // При tilt≠0 стенка наклонена (как для box, так и для I-girder
            // c convention positive tilt = top в +Y).
            double InnerY(double z)
            {
                if (wallOutwardSign != 0)
                {
                    // Box: wall centerline Y(z) + outwardSign·(webT/2).
                    return wallYOffset + wallOutwardSign * tanT * (z - webCenterZ) + wallOutwardSign * (webT / 2.0);
                }
                // I-girder: стенка по центру, наклоняется на tanT. Inner edge
                // ребра = outer face стенки = sideSign·(webT/2) от centerline.
                return wallYOffset + tanT * (z - webCenterZ) + sideSign * (webT / 2.0);
            }
            double OuterY(double z) => InnerY(z) + sideSign * trans.Height;

            var webCenterZLocal = (zBottomFlange + zTopOverall) / 2.0;
            var holesExt = RibGeometry.BuildWebTransHolesAtXExt(
                longSpecs, side, x, zBaseLower, wallTiltRad,
                wallYOffset, webCenterZLocal, wallOutwardSign, webT);
            var holesPlain = new (double Low, double High)[holesExt.Length];
            for (var k = 0; k < holesExt.Length; k++) holesPlain[k] = (holesExt[k].Low, holesExt[k].High);
            var pieces = RibGeometry.SplitByHoles(zBottomFlange, zTopOverall, holesPlain);

            for (var j = 0; j < pieces.Length; j++)
            {
                var (zStart, zEnd) = pieces[j];
                if (zEnd - zStart <= 1.0) continue;

                var isBotPiece = Math.Abs(zStart - zBottomFlange) < 1.0;
                var isTopPiece = Math.Abs(zEnd - zTopOverall) < 1.0;

                // Inner/Outer Y trans rib (transverse).
                var yInnerStart = InnerY(zStart);
                var yOuterStart = OuterY(zStart);
                var yInnerEnd = InnerY(zEnd);
                var yOuterEnd = OuterY(zEnd);

                // Bottom edge Z. Если bot piece касается long top face — slope
                // -tan(tilt) по Y: Z(Y) = zStart - tan·(Y - yLongCenter).
                // Иначе (касается нижнего пояса) — горизонтальная.
                var tanTslope = Math.Tan(wallTiltRad);
                double zStartInner = zStart, zStartOuter = zStart;
                (double Low, double High, double YCenter, double ZCenter)? bandBelow = null;
                foreach (var h in holesExt)
                {
                    if (Math.Abs(h.High - zStart) < 1.0) { bandBelow = h; break; }
                }
                if (bandBelow.HasValue)
                {
                    zStartInner = zStart - tanTslope * (yInnerStart - bandBelow.Value.YCenter);
                    zStartOuter = zStart - tanTslope * (yOuterStart - bandBelow.Value.YCenter);
                }

                // Top edge Z: ortho-deck top piece → deck slope; long band above
                // → -tan slope; иначе horizontal.
                double zEndInner = zEnd, zEndOuter = zEnd;
                if (isTopPiece && topMode != TopMode.TopFlange)
                {
                    zEndInner = zTopRef + RibGeometry.DeckTopOffsetAtY(yInnerEnd, deckLeft, deckRight) - deckT;
                    zEndOuter = zTopRef + RibGeometry.DeckTopOffsetAtY(yOuterEnd, deckLeft, deckRight) - deckT;
                }
                else
                {
                    (double Low, double High, double YCenter, double ZCenter)? bandAbove = null;
                    foreach (var h in holesExt)
                    {
                        if (Math.Abs(h.Low - zEnd) < 1.0) { bandAbove = h; break; }
                    }
                    if (bandAbove.HasValue)
                    {
                        zEndInner = zEnd - tanTslope * (yInnerEnd - bandAbove.Value.YCenter);
                        zEndOuter = zEnd - tanTslope * (yOuterEnd - bandAbove.Value.YCenter);
                    }
                }

                var innerBot = beam.Origin + beam.Ex * x + beam.Ey * yInnerStart + beam.Ez * zStartInner;
                var outerBot = beam.Origin + beam.Ex * x + beam.Ey * yOuterStart + beam.Ez * zStartOuter;
                var outerTop = beam.Origin + beam.Ex * x + beam.Ey * yOuterEnd   + beam.Ez * zEndOuter;
                var innerTop = beam.Origin + beam.Ex * x + beam.Ey * yInnerEnd   + beam.Ez * zEndInner;
                // CCW при взгляде с +Ex: (innerBot, outerBot, outerTop, innerTop)
                // для sideSign>0 (Y увеличивается к outer). Для sideSign<0 порядок
                // должен быть обратным (outer лежит в -Y).
                var contour = sideSign > 0
                    ? new[] { innerBot, outerBot, outerTop, innerTop }
                    : new[] { innerBot, innerTop, outerTop, outerBot };
                _ = isBotPiece; // hook для будущей доводки до точного inner-face нижнего пояса

                var part = PlatePartFactory.CreateContour(
                    contour, trans.Thickness,
                    material, $"{baseName}_{side}_{j + 1:D2}", "7");
                if (part != null) created++;
            }
        }
        return created;
    }

    private static string NormalizeSide(string raw)
    {
        var text = (raw ?? "").Trim().ToUpperInvariant();
        if (text == "R" || text == "RIGHT") return "RIGHT";
        return "LEFT";
    }

    private static double NormalizedSideSign(string side) =>
        NormalizeSide(side) == "RIGHT" ? 1.0 : -1.0;
}
