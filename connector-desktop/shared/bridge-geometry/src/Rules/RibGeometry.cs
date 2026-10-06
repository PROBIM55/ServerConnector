using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;

namespace Platform.Bridge.Geometry.Rules;

/// <summary>
/// Pure-helpers для размещения ребер (deck-ribs, web-long-ribs, web-trans-ribs)
/// и геометрии палубы. Mirrors decomp TeklaBridge.exe методов:
/// <c>NormalizeRun</c> :2088, <c>DeckTopOffsetAtY</c> :2164,
/// <c>BottomTopAtX</c> :2406, <c>TopBottomAtX</c> :2416,
/// <c>BuildWebTransHolesAtX</c> :2314, <c>SplitByHoles</c> :2351.
/// </summary>
public static class RibGeometry
{
    /// <summary>
    /// Диапазон [start..end] вдоль оси балки, после clamp'а к [0..axisLen].
    /// Если length=double.MaxValue — берётся вся оставшаяся длина.
    /// </summary>
    public readonly struct RunRange
    {
        public double Start { get; }
        public double End { get; }
        public RunRange(double start, double end) { Start = start; End = end; }
        public double Span => End - Start;
    }

    /// <summary>
    /// Clamp (start, length) к диапазону [0..axisLen]. Mirrors decomp NormalizeRun.
    /// </summary>
    public static RunRange NormalizeRun(double axisLen, double start, double length)
    {
        var clampedStart = Math.Max(0.0, start);
        var available = Math.Max(0.0, axisLen - clampedStart);
        var clampedLength = Math.Max(0.0, Math.Min(available, length));
        return new RunRange(clampedStart, clampedStart + clampedLength);
    }

    /// <summary>
    /// Делит диапазон продольного ребра [run.Start..run.End] на под-диапазоны
    /// по ГРАНИЦАМ ИЗМЕНЕНИЯ ТОЛЩИНЫ СТЕНКИ. Соседние сегменты стенки одинаковой
    /// толщины объединяются — ребро остаётся ЦЕЛЬНОЙ плитой и НЕ наследует
    /// продольную разбивку стенки. Разрыв возникает ТОЛЬКО там, где толщина
    /// стенки реально меняется (сравнение соседних сегментов, eps =
    /// <see cref="SegmentMath.SectionEpsilon"/>): на уступе грань стенки
    /// сдвигается, и ребро обязано шагнуть отдельной плитой, чтобы сохранить
    /// высоту от поверхности стенки.
    /// Паритет с web-side buildWebThicknessSpans.
    /// </summary>
    public static List<RunRange> SplitRunByWebThickness(RunRange run, IReadOnlyList<SegLwt> webSegs)
    {
        var result = new List<RunRange>();
        if (run.Span <= 1e-6) return result;
        if (webSegs is null || webSegs.Count == 0)
        {
            result.Add(run);
            return result;
        }

        // Внутренние границы сегментов стенки, попадающие строго внутрь run и
        // только там, где толщина меняется (сравнение соседних сегментов).
        var cuts = new List<double>();
        var cursor = 0.0;
        for (var i = 0; i < webSegs.Count - 1; i++)
        {
            cursor += webSegs[i].L;
            if (cursor <= run.Start + 1e-6 || cursor >= run.End - 1e-6) continue;
            if (Math.Abs(webSegs[i + 1].T - webSegs[i].T) > SegmentMath.SectionEpsilon)
                cuts.Add(cursor);
        }

        var prev = run.Start;
        for (var i = 0; i < cuts.Count; i++)
        {
            if (cuts[i] - prev > 1e-6) result.Add(new RunRange(prev, cuts[i]));
            prev = cuts[i];
        }
        if (run.End - prev > 1e-6) result.Add(new RunRange(prev, run.End));
        if (result.Count == 0) result.Add(run);
        return result;
    }

    // ----------------- Фаза 2: участки ребра по длине + авто-скос -----------------

    /// <summary>Уклон авто-скоса перехода высоты: длина скоса = |Δh|·RibTaperRatio.</summary>
    public const double RibTaperRatio = 4.0;

    /// <summary>Участок ребра по длине в абсолютных X (после ResolveRibSections).</summary>
    public readonly struct RibSection
    {
        public double Start { get; }
        public double End { get; }
        public double Height { get; }
        public double Thickness { get; }
        public RibSection(double start, double end, double height, double thickness)
        {
            Start = start;
            End = end;
            Height = height;
            Thickness = thickness;
        }
    }

