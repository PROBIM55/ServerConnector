using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Platform.Bridge.Geometry;
using Platform.Bridge.Geometry.Cuts;
using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Geometry3d;
using Platform.Bridge.Geometry.Parsing;
using Platform.Bridge.Geometry.Placement;
using Platform.Bridge.Geometry.Rules;
using Tekla.Structures.Model;
using TeklaBridge.TeklaShim;

namespace TeklaBridge;

/// <summary>
/// Команды плагина BridgeGirder, вызываемые через command.txt → result.txt.
/// CreateGirderStep4 — основной flow для TOP_FLANGE/DECK/DECK_SLOPES.
/// CreateGirderStep5 — wrapper над Step4 с дополнительным path для deck.
///
/// Mirrors decomp TeklaBridge.exe :430+ (BridgeCommands.CreateGirderStep4).
/// Бинарную парность с прежним TeklaBridge.exe проверяет Phase F.
/// </summary>
public static class BridgeCommands
{
    /// <summary>
    /// Основная команда генерации балки. parts — позиционные + именованные
    /// аргументы из command.txt. resultPath — куда писать success/ERROR.
    /// Возвращает 0 = ok, 1 = error (с подробностями в resultPath).
    /// </summary>
    /// <remarks>
    /// Phase E3 покрывает: parsing, validation, cleanup, BeamFrame/SystemFrame,
    /// размещение плит нижнего и верхнего поясов (TOP_FLANGE) через
    /// BeamPlacement.PlaceFlangePlates + PlatePartFactory.
    /// Не покрывает: transition cuts (Phase E4), web placement и web ribs
    /// (Phase E5), DECK/DECK_SLOPES top modes (Phase E5).
    /// </remarks>
    public static int CreateGirderStep4(Model model, string[] parts, string resultPath)
    {
        try
        {
            // ---------- parse / validate ----------
            if (parts.Length < 12)
            {
                File.WriteAllText(resultPath, "ERROR: create_girder_step4 needs 12 fields");
                return 1;
            }

            var startVec = ParseVec3(parts[1]);
            var endVec = ParseVec3(parts[2]);
            var h = Parsers.ParseDouble(parts[3]);
            var heightMode = Normalize.NormalizeHeightMode(parts[4]);
            var bottomRef = Normalize.NormalizeFlangeRef(parts[5], FlangeRef.TopLocked);
            var bottomSegs = Parsers.ParseLwtSegments(parts[6], "flange");
            var webSegs = Parsers.ParseLwtSegments(parts[7], "web");
            var topModeRaw = parts[8];
            var topMode = Normalize.NormalizeTopMode(topModeRaw);
            var topDataRaw = parts[9];
            var material = parts[10];
            var name = parts[11];

            var stressZone = Normalize.NormalizeStressZone(Parsers.ParseNamedArg(parts, "stressZone", 12));
            var topRef = Normalize.NormalizeFlangeRef(Parsers.ParseNamedArg(parts, "topRef", 12), FlangeRef.TopLocked);
            var (bottomRatio, topRatio) = stressZone.GetRatios();
            var componentId = Parsers.TryParseComponentId(parts, out var hasComponentIdArg);

            if (bottomSegs.Length == 0 || webSegs.Length == 0)
            {
                File.WriteAllText(resultPath, "ERROR: empty flange/web segments");
                return 1;
            }

            var beam = BeamFrame.Build(startVec, endVec);
            var axisLen = beam.AxisLength;
            var tolerance = Math.Max(1.0, axisLen * 0.001);
            if (axisLen < 1.0 || h <= 0.0)
            {
                File.WriteAllText(resultPath, "ERROR: invalid axis or height");
                return 1;
            }

            var bottomSum = SegmentMath.SumLength(bottomSegs);
            if (Math.Abs(bottomSum - axisLen) > tolerance)
            {
                File.WriteAllText(resultPath, $"ERROR: flange total length mismatch; sum={bottomSum:F3};axis={axisLen:F3}");
                return 1;
            }
            var webSum = SegmentMath.SumLength(webSegs);
            if (Math.Abs(webSum - axisLen) > tolerance)
            {
                File.WriteAllText(resultPath, $"ERROR: web total length mismatch; sum={webSum:F3};axis={axisLen:F3}");
                return 1;
            }
            if (!SegmentMath.AllPositive(bottomSegs) || !SegmentMath.AllPositive(webSegs))
            {
                File.WriteAllText(resultPath, "ERROR: flange/web segments must have L>0, W>0, T>0");
                return 1;
            }

            // ---------- topMode-specific data ----------
            SegLwt[] topSegs = Array.Empty<SegLwt>();
            double deckThickness = 0, deckWidth = 0;
            SlopeSeg[] deckLeft = Array.Empty<SlopeSeg>();
            SlopeSeg[] deckRight = Array.Empty<SlopeSeg>();
            switch (topMode)
            {
                case TopMode.TopFlange:
                    topSegs = Parsers.ParseLwtSegments(topDataRaw, "topFlange");
                    if (topSegs.Length == 0 || !SegmentMath.AllPositive(topSegs))
                    {
                        File.WriteAllText(resultPath, "ERROR: top flange segments must have L>0, W>0, T>0");
                        return 1;
                    }
                    var topSum = SegmentMath.SumLength(topSegs);
                    if (Math.Abs(topSum - axisLen) > tolerance)
                    {
                        File.WriteAllText(resultPath, $"ERROR: top flange total length mismatch; sum={topSum:F3};axis={axisLen:F3}");
                        return 1;
                    }
                    var clearTopFlange = h - SegmentMath.MaxThickness(bottomSegs) - SegmentMath.MaxThickness(topSegs);
                    if (clearTopFlange < 20.0)
                    {
                        File.WriteAllText(resultPath, $"ERROR: insufficient web clear height for TOP_FLANGE;clear={clearTopFlange:F3}");
                        return 1;
                    }
                    break;
                case TopMode.Deck:
                    var deckPair = Parsers.ParsePair(topDataRaw, "deckW,deckT");
                    deckWidth = deckPair.Item1;
                    deckThickness = deckPair.Item2;
                    if (deckWidth <= 0 || deckThickness <= 0)
                    {
                        File.WriteAllText(resultPath, "ERROR: deckW and deckT must be > 0");
                        return 1;
                    }
                    var clearDeck = h - SegmentMath.MaxThickness(bottomSegs) - deckThickness;
                    if (clearDeck < 20.0)
                    {
                        File.WriteAllText(resultPath, $"ERROR: insufficient web clear height for DECK;clear={clearDeck:F3}");
                        return 1;
                    }
                    break;
                case TopMode.DeckSlopes:
                    var slopes = Parsers.ParseDeckSlopes(topDataRaw);
                    deckThickness = slopes.DeckT;
                    deckLeft = slopes.Left;
                    deckRight = slopes.Right;
                    if (deckThickness <= 0)
                    {
                        File.WriteAllText(resultPath, "ERROR: deckT must be > 0 for DECK_SLOPES");
                        return 1;
                    }
                    if (deckLeft.Length == 0 || deckRight.Length == 0)
                    {
                        File.WriteAllText(resultPath, "ERROR: DECK_SLOPES requires both left and right slope lists");
                        return 1;
                    }
                    var clearDeckSlopes = h - SegmentMath.MaxThickness(bottomSegs) - deckThickness;
                    if (clearDeckSlopes < 20.0)
                    {
                        File.WriteAllText(resultPath, $"ERROR: insufficient web clear height for DECK_SLOPES;clear={clearDeckSlopes:F3}");
                        return 1;
                    }
                    break;
            }

            // ---------- cleanup existing parts ----------
            int deletedCount;
            string cleanupMode;
            if (hasComponentIdArg)
            {
                if (componentId > 0)
                {
                    deletedCount = PartCleanup.DeleteByFatherComponent(model, componentId);
                    cleanupMode = "COMPONENT_ID";
                }
                else
                {
                    deletedCount = 0;
                    cleanupMode = "SKIP_INVALID_COMPONENT_ID";
                }
            }
            else
            {
                deletedCount = PartCleanup.DeleteByNamePrefix(model, name);
                cleanupMode = "LEGACY_NAME_PREFIX";
            }

            // ---------- system frame ----------
            // wallTiltDeg парсится до SystemFrame для общей геометрии стенок.
            // H остаётся вертикальным габаритом и от наклона не зависит.
            var wallTiltDegStep4 = Parsers.ParseNamedDouble(parts, "wallTiltDeg", 12, 0.0);
            var wallTiltRadStep4 = (double.IsNaN(wallTiltDegStep4) ? 0.0 : wallTiltDegStep4) * Math.PI / 180.0;
            var system = SystemFrame.Build(h, heightMode, bottomSegs, topSegs, wallTiltRadStep4, bottomRef);

            // Flange orientation (042 §3 / П-3). PERPENDICULAR_TO_WALL применима
            // только к I-girder — у box обе стенки имеют противоположные
            // outwardSign и плита-пояс не может быть перпендикулярна обеим.
            // Geometry-слой делает fallback на HORIZONTAL для box, даже если
            // клиент прислал PERPENDICULAR_TO_WALL.
            var bottomFlangeOrientation = Parsers.ParseNamedArg(parts, "bottomFlangeOrientation", 12) ?? "HORIZONTAL";
            var topFlangeOrientation = Parsers.ParseNamedArg(parts, "topFlangeOrientation", 12) ?? "HORIZONTAL";
            var boxFlagRaw = Parsers.ParseNamedArg(parts, "box", 12) ?? "0";
            var isBoxForFlange = boxFlagRaw == "1" || boxFlagRaw.Equals("true", StringComparison.OrdinalIgnoreCase);
            // I_TWIN_ORTHO_DECK: две независимые двутавровые.
            var twinFlagRaw = Parsers.ParseNamedArg(parts, "twin", 12) ?? "0";
            var isTwin = twinFlagRaw == "1" || twinFlagRaw.Equals("true", StringComparison.OrdinalIgnoreCase);
            var twinSpacingStep4 = isTwin ? Parsers.ParseNamedDouble(parts, "twinSpacing", 12, 0.0) : 0.0;
            // BOX_SPLIT_TOP_FLANGES: коробка с 2 отдельными верхними поясами.
            var boxSplitTopRaw = Parsers.ParseNamedArg(parts, "boxSplitTop", 12) ?? "0";
            var isBoxSplitTop = (boxSplitTopRaw == "1" || boxSplitTopRaw.Equals("true", StringComparison.OrdinalIgnoreCase))
                && isBoxForFlange && topMode == TopMode.TopFlange;
            // boxRotationDeg: глобальный поворот всей коробки (стенки/пояса/рёбра)
            // вокруг продольной оси Ex. Применяется ТОЛЬКО для box-режимов
            // (для I-girder/twin игнорируется — нет физического смысла). Реализован
            // через BeamFrame.WithRoll: все последующие placement (PlaceFlangePlates,
            // wall построение, ribs) автоматически идут по повёрнутым Ey/Ez.
            var boxRotationDegStep4 = Parsers.ParseNamedDouble(parts, "boxRotationDeg", 12, 0.0);
            var boxRotationRadStep4 = (double.IsNaN(boxRotationDegStep4) ? 0.0 : boxRotationDegStep4) * Math.PI / 180.0;
            if (isBoxForFlange && Math.Abs(boxRotationRadStep4) > 1e-9)
            {
                beam = beam.WithRoll(boxRotationRadStep4);
            }
            var bottomFlangeTiltRad =
                bottomFlangeOrientation.Equals("PERPENDICULAR_TO_WALL", StringComparison.OrdinalIgnoreCase) && !isBoxForFlange
                    ? wallTiltRadStep4
                    : 0.0;
            var topFlangeTiltRad =
                topFlangeOrientation.Equals("PERPENDICULAR_TO_WALL", StringComparison.OrdinalIgnoreCase) && !isBoxForFlange
                    ? wallTiltRadStep4
                    : 0.0;

            // ---------- bottom flange ----------
            // Mirrors decomp Step4 :1123 — branching:
            // - all-same-section → BuildBreakpoints path: одна плита на segment-interval.
            // - section variation → GroupDetails: соседние одинаковые сливаются, разные дают
            //   отдельные плиты с trapezoidal transitions.
            // I_TWIN: 2 копии нижнего пояса на ±twinSpacing/2 в Ey direction.
            var bottomDetails = SegmentMath.HasSectionVariation(bottomSegs)
                ? FlangeTransitionRules.GroupDetails(bottomSegs, bottomRatio)
                : FlangeTransitionRules.BuildBreakpointDetails(bottomSegs);
            var twinOffsets = isTwin
                ? new[] { -twinSpacingStep4 / 2.0, +twinSpacingStep4 / 2.0 }
                : new[] { 0.0 };
            var twinSuffixes = isTwin ? new[] { "_W1", "_W2" } : new[] { "" };
            // Bottom plates строятся per-set (twin) сразу с cutters'ами, чтобы каждый
            // cutter получил правильный transverseOffset (Y) для своего набора плит.
            // bottomParts собирает все плиты обоих сетов для downstream-кода (web/ribs).
            var bottomParts = new List<Part>();
            var bottomSetPartsList = new List<List<Part>>();
            for (var iTwin = 0; iTwin < twinOffsets.Length; iTwin++)
            {
                var specs = BeamPlacement.PlaceFlangePlates(
                    bottomDetails, beam, system, bottomRef, isTopFlange: false, material, name + twinSuffixes[iTwin],
                    classId: "3", flangeTiltRad: bottomFlangeTiltRad,
                    transverseOffset: twinOffsets[iTwin]);
                var setParts = new List<Part>();
                foreach (var spec in specs)
                {
                    var part = PlatePartFactory.Create(spec);
                    if (part is null)
                    {
                        File.WriteAllText(resultPath, "ERROR: bottom flange create failed");
                        return 1;
                    }
                    setParts.Add(part);
                }
                bottomSetPartsList.Add(setParts);
                bottomParts.AddRange(setParts);
            }

            // ---------- top zone (flange / deck / deck-slopes) ----------
            var topParts = new List<Part>();
            // topSetPartsList — параллельно topSetOffsets, для per-set cutter
            // application (заполняется только в TopFlange ветке).
            List<List<Part>>? topSetPartsList = null;
            double[] topSetOffsets = Array.Empty<double>();
            switch (topMode)
            {
                case TopMode.TopFlange:
                {
                    var topDetails = SegmentMath.HasSectionVariation(topSegs)
                        ? FlangeTransitionRules.GroupDetails(topSegs, topRatio)
                        : FlangeTransitionRules.BuildBreakpointDetails(topSegs);
                    // BOX_SPLIT_TOP_FLANGES: 2 отдельных верхних пояса, по
                    // одному на стенку. Каждый центрирован на yTop стенки.
                    // Вычисляем yTop напрямую (webSides ещё не создан в этом
                    // месте кода): для box+TOP_FLANGE flat-top zTop = webTopOuterZ,
                    // yTop = sign·boxWidthBottom/2 + sign·(zTop-zBottom)·tan.
                    // Per-wall tilt: используем wallTiltRadLeft/Right из парсинга
                    // box-блока ниже (они нужны раньше — парсим здесь же
                    // дублирующе чтобы не зависеть от order). isBoxSplitTop
                    // включается только при isBoxForFlange + TopFlange.
                    double[] topFlangeOffsets;
                    string[] topFlangeSuffixes;
                    if (isBoxSplitTop)
                    {
                        var bwBSplit = Parsers.ParseNamedDouble(parts, "boxWidthBottom", 12, 0.0);
                        var wtdLeft = Parsers.ParseNamedDouble(parts, "wallTiltDegLeft", 12, double.NaN);
                        var wtdRight = Parsers.ParseNamedDouble(parts, "wallTiltDegRight", 12, double.NaN);
                        var commonDeg = double.IsNaN(wallTiltDegStep4) ? 0.0 : wallTiltDegStep4;
                        if (double.IsNaN(wtdLeft)) wtdLeft = commonDeg;
                        if (double.IsNaN(wtdRight)) wtdRight = commonDeg;
                        var tanL = Math.Tan(wtdLeft * Math.PI / 180.0);
                        var tanR = Math.Tan(wtdRight * Math.PI / 180.0);
                        // Для flat-top box используем фактический вертикальный
                        // span системного фрейма; tilt меняет только поперечный вынос.
                        var vSpan = system.TopBaselineY - system.BottomBaselineY;
                        var yTopL = -bwBSplit / 2.0 + (-1) * vSpan * tanL;
                        var yTopR = +bwBSplit / 2.0 + (+1) * vSpan * tanR;
                        topFlangeOffsets = new[] { yTopL, yTopR };
                        topFlangeSuffixes = new[] { "_TL", "_TR" };
                    }
                    else
                    {
                        // Single common top flange (I_TOP_FLANGE или BOX_TOP_FLANGE).
                        // При наклоне стенки верх стенки сдвигается по Y относительно
                        // её низа на vSpan·tan(tilt). Top flange должен следовать за
                        // верхом стенки, а не оставаться на Y=0:
                        //   I-girder (одна стенка): yTop = vSpan·tan(wallTiltRad).
                        //   BOX single common: midpoint между yTopL и yTopR
                        //                       (ассимметричный tilt → ненулевой midpoint).
                        // Для симметричной box с linked tilt результат = 0 (backward compat).
                        var vSpanCommon = system.TopBaselineY - system.BottomBaselineY;
                        double yTopCommon;
                        if (isBoxForFlange)
                        {
                            var bwB = Parsers.ParseNamedDouble(parts, "boxWidthBottom", 12, 0.0);
                            var wtdL = Parsers.ParseNamedDouble(parts, "wallTiltDegLeft", 12, double.NaN);
                            var wtdR = Parsers.ParseNamedDouble(parts, "wallTiltDegRight", 12, double.NaN);
                            var commonDeg = double.IsNaN(wallTiltDegStep4) ? 0.0 : wallTiltDegStep4;
                            if (double.IsNaN(wtdL)) wtdL = commonDeg;
                            if (double.IsNaN(wtdR)) wtdR = commonDeg;
                            var tanL = Math.Tan(wtdL * Math.PI / 180.0);
                            var tanR = Math.Tan(wtdR * Math.PI / 180.0);
                            var yTopL = -bwB / 2.0 + (-1) * vSpanCommon * tanL;
                            var yTopR = +bwB / 2.0 + (+1) * vSpanCommon * tanR;
                            yTopCommon = (yTopL + yTopR) / 2.0;
                        }
                        else
                        {
                            yTopCommon = vSpanCommon * Math.Tan(wallTiltRadStep4);
                        }
                        topFlangeOffsets = new[] { yTopCommon };
                        topFlangeSuffixes = new[] { "" };
                    }
                    // Top plates per-set (split → 2 шт., common → 1) — cutters
                    // применяются на каждый set отдельно с его transverseOffset.
                    topSetPartsList = new List<List<Part>>();
                    topSetOffsets = topFlangeOffsets;
                    for (var iTop = 0; iTop < topFlangeOffsets.Length; iTop++)
                    {
                        var specs = BeamPlacement.PlaceFlangePlates(
                            topDetails, beam, system, topRef, isTopFlange: true, material, name + topFlangeSuffixes[iTop],
                            classId: "3", flangeTiltRad: topFlangeTiltRad,
                            transverseOffset: topFlangeOffsets[iTop]);
                        var setParts = new List<Part>();
                        foreach (var spec in specs)
                        {
                            var part = PlatePartFactory.Create(spec);
                            if (part is null)
                            {
                                File.WriteAllText(resultPath, "ERROR: top flange create failed");
                                return 1;
                            }
                            setParts.Add(part);
                        }
                        topSetPartsList.Add(setParts);
                        topParts.AddRange(setParts);
                    }
                    break;
                }
                case TopMode.Deck:
                {
                    // Single deck plate centered at Y=0, top at TopBaselineY.
                    // Z center = TopBaselineY - deckT/2 (plate descends by half-thickness from top edge).
                    var deckCenterZ = system.TopBaselineY - deckThickness / 2.0;
                    var p1 = beam.LocalPoint(0, deckCenterZ);
                    var p2 = beam.LocalPoint(axisLen, deckCenterZ);
                    var deckPart = PlatePartFactory.Create(
                        p1, p2, beam.Ey, beam.Ez, deckWidth, 0.0, deckThickness,
                        material, name + "_DECK", "2");
                    if (deckPart is null)
                    {
                        File.WriteAllText(resultPath, "ERROR: deck create failed");
                        return 1;
                    }
                    topParts.Add(deckPart);
                    break;
                }
                case TopMode.DeckSlopes:
                {
                    var strips = DeckPlacement.BuildSlopedStrips(deckLeft, deckRight, deckThickness, system, name);
                    var created = DeckStripFactory.CreateStrips(strips, beam, material);
                    if (created is null || created.Count == 0)
                    {
                        File.WriteAllText(resultPath, "ERROR: deck slopes create failed");
                        return 1;
                    }
                    topParts.AddRange(created);
                    break;
                }
            }

            // ---------- transition cuts (Phase E4) ----------
            // Cutters применяются ПО НАБОРАМ: для twin → 2 набора нижнего пояса
            // на ±twinSpacing/2; для BOX_SPLIT_TOP_FLANGES → 2 набора верхнего пояса
            // на yTopL/yTopR. Каждый cutter передаёт свой transverseOffset, чтобы
            // полигон попал ПО Y туда же, где плиты — иначе boolean cut промахивается.
            // Также передаётся flangeTiltRad — cutter поворачивается синхронно с
            // плитой (PERPENDICULAR_TO_WALL орентация I-girder).
            var cutBottomT = 0; var cutBottomW = 0;
            for (var iSet = 0; iSet < bottomSetPartsList.Count; iSet++)
            {
                var setParts = bottomSetPartsList[iSet];
                var offY = twinOffsets[iSet];
                cutBottomT += CutterPartFactory.ApplyCuts(
                    FlangeCutters.BuildBottomThicknessCutters(
                        bottomSegs, bottomRatio, beam, bottomRef, setParts.Count,
                        name + twinSuffixes[iSet],
                        flangeTiltRad: bottomFlangeTiltRad, transverseOffset: offY),
                    setParts, material);
                cutBottomW += CutterPartFactory.ApplyCuts(
                    FlangeCutters.BuildBottomWidthCutters(
                        bottomSegs, bottomRatio, beam, bottomRef, setParts.Count,
                        name + twinSuffixes[iSet],
                        flangeTiltRad: bottomFlangeTiltRad, transverseOffset: offY),
                    setParts, material);
            }
            int cutTopT = 0, cutTopW = 0;
            if (topMode == TopMode.TopFlange && topSetPartsList is not null)
            {
                for (var iSet = 0; iSet < topSetPartsList.Count; iSet++)
                {
                    var setParts = topSetPartsList[iSet];
                    var offY = topSetOffsets[iSet];
                    cutTopT += CutterPartFactory.ApplyCuts(
                        FlangeCutters.BuildTopThicknessCutters(
                            topSegs, topRatio, beam, system, topRef, setParts.Count,
                            name + (iSet < topSetOffsets.Length ? $"_TS{iSet + 1}" : ""),
                            flangeTiltRad: topFlangeTiltRad, transverseOffset: offY),
                        setParts, material);
                    cutTopW += CutterPartFactory.ApplyCuts(
                        FlangeCutters.BuildTopWidthCutters(
                            topSegs, topRatio, beam, system, topRef, setParts.Count,
                            name + (iSet < topSetOffsets.Length ? $"_TS{iSet + 1}" : ""),
                            flangeTiltRad: topFlangeTiltRad, transverseOffset: offY),
                        setParts, material);
                }
            }

            // ---------- web placement (Phase E5a + E5b cuts) ----------
            // Стенка: одна центральная (I-girder) или две по бокам (box).
            // Box-mode: named arg "box=1" + boxWidthTop/boxWidthBottom задают
            // Y-смещения стенок относительно оси (вертикальные = top==bottom,
            // наклонные = top!=bottom, трапеция).
            // Mirrors decomp Step4 :1294-1329 для I-girder; box — расширение.
            var webBottomOuterZ = bottomRef == FlangeRef.BottomLocked
                ? system.BottomBaselineY
                : system.BottomBaselineY - SegmentMath.MaxThickness(bottomSegs);
            var webTopOuterZ = system.TopBaselineY;
            if (topMode == TopMode.TopFlange)
            {
                webTopOuterZ = topRef == FlangeRef.BottomLocked
                    ? system.TopBaselineY + SegmentMath.MaxThickness(topSegs)
                    : system.TopBaselineY;
            }
            var webDZ = webTopOuterZ - webBottomOuterZ;
            var webCenterZ = (webTopOuterZ + webBottomOuterZ) / 2.0;

            // Box-mode config. Primary input: boxWidthBottom (расст. между
            // стенками снизу) + wallTiltDeg (угол наклона от вертикали, deg).
            // boxWidthTop вычисляется из bottom + 2*h*tan(tilt). Legacy
            // boxWidthTop arg (для обратной совместимости) используется как
            // fallback если wallTiltDeg не задан.
            var boxModeRaw = Parsers.ParseNamedArg(parts, "box", 12) ?? "";
            var boxMode = boxModeRaw == "1" || boxModeRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || boxModeRaw.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var boxWidthBottom = boxMode ? Parsers.ParseNamedDouble(parts, "boxWidthBottom", 12, 0.0) : 0.0;
            var wallTiltDeg = boxMode ? Parsers.ParseNamedDouble(parts, "wallTiltDeg", 12, double.NaN) : 0.0;
            double boxWidthTop;
            if (boxMode)
            {
                if (!double.IsNaN(wallTiltDeg))
                {
                    var tiltRad = wallTiltDeg * Math.PI / 180.0;
                    boxWidthTop = boxWidthBottom + 2.0 * h * Math.Tan(tiltRad);
                }
                else
                {
                    // Legacy fallback: boxWidthTop передан явно.
                    boxWidthTop = Parsers.ParseNamedDouble(parts, "boxWidthTop", 12, boxWidthBottom);
                }
                if (boxWidthBottom < 1.0 || boxWidthTop < 1.0)
                {
                    File.WriteAllText(resultPath, "ERROR: box requires boxWidthBottom > 0 (and resulting boxWidthTop > 0)");
                    return 1;
                }
            }
            else
            {
                boxWidthTop = 0.0;
            }

            // Каждая стенка = (sideSign: -1/+1, yTop, yBottom, zTop, zBottom).
            // Для I-girder одна стенка по центру.
            // Для box+TopFlange и box+Deck (flat) zTop = webTopOuterZ (общий
            // для обеих стенок). Для box+DeckSlopes zTop варьируется по
            // стенкам — стенка должна дойти до нижней грани деки в её
            // фактической Y. Итерируем уравнение:
            //   yTop = yBottom + sign*tan(tilt)*(zTop - webBottomOuterZ)
            //   zTop = system.TopBaselineY - deckT + DeckTopOffsetAtY(yTop)
            // Per-wall tilt: parsing wallTiltDegLeft / wallTiltDegRight для box
            // с linked=false. При linked=true или отсутствии полей в args —
            // обе стенки используют общий wallTiltDeg (backward compat).
            var wallTiltDegLeft = Parsers.ParseNamedDouble(parts, "wallTiltDegLeft", 12, double.NaN);
            var wallTiltDegRight = Parsers.ParseNamedDouble(parts, "wallTiltDegRight", 12, double.NaN);
            var commonTiltDeg = double.IsNaN(wallTiltDeg) ? 0.0 : wallTiltDeg;
            if (double.IsNaN(wallTiltDegLeft)) wallTiltDegLeft = commonTiltDeg;
            if (double.IsNaN(wallTiltDegRight)) wallTiltDegRight = commonTiltDeg;
            var wallTiltRadLeft = wallTiltDegLeft * Math.PI / 180.0;
            var wallTiltRadRight = wallTiltDegRight * Math.PI / 180.0;

            (int Sign, double YTop, double YBottom, double ZTop, double ZBottom, double TiltRad)[] webSides;
            if (boxMode)
            {
                webSides = new (int, double, double, double, double, double)[2];
                for (var i = 0; i < 2; i++)
                {
                    var sign = i == 0 ? -1 : 1;
                    var tiltRadForBox = i == 0 ? wallTiltRadLeft : wallTiltRadRight;
                    var yBottom = sign * (boxWidthBottom / 2.0);
                    var zBottom = webBottomOuterZ;
                    double yTop, zTop;
                    if (topMode == TopMode.DeckSlopes)
                    {
                        // Iterate: yTop ⇄ zTop через slope-deck.
                        var cosT = Math.Max(1e-6, Math.Cos(tiltRadForBox));
                        var tanT = Math.Tan(tiltRadForBox);
                        zTop = webTopOuterZ;
                        yTop = yBottom + sign * (zTop - zBottom) * tanT;
                        for (var iter = 0; iter < 16; iter++)
                        {
                            var deckTopAtYTop = system.TopBaselineY + RibGeometry.DeckTopOffsetAtY(yTop, deckLeft, deckRight);
                            var newZTop = deckTopAtYTop - deckThickness;
                            var newYTop = yBottom + sign * (newZTop - zBottom) * tanT;
                            if (Math.Abs(newZTop - zTop) < 0.01 && Math.Abs(newYTop - yTop) < 0.01)
                            {
                                yTop = newYTop;
                                zTop = newZTop;
                                break;
                            }
                            yTop = newYTop;
                            zTop = newZTop;
                        }
                        // cosT used for sanity; if cos(tilt) very small (tilt close to 90°)
                        // wall would be horizontal — рекомендуем избегать.
                        _ = cosT;
                    }
                    else
                    {
                        // Flat-top (TopFlange / Deck): zTop common = webTopOuterZ.
                        // yTop per-wall: yBottom + h·tan(thisWallTilt). При
                        // linked tilt = одно значение → симметрия как раньше.
                        var tanFlat = Math.Tan(tiltRadForBox);
                        zTop = webTopOuterZ;
                        yTop = yBottom + sign * (zTop - zBottom) * tanFlat;
                    }
                    webSides[i] = (sign, yTop, yBottom, zTop, zBottom, tiltRadForBox);
                }
            }
            else if (isTwin)
            {
                // I_TWIN_ORTHO_DECK: две независимые двутавровые. Каждая ведёт
                // себя как I-girder (parallel walls, не splayed как box).
                // Per-wall tilt: W1 = wallTiltRadLeft, W2 = wallTiltRadRight
                // (для twin при linked=false; при linked=true оба = wallTiltDeg).
                var twinTanLeft = Math.Tan(wallTiltRadLeft);
                var twinTanRight = Math.Tan(wallTiltRadRight);
                var twinYBottomLeft = -twinSpacingStep4 / 2.0;
                var twinYBottomRight = +twinSpacingStep4 / 2.0;
                var twinYTopLeft = twinYBottomLeft + (webTopOuterZ - webBottomOuterZ) * twinTanLeft;
                var twinYTopRight = twinYBottomRight + (webTopOuterZ - webBottomOuterZ) * twinTanRight;
                // DECK_SLOPES: каждая стенка должна дойти до фактической нижней
                // грани наклонной деки на её Y_top (slope-aware). Простая
                // итерация: для каждой стенки находим Z_top где deck_bottom_at(Y_top) = Z_top.
                if (topMode == TopMode.DeckSlopes)
                {
                    for (var twinIter = 0; twinIter < 16; twinIter++)
                    {
                        var dl = system.TopBaselineY + RibGeometry.DeckTopOffsetAtY(twinYTopLeft, deckLeft, deckRight) - deckThickness;
                        var dr = system.TopBaselineY + RibGeometry.DeckTopOffsetAtY(twinYTopRight, deckLeft, deckRight) - deckThickness;
                        var newYL = twinYBottomLeft + (dl - webBottomOuterZ) * twinTanLeft;
                        var newYR = twinYBottomRight + (dr - webBottomOuterZ) * twinTanRight;
                        if (Math.Abs(newYL - twinYTopLeft) < 0.01 && Math.Abs(newYR - twinYTopRight) < 0.01) break;
                        twinYTopLeft = newYL;
                        twinYTopRight = newYR;
                    }
                    var twinZTopLeft = system.TopBaselineY + RibGeometry.DeckTopOffsetAtY(twinYTopLeft, deckLeft, deckRight) - deckThickness;
                    var twinZTopRight = system.TopBaselineY + RibGeometry.DeckTopOffsetAtY(twinYTopRight, deckLeft, deckRight) - deckThickness;
                    webSides = new[]
                    {
                        (0, twinYTopLeft, twinYBottomLeft, twinZTopLeft, webBottomOuterZ, wallTiltRadLeft),
                        (0, twinYTopRight, twinYBottomRight, twinZTopRight, webBottomOuterZ, wallTiltRadRight),
                    };
                }
                else
                {
                    webSides = new[]
                    {
                        (0, twinYTopLeft, twinYBottomLeft, webTopOuterZ, webBottomOuterZ, wallTiltRadLeft),
                        (0, twinYTopRight, twinYBottomRight, webTopOuterZ, webBottomOuterZ, wallTiltRadRight),
                    };
                }
            }
            else
            {
                // I-girder: одна стенка по центру. При wallTiltRad ≠ 0 стенка
                // наклоняется — её midplane не вертикальна. Convention (см. 042 §П-8):
                // положительный wallTiltDeg → верх стенки уходит в +Ey
                // (transverse positive). YBottom = 0 на оси балки.
                //
                // Для топ-mode DECK_SLOPES при tilt ≠ 0: верх стенки уходит в Y > 0,
                // а палуба наклонена → её низ на этой Y отличается от центра.
                // ВАЖНО: wall имеет толщину webT, её ВЕРХНИЙ УГОЛ (тот что
                // трогает деку) смещён от midplane на:
                //   ΔY = −sign(sin)·cos·halfT  (в направлении противоположном tilt)
                //   ΔZ = +|sin|·halfT          (выше midplane)
                // Раньше итерация сходилась на deck_bottom(Y_midplane), не учитывая
                // smещение угла → wall пробивал деку т.к. slope в Y_corner отличается.
                //
                // Корректная итерация: side.ZTop = Z_corner = deck_bottom(Y_corner),
                // где Y_corner = (Z_corner − halfTSin)·tan − sign(sin)·cos·halfT.
                // Wall midplane Z = Z_corner − halfTSin. После — zTopActual в
                // contour = side.ZTop − halfTSin = Z_mid → upper corner = Z_mid +
                // halfT·|sin| = Z_corner = deck_bottom_at_Y_corner. ✓
                //
                // halfT берём для МАКСИМАЛЬНОЙ web-толщины (worst case): для
                // тонких сегментов будет небольшой зазор (acceptable), для
                // толстых — точное касание без penetration.
                var iGirderTanTilt = Math.Tan(wallTiltRadStep4);
                var iGirderSinTilt = Math.Sin(wallTiltRadStep4);
                var iGirderCosTilt = Math.Cos(wallTiltRadStep4);
                var iGirderSinSign = iGirderSinTilt >= 0 ? 1.0 : -1.0;
                var iGirderHalfWebTMax = SegmentMath.MaxThickness(webSegs) / 2.0;
                var iGirderHalfTSinMax = iGirderHalfWebTMax * Math.Abs(iGirderSinTilt);
                var iGirderHalfTCos = iGirderHalfWebTMax * iGirderCosTilt;
                double iGirderYTop, iGirderZTop;
                if (topMode == TopMode.DeckSlopes)
                {
                    iGirderZTop = webTopOuterZ; // initial = Z_corner
                    for (var iter = 0; iter < 16; iter++)
                    {
                        var zMid = iGirderZTop - iGirderHalfTSinMax;
                        var yMid = (zMid - webBottomOuterZ) * iGirderTanTilt;
                        var cornerY = yMid - iGirderSinSign * iGirderHalfTCos;
                        var deckTopAtCorner = system.TopBaselineY + RibGeometry.DeckTopOffsetAtY(cornerY, deckLeft, deckRight);
                        var newZTop = deckTopAtCorner - deckThickness;
                        if (Math.Abs(newZTop - iGirderZTop) < 0.01)
                        {
                            iGirderZTop = newZTop;
                            break;
                        }
                        iGirderZTop = newZTop;
                    }
                    iGirderYTop = (iGirderZTop - iGirderHalfTSinMax - webBottomOuterZ) * iGirderTanTilt;
                }
                else
                {
                    iGirderZTop = webTopOuterZ;
                    iGirderYTop = (iGirderZTop - webBottomOuterZ) * iGirderTanTilt;
                }
                webSides = new[] { (0, iGirderYTop, 0.0, iGirderZTop, webBottomOuterZ, wallTiltRadStep4) };
            }

            // Web стенка: ОДНА ContourPlate на каждый webSeg (одна толщина),
            // с N контурными точками — по 2*N на пару (bottom + top) edges,
            // повторяющими фактические chamfer-ramp поясов. Все точки лежат
            // в ОДНОЙ плоскости стенки — wall plane = span(Ex, slant_unit),
            // где slant_unit зафиксирован baseline-anchor'ами side.YBottom/YTop,
            // side.ZBottom/ZTop (без учёта chamfer). Для каждого breakpoint x_i
            // вычисляем v_bot_i = (zBotActual_i - side.ZBottom) / sz и аналогично
            // v_top_i; точка контура = origin + Ex·x_i + slant_unit·v_i. Контур
            // PLANAR по построению (sub-section non-coplanarity убрана).
            //
            // touch-условие: при tilt=0 (I-girder) — обе кромки касаются полностью.
            // При tilt>0 (box) — outer-bottom касается нижнего пояса, inner-top
            // верхнего/деки, угол стенки к поясу немного меняется в зоне
            // chamfer'а (это нормально — пилёный металл).
            var bottomCutsForWeb = FlangeTransitionRules.BuildThicknessCuts(bottomSegs, bottomRatio);
            var topCutsForWeb = topMode == TopMode.TopFlange
                ? FlangeTransitionRules.BuildThicknessCuts(topSegs, topRatio)
                : new List<ThicknessCutSpec>();
            var webParts = new List<Part>();
            var webThicknessSummary = new List<string>();
            foreach (var side in webSides)
            {
                var widthVecY = side.YTop - side.YBottom;
                var widthVecZ = side.ZTop - side.ZBottom;
                var widthLenOverall = Math.Sqrt(widthVecY * widthVecY + widthVecZ * widthVecZ);
                if (widthLenOverall < 1.0) continue;
                // slant_unit = (sy, sz) в (Ey, Ez). Фиксирован для всей стороны
                // wall (без учёта chamfer-ramp поясов). sz < 1e-6 → стенка
                // почти горизонтальная — пропускаем (вырожденный случай).
                var sy = widthVecY / widthLenOverall;
                var sz = widthVecZ / widthLenOverall;
                if (Math.Abs(sz) < 1e-6) continue;
                var sinTiltAbs = Math.Abs(sy);
                var sideTag = boxMode ? (side.Sign < 0 ? "_L" : "_R") : "";

                if (side.Sign != 1)
                {
                    for (var i = 0; i < webSegs.Length; i++)
                    {
                        webThicknessSummary.Add(webSegs[i].T.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                }

                var cursor = 0.0;
                for (var i = 0; i < webSegs.Length; i++)
                {
                    var segStart = cursor;
                    var segEnd = Math.Min(cursor + webSegs[i].L, axisLen);
                    cursor += webSegs[i].L;
                    if (segEnd - segStart < 1.0) continue;
                    var t = webSegs[i].T;
                    // halfTSin поправляет midplane Z так, чтобы УГОЛ стенки касался
                    // ГОРИЗОНТАЛЬНОЙ плоскости пояса (стандарт HORIZONTAL). Для
                    // PERPENDICULAR_TO_WALL пояс параллелен направлению стенки —
                    // обе кромки стенки лежат В плоскости пояса (нет угла-протыкания),
                    // поправка halfTSin становится не нужна (даже вредна). Используем
                    // отдельный коэффициент per-flange: bottom/top могут быть с разной
                    // ориентацией (хотя обычно одинаковой).
                    var bottomHalfTSin = bottomFlangeTiltRad != 0.0 ? 0.0 : (t / 2.0) * sinTiltAbs;
                    var topHalfTSin = topFlangeTiltRad != 0.0 ? 0.0 : (t / 2.0) * sinTiltAbs;
                    var halfTSin = (t / 2.0) * sinTiltAbs; // legacy для совместимости; новый код использует *HalfTSin

                    // Sub-breakpoints внутри одного webSeg: union(segStart, segEnd,
                    // bottom chamfer breaks ∈ диапазона, top chamfer breaks ∈ диапазона).
                    var xs = new SortedSet<double> { segStart, segEnd };
                    foreach (var c in bottomCutsForWeb)
                    {
                        if (c.Start > segStart + 1e-3 && c.Start < segEnd - 1e-3) xs.Add(c.Start);
                        if (c.End > segStart + 1e-3 && c.End < segEnd - 1e-3) xs.Add(c.End);
                    }
                    foreach (var c in topCutsForWeb)
                    {
                        if (c.Start > segStart + 1e-3 && c.Start < segEnd - 1e-3) xs.Add(c.Start);
                        if (c.End > segStart + 1e-3 && c.End < segEnd - 1e-3) xs.Add(c.End);
                    }
                    var xsArr = new double[xs.Count];
                    xs.CopyTo(xsArr);

                    // Helper для точки контура: для z_actual возвращает 3D точку
                    // на wall plane (центр-line plate). v = (z_actual - side.ZBottom) / sz,
                    // Y = side.YBottom + v·sy, Z = side.ZBottom + v·sz (= z_actual).
                    Vec3 ContourPoint(double x, double zActual)
                    {
                        var v = (zActual - side.ZBottom) / sz;
                        var Y = side.YBottom + v * sy;
                        var Z = side.ZBottom + v * sz;
                        return beam.Origin + beam.Ex * x + beam.Ey * Y + beam.Ez * Z;
                    }

                    double TopZActualAt(double x)
                    {
                        // TopFlange варьируется по X (chamfer-ramp) → берём
                        // WebCutters.TopZoneBottomZ с учётом flangeTilt (PERP_TO_WALL
                        // даёт T·cos вместо T — пересечение wall midplane с
                        // повёрнутой плоскостью пояса).
                        if (topMode == TopMode.DeckSlopes) return side.ZTop;
                        return WebCutters.TopZoneBottomZ(
                            topSegs, topCutsForWeb, topMode, topRef, deckThickness, system, x,
                            flangeTiltRad: topFlangeTiltRad);
                    }

                    var contour = new List<Vec3>(xsArr.Length * 2);
                    // Bottom edge L→R: каждая точка на (zBotInner(x) + bottomHalfTSin).
                    for (var j = 0; j < xsArr.Length; j++)
                    {
                        var x = xsArr[j];
                        var zBotActual = WebCutters.BottomFlangeTopZ(
                            bottomSegs, bottomCutsForWeb, bottomRef, x, system.BottomBaselineY,
                            flangeTiltRad: bottomFlangeTiltRad) + bottomHalfTSin;
                        contour.Add(ContourPoint(x, zBotActual));
                    }
                    // Top edge R→L: каждая точка на (zTopInner(x) − topHalfTSin).
                    for (var j = xsArr.Length - 1; j >= 0; j--)
                    {
                        var x = xsArr[j];
                        var zTopActual = TopZActualAt(x) - topHalfTSin;
                        contour.Add(ContourPoint(x, zTopActual));
                    }

                    var webPart = PlatePartFactory.CreateContour(
                        contour, t,
                        material, $"{name}_WEB{sideTag}_{webParts.Count + 1:D2}", "4");
                    if (webPart is null)
                    {
                        File.WriteAllText(resultPath, "ERROR: web create failed");
                        return 1;
                    }
                    webParts.Add(webPart);
                }
            }
            var webPartCount = webParts.Count;

            // Web/deck ribs не строятся в Step4 — это base шаг (пояса + стенка
            // + transition cuts + web cuts). Ребра создаёт Step5 (см. ниже),
            // который вызывается из плагина-DLL когда в UI заданы rib-параметры.

            model.CommitChanges();

            // Result format follows decomp Step4 :1331. Phase E3 produces partial
            // counters with placeholders for E4/E5; Phase F adapts the parser
            // (bridge-desktop side) only after binary parity passes.
            var ratioBot = bottomRatio.ToString("0.###", CultureInfo.InvariantCulture);
            var ratioTop = topRatio.ToString("0.###", CultureInfo.InvariantCulture);
            var webThicknessSummaryJoined = string.Join("|", webThicknessSummary);
            File.WriteAllText(resultPath, $"OK:GIRDER_STEP4:WEB={webPartCount};BOTTOM={bottomParts.Count};TOP={topParts.Count};"
                + $"CUT_BOT_T={cutBottomT};CUT_BOT_W={cutBottomW};CUT_TOP_T={cutTopT};CUT_TOP_W={cutTopW};TR_BOT=0;TR_TOP=0;HP_BOT=0;WEB_T={webThicknessSummaryJoined};"
                + $"heightMode={heightMode};bottomRef={bottomRef};topRef={topRef};stressZone={stressZone};"
                + $"ratioBot={ratioBot};ratioTop={ratioTop};cleanup={cleanupMode};deleted={deletedCount};cmpid={componentId}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "ERROR: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Step5 — полная балка с ребрами. Layout аргументов 1:1 с decomp:
    ///   1-11: те же что Step4 (start, end, h, heightMode, bottomRef, flange,
    ///         web, topMode, topData, mat, name).
    ///   12=ribH, 13=ribT, 14=ribStepsLeft, 15=ribStepsRight.
    ///   16+ named: ribBothSides, ribStartLeft/Right, ribLengthLeft/Right,
    ///   webLongRibs, webTransRibs, topRef, placementMode, axisSourceObjectId,
    ///   ribSection, stressZone, cmpid.
    ///
    /// Flow: реконструирует args для Step4 (1-11 positional + named beginning
    /// from idx 16), запускает базовый Step4 (пояса+стенки+cuts), потом
    /// создаёт deck-ribs (для DECK/DECK_SLOPES) + web-long-ribs + web-trans-ribs.
    /// Mirrors decomp :822-942.
    /// </summary>
    public static int CreateGirderStep5(Model model, string[] parts, string resultPath)
    {
        try
        {
            if (parts.Length < 16)
            {
                File.WriteAllText(resultPath, "ERROR: create_girder_step5 needs 16 fields");
                return 1;
            }

            // 1. Сначала Step4 для базовой части — пересобираем args.
            var step4Args = new List<string> { "create_girder_step4" };
            for (var i = 1; i <= 11; i++) step4Args.Add(parts[i]);
            for (var i = 16; i < parts.Length; i++) step4Args.Add(parts[i]);
            var step4Rc = CreateGirderStep4(model, step4Args.ToArray(), resultPath);
            if (step4Rc != 0) return step4Rc;

            // 2. Парсим Step5-specific args.
            var startVec = ParseVec3(parts[1]);
            var endVec = ParseVec3(parts[2]);
            var h = Parsers.ParseDouble(parts[3]);
            var heightMode = Normalize.NormalizeHeightMode(parts[4]);
            var bottomRef = Normalize.NormalizeFlangeRef(parts[5], FlangeRef.TopLocked);
            var bottomSegs = Parsers.ParseLwtSegments(parts[6], "flange");
            var webSegs = Parsers.ParseLwtSegments(parts[7], "web");
            var topModeRaw = parts[8];
            var topMode = Normalize.NormalizeTopMode(topModeRaw);
            var topDataRaw = parts[9];
            var material = parts[10];
            var name = parts[11];
            var ribH = Parsers.ParseDouble(parts[12]);
            var ribT = Parsers.ParseDouble(parts[13]);
            var stepsLeft = Parsers.ParseDoubleSteps(parts[14]);
            var stepsRight = Parsers.ParseDoubleSteps(parts[15]);

            var topRef = Normalize.NormalizeFlangeRef(Parsers.ParseNamedArg(parts, "topRef", 16), FlangeRef.TopLocked);
            var ribBothSides = Parsers.ParseBool(Parsers.ParseNamedArg(parts, "ribBothSides", 16));
            var startLeft = Parsers.ParseNamedDouble(parts, "ribStartLeft", 16, 0.0);
            var lengthLeft = Parsers.ParseNamedDouble(parts, "ribLengthLeft", 16, double.MaxValue);
            var startRight = Parsers.ParseNamedDouble(parts, "ribStartRight", 16, 0.0);
            var lengthRight = Parsers.ParseNamedDouble(parts, "ribLengthRight", 16, double.MaxValue);
            var webLongRaw = Parsers.ParseNamedArg(parts, "webLongRibs", 16) ?? "";
            var webTransRaw = Parsers.ParseNamedArg(parts, "webTransRibs", 16) ?? "";

            // 3. Восстанавливаем систему координат + frame (как в Step4).
            var beam = BeamFrame.Build(startVec, endVec);
            var axisLen = beam.AxisLength;
            if (axisLen < 1.0) { File.WriteAllText(resultPath, "ERROR: invalid axis"); return 1; }

            // boxRotationDeg: глобальный roll всей коробки (см. Step4). Применяем
            // здесь же, до любых ribs/deck placement, чтобы все последующие
            // PlaceXxx-расчёты автоматически шли по повёрнутым Ey/Ez.
            var boxModeStep5EarlyRaw = Parsers.ParseNamedArg(parts, "box", 16) ?? "";
            var boxModeStep5Early = boxModeStep5EarlyRaw == "1" || boxModeStep5EarlyRaw.Equals("true", StringComparison.OrdinalIgnoreCase) || boxModeStep5EarlyRaw.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var boxRotationDegStep5 = Parsers.ParseNamedDouble(parts, "boxRotationDeg", 16, 0.0);
            var boxRotationRadStep5 = (double.IsNaN(boxRotationDegStep5) ? 0.0 : boxRotationDegStep5) * Math.PI / 180.0;
            if (boxModeStep5Early && Math.Abs(boxRotationRadStep5) > 1e-9)
            {
                beam = beam.WithRoll(boxRotationRadStep5);
            }

            // topSegs только для TOP_FLANGE; для DECK/DECK_SLOPES — пустой.
            var topSegs = topMode == TopMode.TopFlange
                ? Parsers.ParseLwtSegments(topDataRaw, "topFlange")
                : Array.Empty<SegLwt>();

            // deckT + slopes для DECK/DECK_SLOPES.
            double deckT = 0.0;
            SlopeSeg[] deckLeft = Array.Empty<SlopeSeg>();
            SlopeSeg[] deckRight = Array.Empty<SlopeSeg>();
            switch (topMode)
            {
                case TopMode.Deck:
                    var pair = Parsers.ParsePair(topDataRaw, "deckW,deckT");
                    deckT = pair.Item2;
                    break;
                case TopMode.DeckSlopes:
                    var slopes = Parsers.ParseDeckSlopes(topDataRaw);
                    deckT = slopes.DeckT;
                    deckLeft = slopes.Left;
                    deckRight = slopes.Right;
                    break;
            }

            // Z-уровни. zTopSystem = baseline верха системы (для DECK/DECK_SLOPES =
            // верх палубы); zBaseLower = baseline низа (с учётом heightMode).
            // H — вертикальный габарит от выбранной грани нижнего пояса.
            // wallTiltDeg парсится раньше для применения и в I-girder, и в box.
            var wallTiltDegEarly = Parsers.ParseNamedDouble(parts, "wallTiltDeg", 16, 0.0);
            var wallTiltDegLeftEarly = Parsers.ParseNamedDouble(parts, "wallTiltDegLeft", 16, double.NaN);
            var wallTiltDegRightEarly = Parsers.ParseNamedDouble(parts, "wallTiltDegRight", 16, double.NaN);
            var wallTiltRadEarly = (double.IsNaN(wallTiltDegEarly) ? 0.0 : wallTiltDegEarly) * Math.PI / 180.0;
            // Per-wall tilt: при unlinked используем left/right отдельно.
            // SystemFrame и v-span расчёты используют worst-case (max |sin|).
            var commonTiltStep5 = double.IsNaN(wallTiltDegEarly) ? 0.0 : wallTiltDegEarly;
            if (double.IsNaN(wallTiltDegLeftEarly)) wallTiltDegLeftEarly = commonTiltStep5;
            if (double.IsNaN(wallTiltDegRightEarly)) wallTiltDegRightEarly = commonTiltStep5;
            var wallTiltRadLeftEarly = wallTiltDegLeftEarly * Math.PI / 180.0;
            var wallTiltRadRightEarly = wallTiltDegRightEarly * Math.PI / 180.0;
            var worstTiltStep5 = Math.Abs(wallTiltRadLeftEarly) > Math.Abs(wallTiltRadRightEarly)
                ? wallTiltRadLeftEarly
                : wallTiltRadRightEarly;
            // SystemFrame + chamfer-aware thickness cuts для trans-rib geometry
            // (rib торцы должны прилегать к фактической поверхности пояса в
            // зоне chamfer-ramp, иначе step-function промахивается).
            var systemStep5 = SystemFrame.Build(h, heightMode, bottomSegs, topSegs, worstTiltStep5, bottomRef);
            var zTopSystem = systemStep5.TopBaselineY;
            var zBaseLower = systemStep5.BottomBaselineY;
            var verticalSpanStep5 = zTopSystem - zBaseLower;
            var zTopRef = zTopSystem;
            var stressZoneStep5 = Normalize.NormalizeStressZone(Parsers.ParseNamedArg(parts, "stressZone", 16));
            var (bottomRatioStep5, topRatioStep5) = stressZoneStep5.GetRatios();
            var bottomCutsStep5 = FlangeTransitionRules.BuildThicknessCuts(bottomSegs, bottomRatioStep5);
            var topCutsStep5 = topMode == TopMode.TopFlange
                ? FlangeTransitionRules.BuildThicknessCuts(topSegs, topRatioStep5)
                : new List<ThicknessCutSpec>();

            // 4. Deck ribs (под палубой) — только для DECK/DECK_SLOPES.
            var deckRibCount = 0;
            if ((topMode == TopMode.Deck || topMode == TopMode.DeckSlopes) && ribH > 0.0 && ribT > 0.0)
            {
                deckRibCount = RibFactory.CreateDeckRibs(
                    beam, axisLen, stepsLeft, stepsRight, ribBothSides, ribH, ribT,
                    startLeft, lengthLeft, startRight, lengthRight,
                    zTopSystem, deckT, topMode, deckLeft, deckRight,
                    material, name);
            }

            // 5. Web ribs (продольные + поперечные).
            // - I-girder: одна стенка по центру (wallYOffset=0), общие
            //   webLongRibs / webTransRibs.
            // - Box-girder: 2 стенки, у каждой собственные ribs:
            //   webLongRibsLeft + webTransRibsLeft → левая стенка (Y < 0),
            //   webLongRibsRight + webTransRibsRight → правая (Y > 0).
            //   Если per-wall пусто — fallback на общий webLongRibs / webTransRibs
            //   (применяется к обеим стенкам).
            var boxModeStep5Raw = Parsers.ParseNamedArg(parts, "box", 16) ?? "";
            var boxModeStep5 = boxModeStep5Raw == "1" || boxModeStep5Raw.Equals("true", StringComparison.OrdinalIgnoreCase) || boxModeStep5Raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
            var twinModeStep5Raw = Parsers.ParseNamedArg(parts, "twin", 16) ?? "";
            var twinModeStep5 = twinModeStep5Raw == "1" || twinModeStep5Raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            var twinSpacingStep5 = twinModeStep5 ? Parsers.ParseNamedDouble(parts, "twinSpacing", 16, 0.0) : 0.0;
            var boxWidthBottomStep5 = boxModeStep5 ? Parsers.ParseNamedDouble(parts, "boxWidthBottom", 16, 0.0) : 0.0;
            var wallTiltDegStep5 = boxModeStep5 ? Parsers.ParseNamedDouble(parts, "wallTiltDeg", 16, double.NaN) : 0.0;
            double boxWidthTopStep5;
            if (boxModeStep5)
            {
                if (!double.IsNaN(wallTiltDegStep5))
                {
                    var tiltRad5 = wallTiltDegStep5 * Math.PI / 180.0;
                    boxWidthTopStep5 = boxWidthBottomStep5 + 2.0 * h * Math.Tan(tiltRad5);
                }
                else
                {
                    boxWidthTopStep5 = Parsers.ParseNamedDouble(parts, "boxWidthTop", 16, boxWidthBottomStep5);
                }
            }
            else { boxWidthTopStep5 = 0.0; }

            // Y-смещения стенок (= mid-wall Y, на z=webCenterZ): RibFactory
            // ожидает walY = wallYOffset + tan(tilt)·(z − webCenterZ). Для box
            // mid-Y стенки = (yBottom + yTop)/2 = sign·(boxWidthBottom/2 + h·tan/2).
            // Для I-girder convention (см. Step4 webSides выше): wall bottom
            // anchored at Y=0, top в +Ey направлении (positive tilt → top в +Ey).
            // mid-Y = (0 + tan·verticalSpan)/2 = tan·verticalSpan/2. При tilt=0
            // → mid-Y=0 (стандартный I-girder, стенка вертикальная по центру).
            var wallYOffsets = boxModeStep5
                ? new[] { -(boxWidthTopStep5 + boxWidthBottomStep5) / 4.0, +(boxWidthTopStep5 + boxWidthBottomStep5) / 4.0 }
                : twinModeStep5
                ? new[]
                {
                    // W1: yBottom=-spacing/2, yTop=-spacing/2 + verticalSpan·tan_left.
                    // mid = -spacing/2 + verticalSpan·tan_left/2.
                    -twinSpacingStep5 / 2.0 + Math.Tan(wallTiltRadLeftEarly) * verticalSpanStep5 / 2.0,
                    // W2 аналогично с tan_right.
                    +twinSpacingStep5 / 2.0 + Math.Tan(wallTiltRadRightEarly) * verticalSpanStep5 / 2.0,
                }
                : new[] { Math.Tan(wallTiltRadEarly) * verticalSpanStep5 / 2.0 };
            // Per-wall tilt for ribs in twin: W1 = left, W2 = right.
            // (В box wallTiltRadsStep5 уже строится дальше из per-wall; для twin
            // ниже определяется отдельный массив.)

            // Per-wall rib specs (только box). Если пусты — fallback на общие.
            var commonLongSpecs = RibParsers.ParseWebLongRibs(webLongRaw);
            var commonTransSpecs = RibParsers.ParseWebTransRibs(webTransRaw);
            var longLeftRaw = Parsers.ParseNamedArg(parts, "webLongRibsLeft", 16) ?? "";
            var longRightRaw = Parsers.ParseNamedArg(parts, "webLongRibsRight", 16) ?? "";
            var transLeftRaw = Parsers.ParseNamedArg(parts, "webTransRibsLeft", 16) ?? "";
            var transRightRaw = Parsers.ParseNamedArg(parts, "webTransRibsRight", 16) ?? "";
            var longLeftSpecs = !string.IsNullOrWhiteSpace(longLeftRaw) ? RibParsers.ParseWebLongRibs(longLeftRaw) : commonLongSpecs;
            var longRightSpecs = !string.IsNullOrWhiteSpace(longRightRaw) ? RibParsers.ParseWebLongRibs(longRightRaw) : commonLongSpecs;
            var transLeftSpecs = !string.IsNullOrWhiteSpace(transLeftRaw) ? RibParsers.ParseWebTransRibs(transLeftRaw) : commonTransSpecs;
            var transRightSpecs = !string.IsNullOrWhiteSpace(transRightRaw) ? RibParsers.ParseWebTransRibs(transRightRaw) : commonTransSpecs;

            // tilt/center for box-mode rib placement (см. RibFactory.CreateWebLongRib).
            // webCenterZ — приблизительный центр стенки по Z, нужен для
            // вычисления Y стенки на любом уровне (walY = wallYOffset +
            // outwardSign*tan(tilt)*(z - webCenterZ)). Используем геом. центр
            // между zBaseLower и zTopRef (полная точность не нужна — ribs
            // ставятся на внешнюю поверхность стенки, погрешность от ref +
            // толщины поясов в пределах 1-2 мм при типичных углах <10°).
            // Per-wall tilt: ribs цикл ниже использует wallTiltRadsStep5[wallIdx]
            // для каждой стенки отдельно. wallTiltRadEarly остаётся для общих
            // расчётов (SystemFrame и пр.).
            var ribWebCenterZ = (zBaseLower + zTopRef) / 2.0;

            // Box+DeckSlopes: per-wall wallTopZ — реальная высота, на которой
            // стенка встречает низ деки в её Y (slope-aware). Без этого
            // trans rib стопается на центральном TopBottomAtX (= zTopRef -
            // deckT), не доходя до фактического верха наклонной стенки.
            // Iteration: walY ⇄ zTop через deckBottomAtY. Та же логика, что
            // в Step4 для wall plate placement (см. webSides).
            var wallTopZs = new double[wallYOffsets.Length];
            // Per-wall tilt в Step5 ribs: каждая стенка использует свой
            // wallTiltRadLeftEarly / wallTiltRadRightEarly.
            var wallTiltRadsStep5 = boxModeStep5
                ? new[] { wallTiltRadLeftEarly, wallTiltRadRightEarly }
                : twinModeStep5
                ? new[] { wallTiltRadLeftEarly, wallTiltRadRightEarly }
                : new[] { wallTiltRadEarly };
            if (boxModeStep5 && topMode == TopMode.DeckSlopes)
            {
                for (var i = 0; i < wallYOffsets.Length; i++)
                {
                    var sign = i == 0 ? -1 : 1;
                    var tanT = Math.Tan(wallTiltRadsStep5[i]);
                    var yBottomWall = sign * (boxWidthBottomStep5 / 2.0);
                    var zBottomWall = zBaseLower; // approx; точная webBottomOuterZ зависит от bottomRef
                    var zTop = zTopRef;
                    var yTop = yBottomWall + sign * (zTop - zBottomWall) * tanT;
                    for (var iter = 0; iter < 16; iter++)
                    {
                        var deckTopAtYTop = zTopRef + RibGeometry.DeckTopOffsetAtY(yTop, deckLeft, deckRight);
                        var newZTop = deckTopAtYTop - deckT;
                        var newYTop = yBottomWall + sign * (newZTop - zBottomWall) * tanT;
                        if (Math.Abs(newZTop - zTop) < 0.01 && Math.Abs(newYTop - yTop) < 0.01)
                        {
                            zTop = newZTop;
                            break;
                        }
                        yTop = newYTop;
                        zTop = newZTop;
                    }
                    wallTopZs[i] = zTop;
                }
            }

            var webLongCount = 0;
            var webTransCount = 0;
            for (var wallIdx = 0; wallIdx < wallYOffsets.Length; wallIdx++)
            {
                var wallSuffix = boxModeStep5
                    ? (wallIdx == 0 ? "_L" : "_R")
                    : twinModeStep5
                    ? (wallIdx == 0 ? "_W1" : "_W2")
                    : "";
                // twin: outwardSign=0 (I-girder rib branch), как у box не делаем
                // splay-сторону.
                var wallOutward = boxModeStep5 ? (wallIdx == 0 ? -1 : 1) : 0;
                var wallTiltRadForRibs = wallTiltRadsStep5[wallIdx];
                var longForWall = !boxModeStep5
                    ? commonLongSpecs
                    : wallIdx == 0 ? longLeftSpecs : longRightSpecs;
                var transForWall = !boxModeStep5
                    ? commonTransSpecs
                    : wallIdx == 0 ? transLeftSpecs : transRightSpecs;
                webLongCount += RibFactory.CreateWebLongRibs(
                    beam, axisLen, webSegs, bottomSegs, topSegs, topMode, deckT, zTopRef, zBaseLower,
                    bottomRef, topRef, longForWall, material, name + wallSuffix,
                    wallYOffsets[wallIdx], wallTiltRadForRibs, wallOutward, ribWebCenterZ,
                    heightMode: heightMode);
                webTransCount += RibFactory.CreateWebTransRibs(
                    beam, axisLen, webSegs, bottomSegs, topSegs, topMode, deckT, zTopRef, zBaseLower,
                    bottomRef, topRef, longForWall, transForWall, material, name + wallSuffix,
                    wallYOffsets[wallIdx], wallTiltRadForRibs, wallOutward, ribWebCenterZ, wallTopZs[wallIdx],
                    bottomCutsStep5, topCutsStep5, deckLeft, deckRight, systemStep5);
            }

            model.CommitChanges();

            File.WriteAllText(resultPath,
                $"OK:GIRDER_STEP5:RIBS={deckRibCount};WEB_LONG={webLongCount};WEB_TRANS={webTransCount}");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "ERROR: " + ex.Message);
            return 1;
        }
    }

    private static Vec3 ParseVec3(string raw)
    {
        var parts = raw.Split(',');
        if (parts.Length != 3)
            throw new FormatException("Point must be x,y,z");
        return new Vec3(
            Parsers.ParseDouble(parts[0]),
            Parsers.ParseDouble(parts[1]),
            Parsers.ParseDouble(parts[2]));
    }
}
