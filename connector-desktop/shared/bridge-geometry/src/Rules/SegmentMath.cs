using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;

namespace Platform.Bridge.Geometry.Rules;

/// <summary>
/// Чистые арифметические утилиты над сегментными списками. Все методы
/// детерминированные, без побочных эффектов, без Tekla-зависимостей.
/// Используются <see cref="FlangeTransitionRules"/> и Step4/Step5
/// орхестрацией в Phase E.
/// </summary>
public static class SegmentMath
{
    /// <summary>
    /// Epsilon для сравнения W/T соседних сегментов — стандартный allowance
    /// при которой плагин **не** считает разницу значащей. Захардкожен в
    /// прежнем плагине, не выводится в payload.
    /// </summary>
    public const double SectionEpsilon = 0.01;

    /// <summary>
    /// Сумма длин всех сегментов вдоль оси X.
    /// </summary>
    public static double SumLength(IReadOnlyList<SegLwt> segs)
    {
        var total = 0.0;
        for (var i = 0; i < segs.Count; i++)
            total += segs[i].L;
        return total;
    }

    /// <summary>
    /// Максимальная толщина среди сегментов. 0 если список пустой —
    /// сохраняет совместимость с прежним плагином (вместо exception).
    /// </summary>
    public static double MaxThickness(IReadOnlyList<SegLwt> segs)
    {
        var max = 0.0;
        for (var i = 0; i < segs.Count; i++)
            if (segs[i].T > max) max = segs[i].T;
        return max;
    }

    /// <summary>
    /// Максимальная ширина среди сегментов. См. MaxThickness.
    /// </summary>
    public static double MaxWidth(IReadOnlyList<SegLwt> segs)
    {
        var max = 0.0;
        for (var i = 0; i < segs.Count; i++)
            if (segs[i].W > max) max = segs[i].W;
        return max;
    }

    /// <summary>
    /// Все ли сегменты имеют положительные L,W,T. Пустой список → true
    /// (vacuously satisfies). Step4/Step5 проверяет отдельно `segs.Length>0`.
    /// </summary>
    public static bool AllPositive(IReadOnlyList<SegLwt> segs)
    {
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.L <= 0.0 || s.W <= 0.0 || s.T <= 0.0) return false;
        }
        return true;
    }

    /// <summary>
    /// Есть ли хотя бы один стык где меняется ширина или толщина больше
    /// чем на <see cref="SectionEpsilon"/>. Если false — плита одинаковая
    /// по всей длине и переходов нет (Step4 выбирает быструю ветку).
    /// </summary>
    public static bool HasSectionVariation(IReadOnlyList<SegLwt>? segs)
    {
        if (segs is null || segs.Count < 2) return false;
        for (var i = 0; i < segs.Count - 1; i++)
        {
            if (Math.Abs(segs[i + 1].W - segs[i].W) > SectionEpsilon) return true;
            if (Math.Abs(segs[i + 1].T - segs[i].T) > SectionEpsilon) return true;
        }
        return false;
    }

    /// <summary>
    /// Множество breakpoint'ов вдоль оси X: 0, axisLen и все внутренние
    /// границы сегментов пояса и стенки. Сортированный массив без
    /// дубликатов. Используется для дискретизации mesh-стенки.
    /// </summary>
    public static double[] BuildBreakpoints(double axisLen, IReadOnlyList<SegLwt> flange, IReadOnlyList<SegLwt> web)
    {
        var set = new HashSet<double> { 0.0, axisLen };
        var cursor = 0.0;
        for (var i = 0; i < flange.Count; i++)
        {
            cursor += flange[i].L;
            if (cursor > 0.0 && cursor < axisLen) set.Add(cursor);
        }
        cursor = 0.0;
        for (var i = 0; i < web.Count; i++)
        {
            cursor += web[i].L;
            if (cursor > 0.0 && cursor < axisLen) set.Add(cursor);
        }
        var arr = new double[set.Count];
        set.CopyTo(arr);
        Array.Sort(arr);
        return arr;
    }

    /// <summary>
    /// Возвращает сегмент, в котором лежит координата s (накопительно
    /// по длинам). При s ≥ ΣL возвращает последний сегмент. Используется
    /// для интерполяции профиля в произвольной точке оси.
    /// </summary>
    public static SegLwt ParamsAt(IReadOnlyList<SegLwt> segs, double s)
    {
        if (segs.Count == 0) throw new InvalidOperationException("ParamsAt called with empty segs");
        var cursor = 0.0;
        for (var i = 0; i < segs.Count; i++)
        {
            cursor += segs[i].L;
            if (s <= cursor || i == segs.Count - 1)
                return segs[i];
        }
        return segs[segs.Count - 1];
    }

    /// <summary>
    /// Список точек разрыва в полуинтервале (s0, s1), плюс s0 и s1 как
    /// крайние. Включаются Start/End всех <see cref="ThicknessCutSpec"/>
    /// попадающих в интервал. Сортированно, без дубликатов.
    /// Используется для отрисовки переменной толщины полки/стенки между
    /// двумя bottom-breakpoints.
    /// </summary>
    public static List<double> CollectBreaks(double s0, double s1, IReadOnlyList<ThicknessCutSpec> cuts)
    {
        var set = new HashSet<double> { s0, s1 };
        for (var i = 0; i < cuts.Count; i++)
        {
            var c = cuts[i];
            if (c.Start > s0 && c.Start < s1) set.Add(c.Start);
            if (c.End > s0 && c.End < s1) set.Add(c.End);
        }
        var list = new List<double>(set);
        list.Sort();
        return list;
    }

    /// <summary>
    /// Толщина пояса в произвольной точке оси s. Если s попадает в
    /// активный ThicknessCutSpec, выдаёт линейно-интерполированную
    /// толщину между ThinT и ThickT. Иначе — толщина соответствующего
    /// сегмента из ParamsAt.
    /// </summary>
    public static double ThicknessAt(IReadOnlyList<SegLwt> segs, IReadOnlyList<ThicknessCutSpec> cuts, double s)
    {
        for (var i = 0; i < cuts.Count; i++)
        {
            var c = cuts[i];
            if (s >= c.Start && s <= c.End)
            {
                // Decrease=true: thickness ↓ по ходу X (текущая плита толще).
                // num/num2 в decomp = (from, to).
                var from = c.Decrease ? c.ThickT : c.ThinT;
                var to = c.Decrease ? c.ThinT : c.ThickT;
                var u = (s - c.Start) / (c.End - c.Start);
                return from + (to - from) * u;
            }
        }
        return ParamsAt(segs, s).T;
    }
}
