using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;

namespace Platform.Bridge.Geometry.Rules;

/// <summary>
/// Геометрические правила переходов между сегментами пояса.
/// Pure-логика без Tekla-зависимостей: вход — массив <see cref="SegLwt"/>
/// и ratio (1:N уклон); выход — типизированные DetailBoundary[] и
/// ThicknessCutSpec[], которые потом превращаются в физические Tekla-плиты
/// + бевельные срезы в Phase E.
///
/// Алгоритм портирован из декомпилированного TeklaBridge.exe
/// (<c>FlangeTransitionRules.GroupDetails</c> :216-287,
///  <c>BuildThicknessCuts</c> :289-356,
///  <c>BuildFlangeTransitionSpecs</c> :2502-2565).
/// Поведение сохранено бит-в-бит, иначе Phase F binary-parity не пройдёт.
/// </summary>
public static class FlangeTransitionRules
{
    /// <summary>
    /// Сливает соседние одинаковые сегменты в физические плиты с учётом
    /// переходов. На каждом стыке где меняется ширина (только) — текущая
    /// плита получает trapezoidal cut длиной <c>(|ΔW|/2)·ratio</c> на её
    /// "широкой" стороне, либо следующая плита получает такой же cut на
    /// её "широкой" стороне (зависит от того, какая шире). Если меняется
    /// только толщина — DetailBoundary списка это не отражает, для этого
    /// есть <see cref="BuildThicknessCuts"/>. Если меняются оба параметра
    /// — длина зоны =<c>max(widthLen, thicknessLen)</c>.
    /// </summary>
    /// <param name="segs">Сегменты пояса по оси X.</param>
    /// <param name="ratio">1:N уклон перехода (8 для растянутого пояса, 4 для сжатого).</param>
    /// <returns>Список физических плит (DetailBoundary) с границами Start..End и trapezoidal-зонами TransitionStart/End.</returns>
    public static List<DetailBoundary> GroupDetails(IReadOnlyList<SegLwt> segs, double ratio)
    {
        var list = new List<DetailBoundary>();
        if (segs is null || segs.Count == 0)
            return list;

        var start = 0.0;
        var current = segs[0];
        var transitionStart = 0.0;
        var cursor = 0.0;

        for (var i = 0; i < segs.Count - 1; i++)
        {
            var next = segs[i + 1];
            cursor += current.L;
            var widthChanged = Math.Abs(next.W - current.W) > SegmentMath.SectionEpsilon;
            var thicknessChanged = Math.Abs(next.T - current.T) > SegmentMath.SectionEpsilon;
            if (!widthChanged && !thicknessChanged)
            {
                current = next;
                continue;
            }

            var widthTaperLen = widthChanged ? Math.Abs(next.W - current.W) / 2.0 * ratio : 0.0;
            var thicknessTaperLen = thicknessChanged ? Math.Abs(next.T - current.T) * ratio : 0.0;
            var transitionLen = Math.Max(widthTaperLen, thicknessTaperLen);

            double end, currentTransitionEnd, nextStart, nextTransitionStart;
            // "Wider/thicker side absorbs the taper into its physical extent."
            // If width changes — comparison on W; otherwise comparison on T.
            var currentIsWider = widthChanged ? current.W > next.W : current.T > next.T;
            if (currentIsWider)
            {
                // Current plate extends past the theoretical joint by transitionLen,
                // with a trapezoid taper on its end (only if width changed).
                end = cursor + transitionLen;
                currentTransitionEnd = widthChanged ? transitionLen : 0.0;
                nextStart = cursor + transitionLen;
                nextTransitionStart = 0.0;
            }
            else
            {
                // Next plate (wider/thicker) absorbs the taper at its start.
                // Current plate ends short of the theoretical joint by transitionLen.
                end = cursor - transitionLen;
                currentTransitionEnd = 0.0;
                nextStart = cursor - transitionLen;
                nextTransitionStart = widthChanged ? transitionLen : 0.0;
            }

            list.Add(new DetailBoundary(
                Start: start,
                End: end,
                Width: current.W,
                Thickness: current.T,
                TransitionStart: transitionStart,
                TransitionEnd: currentTransitionEnd));

            start = nextStart;
            current = next;
            transitionStart = nextTransitionStart;
        }

        var totalLen = SegmentMath.SumLength(segs);
        list.Add(new DetailBoundary(
            Start: start,
            End: totalLen,
            Width: current.W,
            Thickness: current.T,
            TransitionStart: transitionStart,
            TransitionEnd: 0.0));

        return list;
    }

    /// <summary>
    /// "Plate-per-segment" размещение для случая одинаковых W/T у всех
    /// сегментов: каждый исходный SegLwt становится отдельным DetailBoundary
    /// (без TransitionStart/End — там butt-joint без скоса). Используется
    /// когда <see cref="SegmentMath.HasSectionVariation"/> = false; в этом
    /// случае decomp Step4 идёт через BuildBreakpoints-путь и создаёт по
    /// плите на каждый segment-interval (а не одну большую плиту).
    /// </summary>
    public static List<DetailBoundary> BuildBreakpointDetails(IReadOnlyList<SegLwt> segs)
    {
        var result = new List<DetailBoundary>();
        if (segs is null || segs.Count == 0) return result;
        var cursor = 0.0;
        foreach (var s in segs)
        {
            result.Add(new DetailBoundary(
                Start: cursor,
                End: cursor + s.L,
                Width: s.W,
                Thickness: s.T,
                TransitionStart: 0.0,
                TransitionEnd: 0.0));
            cursor += s.L;
        }
        return result;
    }

