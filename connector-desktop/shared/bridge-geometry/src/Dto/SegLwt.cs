namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Сегмент пояса или стенки, описанный (Length, Width, Thickness) вдоль оси
/// балки. Исходный формат в плагин-payload (tb_flange, tb_web, tb_topData) —
/// "L,W,T|L,W,T|..." Все размеры в мм.
/// </summary>
public readonly record struct SegLwt(double L, double W, double T);
