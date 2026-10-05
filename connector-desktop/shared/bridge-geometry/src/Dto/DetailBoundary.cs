namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Одна физическая плита пояса после слияния соседних одинаковых сегментов
/// и применения переходов по ширине/толщине. Start..End — границы плиты по
/// оси балки X. TransitionStart/TransitionEnd — длины трапециевидных зон
/// перехода на левом и правом конце (≥0, ≤ длины плиты).
/// </summary>
public readonly record struct DetailBoundary(
    double Start,
    double End,
    double Width,
    double Thickness,
    double TransitionStart,
    double TransitionEnd);