    /// <summary>
    /// Бевельные срезы на стыках с изменением толщины. Срез всегда на
    /// более толстой плите, длина = |ΔT|·ratio. Если на том же стыке
    /// меняется ширина — <see cref="ThicknessCutSpec.WidthOffset"/> смещает
    /// срез так, чтобы width-trans и thickness-trans были коаксиальны
    /// (вместе образуют один visible taper).
    ///
    /// Стыки с изменением только ширины (без T) НЕ генерируют
    /// ThicknessCutSpec — width-таперы зашиты в DetailBoundary geometry
    /// (TransitionStart/TransitionEnd), не как отдельные cuts.
    /// </summary>
    public static List<ThicknessCutSpec> BuildThicknessCuts(IReadOnlyList<SegLwt> segs, double ratio)
    {
        var list = new List<ThicknessCutSpec>();
        if (segs is null || segs.Count < 2)
            return list;

        var cursor = 0.0;
        // detailIndex tracks which physical detail (after GroupDetails merging)
        // this cut targets. Increments at every section-change joint (W OR T).
        var detailIndex = 0;

        for (var i = 0; i < segs.Count - 1; i++)
        {
            var current = segs[i];
            var next = segs[i + 1];
            cursor += current.L;

            var thicknessDiff = Math.Abs(current.T - next.T);
            var widthDiff = Math.Abs(current.W - next.W);
            var thicknessChanged = thicknessDiff > SegmentMath.SectionEpsilon;
            var widthChanged = widthDiff > SegmentMath.SectionEpsilon;

            if (!thicknessChanged && !widthChanged)
                continue; // no section change — no detail boundary

            if (!thicknessChanged)
            {
                // Width-only: advances detail index but no thickness cut.
                detailIndex++;
                continue;
            }

            var thicknessTaperLen = thicknessDiff * ratio;
            var widthOffset = 0.0;
            if (widthChanged)
                widthOffset = widthDiff / 2.0 * ratio - thicknessTaperLen;

            if (current.T > next.T)
            {
                var cutFromX = cursor + widthOffset;
                list.Add(new ThicknessCutSpec(
                    BoundaryIndex: i,
                    Decrease: true,
                    Theoretical: cursor,
                    Start: cutFromX,
                    End: cutFromX + thicknessTaperLen,
                    ThickT: current.T,
                    ThinT: next.T,
                    WidthOffset: widthOffset,
                    TargetDetailIndex: detailIndex));
            }
            else
            {
                var cutToX = cursor - widthOffset;
                list.Add(new ThicknessCutSpec(
                    BoundaryIndex: i,
                    Decrease: false,
                    Theoretical: cursor,
                    Start: cutToX - thicknessTaperLen,
                    End: cutToX,
                    ThickT: next.T,
                    ThinT: current.T,
                    WidthOffset: widthOffset,
                    TargetDetailIndex: detailIndex + 1));
            }
            detailIndex++;
        }
        return list;
    }

    /// <summary>
    /// Сводная спецификация переходов: одна зона Start..End на каждом
    /// section-changing стыке, объединяющая width+thickness taper'ы.
    /// Используется для построения helper-points в Tekla (Phase E).
    /// </summary>
    /// <remarks>
    /// Отличается от <see cref="BuildThicknessCuts"/> тем что:
    /// (a) генерирует spec даже для width-only стыков;
    /// (b) применяет clipping к границам исходных сегментов
    /// (Start не уходит левее (cursor - currentSeg.L), End не уходит правее
    /// (cursor + nextSeg.L));
    /// (c) фильтрует слишком короткие зоны (&lt;1mm) и инвертированные (End-Start &lt; 1).
    /// </remarks>
    public static FlangeTransitionSpec[] BuildFlangeTransitionSpecs(
        IReadOnlyList<SegLwt> segs,
        double widthRatio,
        double thicknessRatio)
    {
        if (segs is null || segs.Count < 2)
            return Array.Empty<FlangeTransitionSpec>();

        var list = new List<FlangeTransitionSpec>();
        var cursor = 0.0;

        for (var i = 0; i < segs.Count - 1; i++)
        {
            var current = segs[i];
            var next = segs[i + 1];
            cursor += current.L;

            var widthDiff = Math.Abs(next.W - current.W);
            var thicknessDiff = Math.Abs(next.T - current.T);
            var widthTaperLen = widthDiff / 2.0 * widthRatio;
            var thicknessTaperLen = thicknessDiff * thicknessRatio;
            var transitionLen = Math.Max(widthTaperLen, thicknessTaperLen);

            if (transitionLen < 1.0) continue;

            var widthChanged = widthDiff > SegmentMath.SectionEpsilon;
            var thicknessChanged = thicknessDiff > SegmentMath.SectionEpsilon;
            var widthOffset = 0.0;
            if (widthChanged && thicknessChanged)
                widthOffset = widthTaperLen - thicknessTaperLen;

            var currentIsWider = widthChanged ? current.W > next.W : current.T > next.T;
            double start, end;
            if (currentIsWider)
            {
                start = cursor + widthOffset;
                end = start + transitionLen;
            }
            else
            {
                end = cursor - widthOffset;
                start = end - transitionLen;
            }

            // Clip to source-segment span. Prevents transitions from leaking
            // past the previous/next segment's nominal extent (Step4 needs
            // physical helper-points to stay inside the plate).
            var prevStart = cursor - current.L;
            var nextEnd = cursor + next.L;
            if (start < prevStart) start = prevStart;
            if (end > nextEnd) end = nextEnd;

            if (end - start < 1.0) continue;

            list.Add(new FlangeTransitionSpec(
                BoundaryIndex: i,
                Theoretical: cursor,
                Start: start,
                End: end,
                WidthOffset: widthOffset,
                WidthChanged: widthChanged,
                ThicknessChanged: thicknessChanged,
                ToNextForWide: !currentIsWider));
        }
        return list.ToArray();
    }
}
