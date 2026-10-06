using Platform.Bridge.Geometry.Dto;
using Platform.Bridge.Geometry.Rules;

namespace Platform.Bridge.Geometry.Placement;

/// <summary>
/// Высотная схема балки: baseline-уровни для центров плит низа и верха.
/// Pure-вычисление, без Tekla-зависимостей. Все Y-координаты считаются
/// относительно нулевой baseline балки.
/// </summary>
/// <remarks>
/// BottomBaselineY — фиксируемая грань нижнего пояса. H является вертикальным
/// габаритом от выбранной HeightMode фактической грани нижнего пояса до верха
/// системы. Наклон стенки меняет её slant-длину, но не H.
///
/// Центр плиты Y вычисляется через <see cref="PlateCenterY"/>:
///   * BOTTOM_LOCKED → baseline + T/2 (плита над baseline).
///   * TOP_LOCKED    → baseline - T/2 (плита под baseline).
/// </remarks>
public readonly record struct SystemFrame(double BottomBaselineY, double TopBaselineY, double WebClearHeight)
{
    /// <summary>
    /// Построить SystemFrame по заданной высоте балки и сегментам поясов.
    /// Возвращает структуру с baseline-уровнями и зазором для стенки.
    /// </summary>
    /// <param name="h">Вертикальный габарит от выбранной грани нижнего пояса
    /// до верха системы.</param>
    /// <param name="heightMode">Как интерпретировать H — до верха или до низа.</param>
    /// <param name="bottomSegs">Сегменты нижнего пояса (нужен для MaxThickness).</param>
    /// <param name="topSegs">Сегменты верхнего пояса (для WebClearHeight).</param>
    /// <param name="wallTiltRad">Угол наклона стенки от вертикали (рад).
    /// Сохранён в сигнатуре для совместимости; на вертикальный габарит H не влияет.</param>
    /// <param name="bottomRef">Фиксируемая грань нижнего пояса.</param>
    public static SystemFrame Build(
        double h,
        HeightMode heightMode,
        System.Collections.Generic.IReadOnlyList<SegLwt> bottomSegs,
        System.Collections.Generic.IReadOnlyList<SegLwt> topSegs,
        double wallTiltRad = 0.0,
        FlangeRef bottomRef = FlangeRef.TopLocked)
    {
        var bottomMaxT = SegmentMath.MaxThickness(bottomSegs);
        var topMaxT = SegmentMath.MaxThickness(topSegs);
        _ = wallTiltRad;
        const double bottomBaseline = 0.0;
        var bottomBottom = bottomRef == FlangeRef.BottomLocked ? bottomBaseline : bottomBaseline - bottomMaxT;
        var bottomTop = bottomRef == FlangeRef.BottomLocked ? bottomBaseline + bottomMaxT : bottomBaseline;
        var heightDatum = heightMode == HeightMode.ToBottom ? bottomBottom : bottomTop;
        var topBaseline = heightDatum + h;
        var webClear = topBaseline - topMaxT - bottomTop;
        return new SystemFrame(bottomBaseline, topBaseline, webClear);
    }

    /// <summary>
    /// Возвращает Y центра плиты пояса заданной толщины с учётом ссылки.
    /// </summary>
    public double PlateCenterY(double thickness, FlangeRef flangeRef, bool isTopFlange)
    {
        var baseline = isTopFlange ? TopBaselineY : BottomBaselineY;
        return flangeRef == FlangeRef.BottomLocked
            ? baseline + thickness / 2.0
            : baseline - thickness / 2.0;
    }
}
