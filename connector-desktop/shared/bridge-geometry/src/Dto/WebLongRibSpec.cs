using System.Collections.Generic;

namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Ориентация продольного ребра относительно стенки (актуально для box-mode
/// с наклонными стенками).
/// </summary>
public enum RibOrientation
{
    /// <summary>Перпендикулярно поверхности стенки (rib наклоняется вместе со стенкой).</summary>
    Perpendicular,
    /// <summary>Горизонтально (rib всегда параллельно земле, торчит из стенки под углом).</summary>
    Horizontal,
}

/// <summary>
/// Участок продольного ребра по длине (Фаза 2): своё сечение H/T на отрезке.
/// Длина последнего участка трактуется как «остаток» эффективного диапазона
/// ребра (как сегменты поясов). H/T ≤ 0 → fallback на номинальные Height/Thickness.
/// </summary>
/// <param name="L">Длина участка вдоль оси балки (мм).</param>
/// <param name="H">Высота ребра на участке (выступ от стенки).</param>
/// <param name="T">Толщина ребра на участке.</param>
public readonly record struct RibSegment(double L, double H, double T);

/// <summary>
/// Продольное ребро стенки (стиффенер). Идёт вдоль оси балки на одной
/// высоте Offset от низа стенки.
/// </summary>
/// <param name="Side">"left" или "right" — на какой стороне стенки расположено ребро.</param>
/// <param name="BothSides">Если true, ребро дублируется зеркально на противоположной стороне (тогда Side задаёт первичную).</param>
/// <param name="Offset">Вертикальное смещение от низа стенки до центра ребра (мм).</param>
/// <param name="Start">X-координата начала ребра вдоль оси балки (мм).</param>
/// <param name="Length">Длина ребра по оси балки (мм).</param>
/// <param name="Height">Высота ребра (выступ от стенки в поперечном направлении).</param>
/// <param name="Thickness">Толщина ребра (вдоль оси X через узкое измерение пластины).</param>
/// <param name="Orientation">Box-mode: perpendicular (по умолчанию) или horizontal.</param>
/// <param name="Segments">Внутренняя разбивка ребра на участки по длине (Фаза 2):
/// каждый — своё H/T, с авто-скосом высоты на переходах. null/пусто → однородное
/// ребро (Height/Thickness). Длина последнего участка = остаток run ребра.</param>
public readonly record struct WebLongRibSpec(
    string Side,
    bool BothSides,
    double Offset,
    double Start,
    double Length,
    double Height,
    double Thickness,
    RibOrientation Orientation = RibOrientation.Perpendicular,
    IReadOnlyList<RibSegment>? Segments = null);