    /// <summary>Кусок профиля ребра: плоский (HeightStart==HeightEnd) или скос.</summary>
    public readonly struct RibProfilePiece
    {
        public double Start { get; }
        public double End { get; }
        public double HeightStart { get; }
        public double HeightEnd { get; }
        public double Thickness { get; }
        public RibProfilePiece(double start, double end, double heightStart, double heightEnd, double thickness)
        {
            Start = start;
            End = end;
            HeightStart = heightStart;
            HeightEnd = heightEnd;
            Thickness = thickness;
        }

        public bool IsRamp => Math.Abs(HeightEnd - HeightStart) > 1e-6;
    }

    /// <summary>
    /// Подгоняет длины участков под target: последний = остаток; при переборе
    /// сокращает с конца (мин. длина 0.001). Зеркало web-side fitLengthsToAxis.
    /// </summary>
    public static double[] FitLengthsToAxis(IReadOnlyList<double> lengths, double target)
    {
        var n = lengths.Count;
        var safe = new double[n];
        if (n == 0) return safe;
        const double minLen = 0.001;
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            safe[i] = Math.Max(minLen, Round3(lengths[i]));
            sum += safe[i];
        }
        if (target <= 0) return safe;
        var diff = Round3(target - sum);
        var tol = Math.Max(0.5, target * 0.0005);
        if (Math.Abs(diff) <= tol) return safe;
        if (diff > 0)
        {
            safe[n - 1] = Round3(safe[n - 1] + diff);
            return safe;
        }
        var reduceNeeded = Round3(-diff);
        for (var i = n - 1; i >= 0 && reduceNeeded > 0; i--)
        {
            var reducible = Round3(safe[i] - minLen);
            if (reducible <= 0) continue;
            var take = Math.Min(reducible, reduceNeeded);
            safe[i] = Round3(safe[i] - take);
            reduceNeeded = Round3(reduceNeeded - take);
        }
        return safe;
    }

    private static double Round3(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Разрешает участки ребра в абсолютные X. Без segments — один участок на
    /// весь run с номинальными H/T. С segments — длины подгоняются под run
    /// (последний = остаток), каждый участок несёт своё H/T (≤0 → fallback на
    /// номинал). Зеркало web-side resolveRibSections.
    /// </summary>
    public static List<RibSection> ResolveRibSections(
        IReadOnlyList<RibSegment>? segments, RunRange run, double nominalHeight, double nominalThickness)
    {
        var height = Math.Max(0.0, nominalHeight);
        var thickness = Math.Max(0.0, nominalThickness);
        var whole = new RibSection(run.Start, run.Start + run.Span, height, thickness);
        if (segments == null || segments.Count == 0) return new List<RibSection> { whole };

        var raw = new double[segments.Count];
        for (var i = 0; i < segments.Count; i++) raw[i] = Math.Max(0.0, segments[i].L);
        var lengths = FitLengthsToAxis(raw, run.Span);

        var result = new List<RibSection>(lengths.Length);
        var cursor = run.Start;
        for (var i = 0; i < lengths.Length; i++)
        {
            var len = lengths[i];
            if (len <= 1e-6) continue;
            var src = i < segments.Count ? segments[i] : segments[segments.Count - 1];
            var h = Math.Max(0.0, src.H);
            var t = Math.Max(0.0, src.T);
            result.Add(new RibSection(cursor, cursor + len, h > 0 ? h : height, t > 0 ? t : thickness));
            cursor += len;
        }
        return result.Count > 0 ? result : new List<RibSection> { whole };
    }

    /// <summary>
    /// Профиль ребра по длине: плоские участки + наклонные грани (скос) на
    /// переходах высоты. Скос длиной |Δh|·ratio ставится на конце левого участка,
    /// толщина скоса = толщина левого; на стыке толщина — ступенька. Скос
    /// клампится длиной участка. Зеркало web-side buildRibProfile.
    /// </summary>
    public static List<RibProfilePiece> BuildRibProfile(IReadOnlyList<RibSection> sections, double ratio)
    {
        var pieces = new List<RibProfilePiece>();
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var flatEnd = s.End;
            RibProfilePiece? ramp = null;
            if (i + 1 < sections.Count)
            {
                var next = sections[i + 1];
                if (Math.Abs(next.Height - s.Height) > 1e-6)
                {
                    var len = Math.Min(Math.Abs(next.Height - s.Height) * ratio, s.End - s.Start);
                    if (len > 1e-6)
                    {
                        ramp = new RibProfilePiece(s.End - len, s.End, s.Height, next.Height, s.Thickness);
                        flatEnd = s.End - len;
                    }
                }
            }
            if (flatEnd - s.Start > 1e-6)
                pieces.Add(new RibProfilePiece(s.Start, flatEnd, s.Height, s.Height, s.Thickness));
            if (ramp.HasValue) pieces.Add(ramp.Value);
        }
        return pieces;
    }

    /// <summary>Высота профиля ребра в точке X (линейно для скоса). Зеркало ribHeightAt.</summary>
    public static double RibHeightAt(RibProfilePiece piece, double x)
    {
        var span = piece.End - piece.Start;
        if (span <= 1e-9) return piece.HeightStart;
        var u = Math.Max(0.0, Math.Min(1.0, (x - piece.Start) / span));
        return piece.HeightStart + (piece.HeightEnd - piece.HeightStart) * u;
    }

    /// <summary>
    /// Z-offset верха палубы относительно baseline в точке Y (cross-axis).
    /// Для DECK_SLOPES: палуба ломаная по поперечному сечению, slope сегменты
    /// заданы отдельно для левой/правой стороны (Y &lt; 0 → left, Y &gt; 0 → right).
    /// S в SlopeSeg — наклон в ‰ (per mille = mm/m). Mirrors decomp DeckTopOffsetAtY :2164.
    /// </summary>
    public static double DeckTopOffsetAtY(
        double y,
        IReadOnlyList<SlopeSeg> left,
        IReadOnlyList<SlopeSeg> right)
    {
        if (Math.Abs(y) < 1e-9) return 0.0;
        var absY = Math.Abs(y);
        var consumed = 0.0;
        var zAccum = 0.0;
        var array = y < 0.0 ? left : right;
        for (var i = 0; i < array.Count; i++)
        {
            var segL = array[i].L;
            var segS = array[i].S;
            if (absY <= consumed + segL)
            {
                var remainder = absY - consumed;
                return zAccum + remainder * segS / 1000.0;
            }
            zAccum += segL * segS / 1000.0;
            consumed += segL;
        }
        return zAccum;
    }

    /// <summary>
    /// Z-координата верхней грани нижнего пояса в точке X. Для BOTTOM_LOCKED —
    /// варьируется по ThicknessAt; для TOP_LOCKED — фиксирован на zBaseLower
    /// (т.е. inner-face = baseline; outer-face уходит вниз по толщине).
    /// </summary>
    public static double BottomTopAtX(
        IReadOnlyList<SegLwt> flangeSegs,
        double x,
        double zBaseLower,
        FlangeRef bottomRef)
    {
        if (bottomRef == FlangeRef.TopLocked) return zBaseLower;
        var t = SegmentMath.ParamsAt(flangeSegs, x).T;
        return zBaseLower + t;
    }

    /// <summary>
    /// Z-координата нижней грани верхней зоны (top flange или палубы) в точке X.
    /// </summary>
    public static double TopBottomAtX(
        TopMode topMode,
        IReadOnlyList<SegLwt> topSegs,
        double deckT,
        double zTopRef,
        FlangeRef topRef,
        double x)
    {
        if (topMode != TopMode.TopFlange) return zTopRef - deckT;
        if (topRef == FlangeRef.BottomLocked) return zTopRef;
        var t = topSegs.Count == 0 ? 0.0 : SegmentMath.ParamsAt(topSegs, x).T;
        return zTopRef - t;
    }

    /// <summary>
    /// Список Z-интервалов «дырок» в стенке на сечении X=x, образованных
    /// продольными ребрами (web-long). Каждое long-ребро формирует hole
    /// шириной = его толщина (по Z), центрированный на (zLongRef + offset).
    /// Используется при построении web-trans-ribs (split поперечной плиты
    /// по holes, чтобы не пересекать продольные ребра).
    /// Mirrors decomp BuildWebTransHolesAtX :2314.
    /// </summary>
    public static (double Low, double High)[] BuildWebTransHolesAtX(
        IReadOnlyList<WebLongRibSpec> longSpecs,
        string side,
        double x,
        double zLongRef,
        double wallTiltRad = 0.0)
    {
        var ext = BuildWebTransHolesAtXExt(longSpecs, side, x, zLongRef, wallTiltRad, 0.0, 0.0, 0, 0.0);
        var result = new (double Low, double High)[ext.Length];
        for (var i = 0; i < ext.Length; i++) result[i] = (ext[i].Low, ext[i].High);
        return result;
    }

    /// <summary>
    /// Расширенная версия с YCenter каждого long rib — для slope-адаптации
    /// piece torсу при наклонной стенке (long top/bottom faces перпендикулярны
    /// slant, не горизонтальны). Требует geometry-context (wallYOffset,
    /// webCenterZ, wallOutwardSign, webT) для вычисления transverse Y центра.
    /// </summary>
    public static (double Low, double High, double YCenter, double ZCenter)[] BuildWebTransHolesAtXExt(
        IReadOnlyList<WebLongRibSpec> longSpecs,
        string side,
        double x,
        double zLongRef,
        double wallTiltRad,
        double wallYOffset,
        double webCenterZ,
        int wallOutwardSign,
        double webT)
    {
        if (longSpecs is null || longSpecs.Count == 0) return Array.Empty<(double, double, double, double)>();
        var cosTilt = System.Math.Abs(System.Math.Cos(wallTiltRad));
        var sinTilt = System.Math.Sin(wallTiltRad);
        var tanTilt = System.Math.Tan(wallTiltRad);
        var offsetScale = cosTilt > 1e-6 ? cosTilt : 1.0;

        var holes = new List<(double, double, double, double)>();
        for (var i = 0; i < longSpecs.Count; i++)
        {
            var spec = longSpecs[i];
            if (spec.Height <= 0.0 || spec.Thickness <= 0.0 || spec.Length <= 0.0) continue;

            var primarySide = NormalizeSide(spec.Side);
            TryAdd(primarySide, isMirror: false);
            if (spec.BothSides) TryAdd(primarySide == "RIGHT" ? "LEFT" : "RIGHT", isMirror: true);

            void TryAdd(string ribSide, bool isMirror)
            {
                if (ribSide != side) return;
                var run = NormalizeRun(double.MaxValue, spec.Start, spec.Length);
                if (x < run.Start - 1e-6 || x > run.End + 1e-6) return;
                var centerZ = zLongRef + spec.Offset * offsetScale;
                var halfT = (spec.Thickness / 2.0) * offsetScale;
                var low = centerZ - halfT;
                var high = centerZ + halfT;

                // Y_long_center в (Ey, Ez) plane (transverse Y).
                double yCenter;
                if (wallOutwardSign != 0)
                {
                    var lvlSurfaceSign = isMirror ? -1 : 1;
                    var walY = wallYOffset + wallOutwardSign * tanTilt * (centerZ - webCenterZ);
                    var surfaceFaceY = walY + lvlSurfaceSign * wallOutwardSign * (webT / 2.0);
                    var outY = lvlSurfaceSign * wallOutwardSign * System.Math.Cos(wallTiltRad);
                    yCenter = surfaceFaceY + outY * (spec.Height / 2.0);
                }
                else
                {
                    var sideSignLvl = ribSide == "RIGHT" ? 1 : -1;
                    var walY = wallYOffset + tanTilt * (centerZ - webCenterZ);
                    var outerFaceY = walY + sideSignLvl * (webT / 2.0);
                    var outY = sideSignLvl * System.Math.Cos(wallTiltRad);
                    yCenter = outerFaceY + outY * (spec.Height / 2.0);
                }
                _ = sinTilt; // защита от unused-var (формула может использовать sin в будущих refinements)

                holes.Add((low, high, yCenter, centerZ));
            }
        }
        return holes.ToArray();
    }

    /// <summary>
    /// Сплит интервала [from..to] исключая holes. Каждая «дыра» — закрытый
    /// (Low..High) интервал; результат — список непрерывных подинтервалов
    /// которые в дырки не попадают. Used для web-trans-ribs: трансверсальная
    /// плита нарезается на куски между продольными ребрами.
    /// Mirrors decomp SplitByHoles :2351.
    /// </summary>
    public static (double Start, double End)[] SplitByHoles(
        double from,
        double to,
        IReadOnlyList<(double Low, double High)> holes)
    {
        if (to <= from) return Array.Empty<(double, double)>();
        if (holes is null || holes.Count == 0) return new[] { (from, to) };

        var clipped = new List<(double Low, double High)>(holes.Count);
        for (var i = 0; i < holes.Count; i++)
        {
            var low = Math.Max(from, Math.Min(to, holes[i].Low));
            var high = Math.Max(from, Math.Min(to, holes[i].High));
            if (high - low > 1e-6) clipped.Add((low, high));
        }
        clipped.Sort((a, b) => a.Low.CompareTo(b.Low));

        var pieces = new List<(double, double)>();
        var cursor = from;
        foreach (var hole in clipped)
        {
            if (hole.Low > cursor) pieces.Add((cursor, hole.Low));
            if (hole.High > cursor) cursor = hole.High;
            if (cursor >= to) break;
        }
        if (cursor < to) pieces.Add((cursor, to));
        return pieces.ToArray();
    }

    private static string NormalizeSide(string raw)
    {
        var text = (raw ?? "").Trim().ToUpperInvariant();
        if (text == "R" || text == "RIGHT") return "RIGHT";
        return "LEFT";
    }
}
